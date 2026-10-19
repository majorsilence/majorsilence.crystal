using System.Text;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// A Crystal-syntax formula written as statements, reduced to the single expression it
/// computes, for a formula that depends only on the current record (#19).
///
/// An RDL expression has no statements and no variables, so a formula declaring one used to
/// degrade to an empty value. But most such formulas only use their variables as working
/// space: an address built up line by line, a description assembled from optional parts.
/// Following the statements in order with each variable held as the expression it stands
/// for, every assignment replaces that expression, and an "if" that assigns in one branch and
/// not the other leaves "if condition then (new) else (old)". What the last statement leaves
/// is the formula's value, an ordinary expression the transpiler already handles.
///
/// It gives up, and the formula keeps its empty value, when the formula:
/// - reads a variable that is not Local before writing it, the value then being whatever an
///   earlier evaluation left (see PrintTimeVariables for the case of that which is handled);
/// - loops (For, While, Do), uses arrays, or uses Select as a statement;
/// - uses a Date, DateTime or Time variable before assigning it (their defaults have no
///   expression here);
/// - ends in an "if" with no else, or grows past a size worth emitting.
///
/// A Local variable starts at its type's default at every evaluation, as Crystal documents:
/// 0, "" or False. Assignments to Global or Shared variables are followed for this formula's
/// own value only; what they leave for other formulas is not this formula's to express.
/// </summary>
internal static class CrystalStatements
{
    private const int MaxLength = 8000;

    /// <summary>The formula as one expression with no variables, or null.</summary>
    internal static string? Inline(string formula)
    {
        try
        {
            if (Tokenize(formula) is not List<Token> tokens) return null;
            if (!tokens.Any(t => t.Kind == Kind.Ident && IsVarType(t.Text)) && !tokens.Any(t => t.Text == ":="))
                return null;
            var parser = new Parser(tokens);
            var block = parser.ParseBlock(endAtCloseParen: false);
            if (block is null || !parser.AtEnd) return null;
            var env = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
            _declared = DeclaredNames(tokens);
            string? value = Run(block, env);
            if (value is null || value.Length > MaxLength) return null;
            return value;
        }
        catch (GiveUp)
        {
            return null;
        }
    }

    private sealed class GiveUp : Exception;

    // Every variable the formula declares anywhere, so that one read before its declaration
    // is not mistaken for a function name and passed through.
    [ThreadStatic] private static HashSet<string>? _declared;

