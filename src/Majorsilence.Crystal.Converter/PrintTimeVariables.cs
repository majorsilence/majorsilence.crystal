using System.Text.RegularExpressions;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Converter;

/// <summary>
/// Crystal's commonest use of a variable, written as the aggregate it amounts to (#19).
///
/// A variable that is not Local keeps its value from one evaluation to the next, and
/// reports use that to total or list a group's records in three formulas: one in the
/// group header sets the variable to a constant, one in the details adds each record's
/// piece to it ("x := x + {T.Item} + chr(10)", usually in a suppressed section), and one
/// in the group footer shows it ("trim(x)"). An RDL expression has no variables, so all
/// three degraded to an empty value, and the footer printed nothing.
///
/// Between the reset and the footer, the details formula has run once for each of the
/// group's records, in order, so the footer's value is an aggregate of the piece over the
/// group's rows: Join(Aggregate(piece), "") for a string, Sum(piece) for a number. That is
/// what the footer formula is written as, with the variable replaced by its aggregate. It
/// goes on the footer's own textbox rather than the calculated field, because this engine
/// evaluates a calculated field per row with no group to aggregate over, and an aggregate
/// written in a group footer with no scope aggregates over that group.
///
/// Only the exact shape is rewritten; anything else keeps the empty value it had:
/// - the footer formula reads its variables and writes none, and is placed in one group's
///   footer only, or the report footer only;
/// - each variable is written only by resets (a constant, optionally under
///   "if not InRepeatedGroupHeader"), placed in that group's header (the report header for
///   a report-footer display), and by accumulations ("v := v + piece", optionally under
///   "if condition then"), each placed once and in details sections only;
/// - no formula is referenced by another formula, so none runs anywhere else.
/// A formula that is placed nowhere does not run in Crystal, so it is ignored here too.
/// </summary>
internal static class PrintTimeVariables
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Declaration = new(
        @"^(?:(Local|Global|Shared)\s+)?(Number|Currency|String|Boolean|Date|DateTime|Time)Var(\s+Array)?\s+([A-Za-z_]\w*)\s*(?::=\s*(.*))?$",
        Ci | RegexOptions.Singleline);

    private static readonly Regex EvaluationTime = new(
        @"^(WhilePrintingRecords|WhileReadingRecords|BeforeReadingRecords)$", Ci);

    private static readonly Regex Constant = new(
        @"^(-?\d+(\.\d+)?|""[^""]*""|'[^']*'|True|False)$", Ci);

    private enum Kind { Reset, Accumulate, Read, Unsupported }

    private sealed record Use(Kind Kind, string? Piece = null, string? Condition = null);

    private sealed record Formula(FormulaField Field, string Name, List<string> Statements,
        Dictionary<string, bool> Declared);   // variable -> declared numeric

    /// <summary>
    /// The footer expression for each formula that fits, keyed by the formula's name as a
    /// placed field names it (no "@"). <paramref name="transpile"/> turns Crystal formula text
    /// into an RDL expression with its leading "=", or null when it cannot be used.
    /// </summary>
    internal static Dictionary<string, string> FooterExpressions(ReportDefinition report,
        Func<string, string?> transpile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formulas = new List<Formula>();
        // Variables something this does not read could also write: a formula it cannot
        // follow, a Basic-syntax one, or a subreport sharing the variable.
        var untracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in report.Fields.OfType<FormulaField>())
        {
            // A Basic-syntax formula can carry a Crystal-syntax label (the dialect flag is
            // not decoded), and its declarations look nothing like Crystal's, so they are
            // looked for in every formula.
            untracked.UnionWith(DeclaredInBasic(f.FormulaText, sharedOnly: false));
            if (f.Syntax == FormulaSyntax.Crystal && Parse(f) is Formula parsed)
            {
                if (parsed.Declared.Count > 0) formulas.Add(parsed);
                continue;
            }
            untracked.UnionWith(DeclaredInCrystal(f.FormulaText, sharedOnly: false));
        }
        foreach (var sub in Subreports(report))
            foreach (var f in sub.Fields.OfType<FormulaField>())
                untracked.UnionWith(DeclaredInCrystal(f.FormulaText, sharedOnly: true)
                    .Concat(DeclaredInBasic(f.FormulaText, sharedOnly: true)));
        if (formulas.Count == 0) return result;

        var placements = Placements(report);
        var referencedByFormula = ReferencedByAnotherFormula(report);

        foreach (var display in formulas)
        {
            if (referencedByFormula.Contains(display.Name)) continue;
            if (!placements.TryGetValue(display.Name, out var shownAt) || shownAt.Count == 0) continue;
            if (shownAt.Any(p => p.Text)) continue;
            // One footer: a group's, all at one level, or the report's.
            var types = shownAt.Select(p => p.Type).Distinct().ToList();
            if (types.Count != 1 || types[0] is not (SectionType.GroupFooter or SectionType.ReportFooter)) continue;
            bool reportFooter = types[0] == SectionType.ReportFooter;
            var levels = shownAt.Select(p => p.Level).Distinct().ToList();
            if (!reportFooter && levels.Count != 1) continue;
            int level = levels[0];

            var vars = display.Declared.Keys.ToList();
            if (vars.Any(v => UseOf(display, v).Kind != Kind.Read)) continue;
            if (FinalExpression(display) is not string shown) continue;

            if (vars.Any(untracked.Contains)) continue;
            var aggregates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in vars)
            {
                if (Aggregate(v, display.Declared[v], formulas, display, placements, referencedByFormula,
                        reportFooter, level, transpile) is not string aggregate)
                { aggregates.Clear(); break; }
                aggregates[v] = aggregate;
            }
            if (aggregates.Count != vars.Count) continue;

            // The footer formula's own expression, with each variable standing in as a
            // placeholder formula reference the transpiler leaves as a field.
            string text = shown;
            foreach (var v in vars)
                text = ReplaceVariable(text, v, "{@" + Placeholder(v) + "}");
            if (transpile(text) is not string expr) continue;
            foreach (var v in vars)
                expr = expr.Replace($"Fields!{Placeholder(v)}.Value", "(" + aggregates[v] + ")", StringComparison.OrdinalIgnoreCase);
            if (expr.Contains("Fields!" + PlaceholderPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            result[display.Name] = expr;
        }
        return result;
    }

    private const string PlaceholderPrefix = "CrystalVariable_";
    private static string Placeholder(string variable) => PlaceholderPrefix + variable;

    /// <summary>An expression with each variable placeholder taken out, for checking the
    /// fields it refers to.</summary>
    internal static string WithoutPlaceholders(string expr) =>
        Regex.Replace(expr, @"Fields!" + PlaceholderPrefix + @"\w+\.Value", "\"\"", Ci);

    // The aggregate a footer at this place sees for variable v, or null when its writers do
    // not have the shape this handles.
    private static string? Aggregate(string v, bool numeric, List<Formula> formulas, Formula display,
        Dictionary<string, List<Placement>> placements, HashSet<string> referencedByFormula,
        bool reportFooter, int level, Func<string, string?> transpile)
    {
        var pieces = new List<string>();
        foreach (var f in formulas)
        {
            if (f == display || !f.Declared.ContainsKey(v)) continue;
            var use = UseOf(f, v);
            if (use.Kind == Kind.Read) continue;
            if (use.Kind == Kind.Unsupported) return null;
            var at = placements.TryGetValue(f.Name, out var p) ? p : [];
            if (at.Count == 0) continue;   // placed nowhere: Crystal never evaluates it
            if (referencedByFormula.Contains(f.Name) || at.Any(x => x.Text)) return null;
            if (f.Declared[v] != numeric) return null;
            if (use.Kind == Kind.Reset)
            {
                bool inItsHeader = reportFooter
                    ? at.All(x => x.Type == SectionType.ReportHeader)
                    : at.All(x => x.Type == SectionType.GroupHeader && x.Level == level);
                if (!inItsHeader) return null;
                continue;
            }
            // An accumulation runs once per record only where it is placed once, in details.
            if (at.Count != 1 || at[0].Type != SectionType.Details) return null;
            string pieceText = use.Condition is null
                ? use.Piece!
                : $"if {use.Condition} then ({use.Piece}) else {(numeric ? "0" : "\"\"")}";
            if (transpile(pieceText) is not string piece || piece == "=\"\"" || piece == "=0") return null;
            pieces.Add(piece[1..]);
        }
        // A group footer's value starts from its group header's reset; without one it would
        // carry over from the groups before, which is a running value, not this aggregate.
        if (pieces.Count == 0) return null;
        if (!reportFooter && !formulas.Any(f => f != display && f.Declared.ContainsKey(v)
                && UseOf(f, v).Kind == Kind.Reset && placements.TryGetValue(f.Name, out var at) && at.Count > 0))
            return null;
        if (numeric)
            return string.Join(" + ", pieces.Select(p => $"Sum({p})"));
        // Several string accumulations interleave record by record in an order that depends
        // on where each sits; only a single one is certain.
        return pieces.Count == 1 ? $"Join(Aggregate({pieces[0]}), \"\")" : null;
    }

    private static Formula? Parse(FormulaField f)
    {
        string body = Regex.Replace(f.FormulaText, @"//[^\r\n]*", "");
        var statements = SplitStatements(body);
        if (statements is null) return null;
        statements.RemoveAll(s => EvaluationTime.IsMatch(s));
        var declared = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool anyLocal = false;
        foreach (var s in statements)
        {
            var d = Declaration.Match(s);
            if (!d.Success) continue;
            string type = d.Groups[2].Value;
            // A Local variable starts afresh at every evaluation: nothing carries over.
            if (d.Groups[1].Value.Equals("Local", StringComparison.OrdinalIgnoreCase)) { anyLocal = true; continue; }
            if (d.Groups[3].Success && d.Groups[3].Length > 0) return null;   // arrays
            if (type.Equals("Number", StringComparison.OrdinalIgnoreCase) || type.Equals("Currency", StringComparison.OrdinalIgnoreCase))
                declared[d.Groups[4].Value] = true;
            else if (type.Equals("String", StringComparison.OrdinalIgnoreCase))
                declared[d.Groups[4].Value] = false;
            else
                return null;
        }
        // Local and carried variables together is a formula this does not follow.
        if (anyLocal && declared.Count > 0) return null;
        return new Formula(f, Name(f), statements, declared);
    }

    // Crystal syntax: "Global NumberVar total", "Shared StringVar list", "stringvar x".
    private static IEnumerable<string> DeclaredInCrystal(string text, bool sharedOnly)
    {
        string body = Regex.Replace(text, @"//[^\r\n]*", "");
        foreach (Match m in Regex.Matches(body, @"(?:\b(Local|Global|Shared)\s+)?\b(?:Number|Currency|String|Boolean|Date|DateTime|Time)Var(?:\s+Array)?\s+([A-Za-z_]\w*)", Ci))
            if (sharedOnly ? m.Groups[1].Value.Equals("Shared", StringComparison.OrdinalIgnoreCase)
                           : !m.Groups[1].Value.Equals("Local", StringComparison.OrdinalIgnoreCase))
                yield return m.Groups[2].Value;
    }

    // Basic syntax: "Shared total As Number", "Global x As String", "Dim x As String".
    private static IEnumerable<string> DeclaredInBasic(string text, bool sharedOnly)
    {
        foreach (Match m in Regex.Matches(text, @"\b(Shared|Global|Dim)\s+([A-Za-z_]\w*)\s*(?:\(\s*\))?\s+As\b", Ci))
            if (!sharedOnly || m.Groups[1].Value.Equals("Shared", StringComparison.OrdinalIgnoreCase))
                yield return m.Groups[2].Value;
    }

    private static IEnumerable<ReportDefinition> Subreports(ReportDefinition report)
    {
        foreach (var s in report.Sections)
            foreach (var sub in s.Objects.OfType<SubreportObject>())
                if (sub.Report is not null)
                {
                    yield return sub.Report;
                    foreach (var nested in Subreports(sub.Report)) yield return nested;
                }
    }

    private static string Name(FormulaField f) => f.Name.TrimStart('@');

    // What formula f does with variable v.
    private static Use UseOf(Formula f, string v)
    {
        string V = Regex.Escape(v);
        var rest = new List<string>();
        foreach (var s in f.Statements)
        {
            var d = Declaration.Match(s);
            if (d.Success)
            {
                // A declaration with an initializer is an assignment to that variable.
                if (d.Groups[5].Success)
                    rest.Add(d.Groups[4].Value + " := " + d.Groups[5].Value);
                continue;
            }
            rest.Add(s);
        }
        if (rest.Count == 0) return new Use(Kind.Read);

        var writes = rest.Where(s => Regex.IsMatch(s, $@"(?<![\w.{{@]){V}\s*:=", Ci)).ToList();
        if (writes.Count == 0)
        {
            // Read only: the formula's value is its last statement, the others must be inert.
            return rest.Count == 1 ? new Use(Kind.Read) : new Use(Kind.Unsupported);
        }
        if (rest.Count != 1 || writes.Count != 1) return new Use(Kind.Unsupported);

        // "if condition then body", or the body alone; the body may be wrapped in brackets.
        string statement = Unwrap(writes[0]);
        string? condition = null;
        var conditional = Regex.Match(statement, @"^if\s+(.+?)\s+then\s+(.+)$", Ci | RegexOptions.Singleline);
        if (conditional.Success)
        {
            condition = conditional.Groups[1].Value.Trim();
            statement = Unwrap(conditional.Groups[2].Value);
            if (Regex.IsMatch(statement, @"\belse\b", Ci)) return new Use(Kind.Unsupported);
        }
        var assignment = Regex.Match(statement, $@"^{V}\s*:=\s*(.+)$", Ci | RegexOptions.Singleline);
        if (!assignment.Success) return new Use(Kind.Unsupported);
        string value = assignment.Groups[1].Value.Trim();

        bool onlyInNewGroup = condition is not null && Regex.IsMatch(condition, @"^not\s+InRepeatedGroupHeader$", Ci);
        if (Constant.IsMatch(value) && (condition is null || onlyInNewGroup))
            return new Use(Kind.Reset);

        var accumulation = Regex.Match(value, $@"^{V}\s*\+\s*(.+)$", Ci | RegexOptions.Singleline);
        if (!accumulation.Success) return new Use(Kind.Unsupported);
        string piece = accumulation.Groups[1].Value.Trim();
        // The piece and condition must be plain expressions: no variable of any kind.
        if (MentionsVariable(piece, f) || (condition is not null && MentionsVariable(condition, f)))
            return new Use(Kind.Unsupported);
        if (!Balanced(piece) || (condition is not null && !Balanced(condition)))
            return new Use(Kind.Unsupported);
        return new Use(Kind.Accumulate, piece, condition);
    }

    private static string? FinalExpression(Formula f)
    {
        var rest = f.Statements.Where(s => !Declaration.IsMatch(s)).ToList();
        return rest.Count == 1 ? rest[0] : null;
    }

    private static bool MentionsVariable(string text, Formula f) =>
        f.Declared.Keys.Any(v => Regex.IsMatch(StripStrings(text), $@"(?<![\w.{{@]){Regex.Escape(v)}\b", Ci))
        || Regex.IsMatch(text, @"\b(Number|Currency|String|Boolean|Date|DateTime|Time)Var\b|:=", Ci);

    private static string ReplaceVariable(string text, string v, string with)
    {
        // Outside string literals and braces only.
        return Regex.Replace(text, @"""[^""]*""|'[^']*'|\{[^}]*\}|(?<![\w.])" + Regex.Escape(v) + @"\b",
            m => m.Value.StartsWith('"') || m.Value.StartsWith('\'') || m.Value.StartsWith('{') ? m.Value : with, Ci);
    }

    private static string StripStrings(string text) => Regex.Replace(text, @"""[^""]*""|'[^']*'|\{[^}]*\}", "\"\"");

    // "( x := x + 1; )" and "x := x + 1;" alike: trailing semicolons, and brackets
    // around the whole statement.
    private static string Unwrap(string s)
    {
        while (true)
        {
            s = s.Trim();
            while (s.EndsWith(';')) s = s[..^1].TrimEnd();
            if (s.Length < 2 || s[0] != '(' || ClosingBracket(s) != s.Length - 1) return s;
            s = s[1..^1];
        }
    }

    // Where the bracket opening s closes, outside string literals; -1 when it does not.
    private static int ClosingBracket(string s)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool Balanced(string s)
    {
        int depth = 0;
        foreach (char c in StripStrings(s))
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    // Top-level statements, split on ";" outside brackets and string literals; null when
    // the brackets do not balance.
    private static List<string>? SplitStatements(string body)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (--depth < 0) return null; }
            else if (c == ';' && depth == 0) { list.Add(body[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0 || quote != '\0') return null;
        list.Add(body[start..].Trim());
        return list.Where(s => s.Length > 0).ToList();
    }

    private sealed record Placement(SectionType Type, int Level, bool Text);

    // Where each formula is placed: as a field object, or inside a text object's text.
    private static Dictionary<string, List<Placement>> Placements(ReportDefinition report)
    {
        var map = new Dictionary<string, List<Placement>>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, Placement p)
        {
            if (!map.TryGetValue(name, out var list)) map[name] = list = [];
            list.Add(p);
        }
        foreach (var s in report.Sections)
        foreach (var o in s.Objects)
        {
            if (o is FieldObject fo && fo.FieldName.TrimStart('{').StartsWith('@'))
                Add(fo.FieldName.Trim('{', '}').TrimStart('@'), new Placement(s.Type, s.GroupLevel, false));
            else if (o is TextObject t)
                foreach (Match m in Regex.Matches(t.Text ?? "", @"\{@([^}]+)\}"))
                    Add(m.Groups[1].Value, new Placement(s.Type, s.GroupLevel, true));
        }
        return map;
    }

    // Formulas another formula or a section's formula refers to, which would run there too.
    private static HashSet<string> ReferencedByAnotherFormula(ReportDefinition report)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var texts = report.Fields.OfType<FormulaField>().Select(f => f.FormulaText)
            .Concat(report.Sections.Select(s => s.SuppressFormula ?? ""))
            .Append(report.RecordSelectionFormula ?? "")
            .Append(report.GroupSelectionFormula ?? "");
        foreach (var t in texts)
            foreach (Match m in Regex.Matches(t, @"\{@([^}]+)\}"))
                set.Add(m.Groups[1].Value);
        return set;
    }
}
