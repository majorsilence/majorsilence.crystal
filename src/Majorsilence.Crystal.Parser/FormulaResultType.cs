namespace Majorsilence.Crystal.Parser;

/// <summary>
/// Whether a Crystal-syntax formula certainly returns a number, decided from its text.
///
/// The file does not record a formula's result type: the formula's own record holds its
/// name, text and dependencies, and the field object that shows it carries a reference to
/// the formula but not the type of its value. Without one, a formula object's numeric
/// format could not be applied, because every field object carries a numeric record,
/// including the ones showing strings, where it holds whatever the object was last
/// defaulted to.
///
/// So this recognises only the shapes whose type is not in doubt, and says "not known" for
/// everything else, which leaves those objects exactly as they were:
/// <list type="bullet">
///   <item>a number literal;</item>
///   <item>a database field whose column is numeric, or another formula that is numeric
///   by these same rules;</item>
///   <item>a call to a function that returns a number whatever it is given -
///   <c>CCur</c>, <c>CDbl</c>, <c>ToNumber</c>, <c>Round</c>, <c>Sum</c>, <c>Count</c> and
///   the rest of <see cref="NumericFunctions"/>. Functions whose result follows their
///   argument's type, such as <c>Maximum</c> or <c>IIf</c>, are not on it;</item>
///   <item>arithmetic joining operands that are all numeric. That includes <c>+</c>,
///   which concatenates only when a string is involved;</item>
///   <item>a unary minus or parentheses around any of these.</item>
/// </list>
/// A string literal, a keyword, a comment, <c>If</c>, or anything it does not recognise
/// makes the answer "not known".
/// </summary>
internal static class FormulaResultType
{
    internal static readonly HashSet<string> NumericFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Conversions: the result is a number whatever the argument was.
        "CCur", "CDbl", "CInt", "CLng", "ToNumber", "Val",
        // Arithmetic.
        "Round", "Truncate", "Abs", "Int", "Fix", "Sgn", "Sqr", "Exp", "Log", "Remainder",
        "Sin", "Cos", "Tan", "Atn",
        // Summaries that are numbers whatever they summarise.
        "Sum", "Average", "Count", "DistinctCount", "StdDev", "PopulationStdDev",
        "Variance", "PopulationVariance",
    };

    /// <param name="text">The formula's text.</param>
    /// <param name="isNumericField">Whether a <c>{Table.Field}</c> reference (given as the
    /// text between the braces) names a numeric column.</param>
    /// <param name="isNumericFormula">Whether a <c>{@Name}</c> reference (given without the
    /// <c>@</c>) names a formula that is itself numeric.</param>
    internal static bool IsNumeric(string text, Func<string, bool> isNumericField,
        Func<string, bool> isNumericFormula)
    {
        var tokens = Tokenize(text);
        if (tokens is null || tokens.Count == 0) return false;
        int pos = 0;
        return Expression(tokens, ref pos, isNumericField, isNumericFormula) && pos == tokens.Count;
    }

    private enum Kind { Number, Field, Name, Open, Close, Comma, Operator, Minus, Text }

    private readonly record struct Token(Kind Kind, string Value);

    private static bool Expression(List<Token> t, ref int pos, Func<string, bool> field, Func<string, bool> formula)
    {
        if (!Operand(t, ref pos, field, formula)) return false;
        while (pos < t.Count && (t[pos].Kind is Kind.Operator or Kind.Minus))
        {
            pos++;
            if (!Operand(t, ref pos, field, formula)) return false;
        }
        return true;
    }

    private static bool Operand(List<Token> t, ref int pos, Func<string, bool> field, Func<string, bool> formula)
    {
        if (pos >= t.Count) return false;
        var tok = t[pos];
        switch (tok.Kind)
        {
            case Kind.Minus:
                pos++;
                return Operand(t, ref pos, field, formula);
            case Kind.Number:
                pos++;
                return true;
            case Kind.Field:
                pos++;
                return tok.Value.StartsWith('@')
                    ? formula(tok.Value[1..])
                    : field(tok.Value);
            case Kind.Open:
                pos++;
                if (!Expression(t, ref pos, field, formula)) return false;
                if (pos >= t.Count || t[pos].Kind != Kind.Close) return false;
                pos++;
                return true;
            case Kind.Name:
                // Only a call, and only to a function whose result is always a number. Its
                // arguments do not decide the type, so they are skipped rather than checked -
                // but they must be balanced, or the formula is not one this understands.
                if (!NumericFunctions.Contains(tok.Value)) return false;
                if (pos + 1 >= t.Count || t[pos + 1].Kind != Kind.Open) return false;
                pos += 2;
                for (int depth = 1; pos < t.Count; pos++)
                {
                    if (t[pos].Kind == Kind.Open) depth++;
                    else if (t[pos].Kind == Kind.Close && --depth == 0) { pos++; return true; }
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>The formula's tokens, or null when it holds anything this does not model.</summary>
    private static List<Token>? Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end < 0) return null;
                tokens.Add(new Token(Kind.Field, text[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, text[start..i]));
                continue;
            }
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text[start..i];
                // Crystal's word operators. Every other word is a name, and a name is only
                // acceptable as a call to one of the numeric functions.
                tokens.Add(word.Equals("mod", StringComparison.OrdinalIgnoreCase)
                    ? new Token(Kind.Operator, word)
                    : new Token(Kind.Name, word));
                continue;
            }
            switch (c)
            {
                case '(': tokens.Add(new Token(Kind.Open, "(")); break;
                case ')': tokens.Add(new Token(Kind.Close, ")")); break;
                case ',': tokens.Add(new Token(Kind.Comma, ",")); break;
                case '-': tokens.Add(new Token(Kind.Minus, "-")); break;
                case '+': case '*': case '/': case '^': case '\\':
                    // Two slashes open a comment, which could say anything.
                    if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return null;
                    tokens.Add(new Token(Kind.Operator, c.ToString()));
                    break;
                case '"': case '\'':
                {
                    // A string anywhere outside a numeric function's arguments makes the
                    // formula not known; inside them it is skipped over like any argument.
                    int end = text.IndexOf(c, i + 1);
                    if (end < 0) return null;
                    tokens.Add(new Token(Kind.Text, text[i..(end + 1)]));
                    i = end + 1;
                    continue;
                }
                default:
                    return null;
            }
            i++;
        }
        return tokens;
    }
}