    private static HashSet<string> DeclaredNames(List<Token> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Ident || !IsVarType(tokens[i].Text)) continue;
            int n = tokens[i + 1].Text.Equals("Array", StringComparison.OrdinalIgnoreCase) ? i + 2 : i + 1;
            if (n < tokens.Count && tokens[n].Kind == Kind.Ident) names.Add(tokens[n].Text);
        }
        return names;
    }

    // ── Variables ────────────────────────────────────────────────────────────────────

    private sealed record Variable(string Type, bool Local, string? Value);

    private static readonly string[] VarTypes =
        ["NumberVar", "CurrencyVar", "StringVar", "BooleanVar", "DateVar", "DateTimeVar", "TimeVar"];

    private static bool IsVarType(string word) => VarTypes.Any(t => t.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static string? Default(string type) => type.ToLowerInvariant() switch
    {
        "numbervar" or "currencyvar" => "0",
        "stringvar" => "\"\"",
        "booleanvar" => "False",
        _ => null,
    };

    // ── Statements ───────────────────────────────────────────────────────────────────

    private abstract record Stmt;
    private sealed record Declare(string Scope, string Type, string Name, List<Token>? Init) : Stmt;
    private sealed record Assign(string Name, List<Token> Value) : Stmt;
    private sealed record If(List<Token> Condition, List<Stmt> Then, List<Stmt>? Else) : Stmt;
    private sealed record Expr(List<Token> Value) : Stmt;
    private sealed record EvaluationTime : Stmt;

    // The block's value is its last statement's; null when that has none.
    private static string? Run(List<Stmt> block, Dictionary<string, Variable> env)
    {
        string? value = null;
        foreach (var stmt in block)
            if (stmt is not EvaluationTime)
                value = Run(stmt, env);
        return value;
    }

    private static string? Run(Stmt stmt, Dictionary<string, Variable> env)
    {
        switch (stmt)
        {
            case Declare d:
            {
                if (env.TryGetValue(d.Name, out var existing))
                {
                    // Declared again (Crystal allows it): same variable, same value.
                    if (d.Init is null) return existing.Value ?? (existing.Local ? Default(existing.Type) : null);
                    string v2 = Substitute(d.Init, env);
                    env[d.Name] = existing with { Value = v2 };
                    return v2;
                }
                bool local = d.Scope.Equals("Local", StringComparison.OrdinalIgnoreCase);
                // Declared before its initializer is read, so "x := x + 1" reads x itself: a
                // Local's default, or a carried variable's earlier value, which gives up.
                env[d.Name] = new Variable(d.Type, local, local ? Default(d.Type) : null);
                if (d.Init is null) return env[d.Name].Value;
                string v = Substitute(d.Init, env);
                env[d.Name] = env[d.Name] with { Value = v };
                return v;
            }
            case Assign a:
            {
                if (!env.TryGetValue(a.Name, out var variable)) throw new GiveUp();
                string v = Substitute(a.Value, env);
                env[a.Name] = variable with { Value = v };
                return v;
            }
            case If i:
            {
                string condition = Substitute(i.Condition, env);
                var thenEnv = new Dictionary<string, Variable>(env, StringComparer.OrdinalIgnoreCase);
                var elseEnv = new Dictionary<string, Variable>(env, StringComparer.OrdinalIgnoreCase);
                string? thenValue = Run(i.Then, thenEnv);
                string? elseValue = i.Else is null ? null : Run(i.Else, elseEnv);
                // A variable first declared inside a branch has no value on the other path.
                if (thenEnv.Keys.Concat(elseEnv.Keys).Any(k => !env.ContainsKey(k))) throw new GiveUp();
                foreach (var name in env.Keys.ToList())
                {
                    string? a = thenEnv[name].Value, b = elseEnv[name].Value;
                    if (a == b) { env[name] = thenEnv[name]; continue; }
                    env[name] = env[name] with { Value = a is null || b is null ? null : $"(if {condition} then ({a}) else ({b}))" };
                }
                return thenValue is null || elseValue is null ? null : $"(if {condition} then ({thenValue}) else ({elseValue}))";
            }
            case Expr e:
                return Substitute(e.Value, env);
        }
        throw new GiveUp();
    }

    // The expression with every variable replaced by what it holds.
    private static string Substitute(List<Token> tokens, Dictionary<string, Variable> env)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == Kind.Ident && IsVarType(t.Text)) throw new GiveUp();
            if (t.Text == ":=") throw new GiveUp();
            // A name after a dot is a member, not a variable.
            bool afterDot = i > 0 && tokens[i - 1].Text == ".";
            if (t.Kind == Kind.Ident && !afterDot && env.TryGetValue(t.Text, out var v))
            {
                // A carried variable read before this formula wrote it: an earlier
                // evaluation's value, which no expression here can know.
                if (v.Value is null) throw new GiveUp();
                sb.Append('(').Append(v.Value).Append(')');
            }
            else if (t.Kind == Kind.Ident && !afterDot && _declared is not null && _declared.Contains(t.Text))
            {
                throw new GiveUp();   // read before it is declared
            }
            else
            {
                sb.Append(t.Text);
            }
            sb.Append(' ');
            if (sb.Length > MaxLength) throw new GiveUp();
        }
        return sb.ToString().Trim();
    }

    // ── Parsing ──────────────────────────────────────────────────────────────────────

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        public bool AtEnd => _pos >= tokens.Count;

        private Token? Peek(int ahead = 0) => _pos + ahead < tokens.Count ? tokens[_pos + ahead] : null;

        private bool IsWord(Token? t, string word) =>
            t is { Kind: Kind.Ident } && t.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

        // Statements separated by ";", up to the end or, inside brackets, the closing one.
        public List<Stmt>? ParseBlock(bool endAtCloseParen)
        {
            var block = new List<Stmt>();
            while (!AtEnd)
            {
                if (Peek()!.Text == ";") { _pos++; continue; }
                if (endAtCloseParen && Peek()!.Text == ")") break;
                var stmt = ParseStatement(stopAtElse: false);
                if (stmt is null) return null;
                block.Add(stmt);
                if (AtEnd) break;
                if (Peek()!.Text == ";") { _pos++; continue; }
                if (endAtCloseParen && Peek()!.Text == ")") break;
                return null;
            }
            return block;
        }

        private Stmt? ParseStatement(bool stopAtElse)
        {
            var t = Peek();
            if (t is null) return null;
            foreach (var word in new[] { "For", "While", "Do", "ReDim", "Exit" })
                if (IsWord(t, word)) throw new GiveUp();
            // When to evaluate, not what: no value of its own.
            if (IsWord(t, "WhilePrintingRecords") || IsWord(t, "WhileReadingRecords") || IsWord(t, "BeforeReadingRecords"))
            {
                _pos++;
                return AtEnd || Peek()!.Text == ";" ? new EvaluationTime() : null;
            }

            // [Local|Global|Shared] TypeVar [Array] name [:= value]
            int p = 0;
            string scope = "Global";
            if (IsWord(Peek(), "Local") || IsWord(Peek(), "Global") || IsWord(Peek(), "Shared"))
            {
                scope = Peek()!.Text;
                p = 1;
            }
            if (Peek(p) is { Kind: Kind.Ident } typeToken && IsVarType(typeToken.Text))
            {
                if (IsWord(Peek(p + 1), "Array")) throw new GiveUp();
                if (Peek(p + 1) is not { Kind: Kind.Ident } nameToken) return null;
                _pos += p + 2;
                List<Token>? init = null;
                if (Peek()?.Text == ":=")
                {
                    _pos++;
                    init = ReadExpression(stopAtElse);
                }
                return new Declare(scope, typeToken.Text, nameToken.Text, init);
            }

            if (IsWord(t, "If") && ConditionalStatement(stopAtElse) is Stmt conditional)
                return conditional;

            // name := value
            if (t.Kind == Kind.Ident && Peek(1)?.Text == ":=")
            {
                _pos += 2;
                return new Assign(t.Text, ReadExpression(stopAtElse));
            }

            if (IsWord(t, "Select")) throw new GiveUp();

            var expr = ReadExpression(stopAtElse);
            return expr.Count == 0 ? null : new Expr(expr);
        }

        // "if c then S [else S]" as a statement. An if whose branches hold no assignment is
        // an expression, and is read as one.
        private Stmt? ConditionalStatement(bool stopAtElse)
        {
            int start = _pos;
            _pos++;
            var condition = ReadUntilWord("Then");
            if (condition is null) { _pos = start; return null; }
            _pos++; // Then
            var thenBlock = Branch(out bool thenAssigns);
            if (thenBlock is null) { _pos = start; return null; }
            List<Stmt>? elseBlock = null;
            bool elseAssigns = false;
            if (IsWord(Peek(), "Else"))
            {
                _pos++;
                elseBlock = Branch(out elseAssigns);
                if (elseBlock is null) { _pos = start; return null; }
            }
            if (!thenAssigns && !elseAssigns)
            {
                _pos = start;
                var expr = ReadExpression(stopAtElse);
                return expr.Count == 0 ? null : new Expr(expr);
            }
            return new If(condition, thenBlock, elseBlock);
        }

        // A branch: a bracketed block of statements, or one statement.
        private List<Stmt>? Branch(out bool assigns)
        {
            assigns = false;
            if (Peek()?.Text == "(" && BracketHoldsStatements())
            {
                _pos++;
                var block = ParseBlock(endAtCloseParen: true);
                if (block is null || Peek()?.Text != ")") return null;
                _pos++;
                assigns = Assigns(block);
                return block;
            }
            var stmt = ParseStatement(stopAtElse: true);
            if (stmt is null) return null;
            assigns = Assigns([stmt]);
            return [stmt];
        }

        private static bool Assigns(List<Stmt> block) =>
            block.Any(s => s is Assign or Declare || (s is If i && (Assigns(i.Then) || (i.Else is not null && Assigns(i.Else)))));

        // Whether the bracket opening here holds statements (";" or ":=" at its own level)
        // rather than being a grouped expression.
        private bool BracketHoldsStatements()
        {
            int depth = 0;
            for (int i = _pos; i < tokens.Count; i++)
            {
                string s = tokens[i].Text;
                if (s is "(" or "[") depth++;
                else if (s is ")" or "]") { if (--depth == 0) return false; }
                else if (depth == 1 && (s == ";" || s == ":=" || (tokens[i].Kind == Kind.Ident && IsVarType(s))))
                    return true;
            }
            return false;
        }

        private List<Token>? ReadUntilWord(string word)
        {
            var list = new List<Token>();
            int depth = 0, ifs = 0;
            while (!AtEnd)
            {
                var t = Peek()!;
                if (t.Text is "(" or "[") depth++;
                else if (t.Text is ")" or "]") { if (--depth < 0) return null; }
                else if (depth == 0 && t.Text == ";") return null;
                else if (depth == 0 && IsWord(t, "If")) ifs++;
                else if (depth == 0 && IsWord(t, word))
                {
                    if (ifs == 0) return list;
                    ifs--;
                }
                list.Add(t);
                _pos++;
            }
            return null;
        }

        // An expression: to the next ";" at its level, the block's closing bracket, or an
        // "else" belonging to an enclosing statement.
        private List<Token> ReadExpression(bool stopAtElse)
        {
            var list = new List<Token>();
            int depth = 0, pendingIfs = 0;
            while (!AtEnd)
            {
                var t = Peek()!;
                if (depth == 0 && t.Text == ";") break;
                if (t.Text is "(" or "[") depth++;
                else if (t.Text is ")" or "]") { if (depth == 0) break; depth--; }
                else if (depth == 0 && IsWord(t, "If")) pendingIfs++;
                else if (depth == 0 && IsWord(t, "Else"))
                {
                    if (pendingIfs == 0 && stopAtElse) break;
                    if (pendingIfs > 0) pendingIfs--;
                }
                else if (t.Text == ":=") throw new GiveUp();
                list.Add(t);
                _pos++;
            }
            return list;
        }
    }

    // ── Tokens ───────────────────────────────────────────────────────────────────────

    private enum Kind { Ident, Number, Str, Field, Op }

    private sealed record Token(Kind Kind, string Text);

    // Strings, {field} references and comments kept whole; null on an unterminated one.
    private static List<Token>? Tokenize(string text)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                int start = i++;
                while (true)
                {
                    if (i >= text.Length) return null;
                    if (text[i] == c)
                    {
                        // A doubled quote is a quote inside the string.
                        if (i + 1 < text.Length && text[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                list.Add(new Token(Kind.Str, text[start..i]));
                continue;
            }
            if (c == '{')
            {
                int end = text.IndexOf('}', i);
                if (end < 0) return null;
                list.Add(new Token(Kind.Field, text[i..(end + 1)]));
                i = end + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                list.Add(new Token(Kind.Ident, text[start..i]));
                continue;
            }
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                list.Add(new Token(Kind.Number, text[start..i]));
                continue;
            }
            if (i + 1 < text.Length)
            {
                string two = text.Substring(i, 2);
                if (two is ":=" or "<=" or ">=" or "<>")
                {
                    list.Add(new Token(Kind.Op, two));
                    i += 2;
                    continue;
                }
            }
            list.Add(new Token(Kind.Op, c.ToString()));
            i++;
        }
        return list;
    }
}
