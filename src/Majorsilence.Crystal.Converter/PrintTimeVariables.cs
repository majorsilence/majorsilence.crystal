using System.Text.RegularExpressions;
using Majorsilence.Crystal.Converter.Formula;
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
                // Shown alone, the list is printed and nothing more is made of it.
                bool printedAlone = vars.Count == 1 && Regex.IsMatch(Unwrap(shown),
                    $@"^(?:(?:L|R)?Trim\s*\(\s*{Regex.Escape(v)}\s*\)|{Regex.Escape(v)})$", Ci);
                if (Aggregate(v, display.Declared[v], formulas, display, placements, referencedByFormula,
                        reportFooter, level, transpile, printedAlone) is not string aggregate)
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

    /// <summary>
    /// Formulas that number the records as they print: "WhilePrintingRecords; Global
    /// NumberVar n; n := n + 1", optionally ending "n". Evaluated once for each record, in
    /// details only, with nothing else writing n, the value is the record's position from the
    /// first: RowNumber(Nothing). Crystal evaluates a print-time formula once per record
    /// however many places use it, so a details colour formula reading it ("if {@Line_#} mod 2
    /// <> 0") reads the same number as the field showing it. Written as "" as variables are,
    /// boyum__BOM's line numbers printed nothing and its row colour formula failed.
    /// <paramref name="writers"/> is VariableWriters.Collect over the whole report tree.
    /// </summary>
    internal static HashSet<string> RecordCounters(ReportDefinition report,
        Dictionary<string, HashSet<FormulaField>> writers)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formulas = report.Fields.OfType<FormulaField>().ToList();
        Dictionary<string, List<Placement>>? placements = null;
        foreach (var f in formulas)
        {
            if (f.Syntax != FormulaSyntax.Crystal || Parse(f) is not Formula parsed || parsed.Declared.Count != 1)
                continue;
            var (v, numeric) = parsed.Declared.Single();
            if (!numeric || !writers.TryGetValue(v, out var w) || w.Count != 1 || !w.Contains(f))
                continue;
            var rest = parsed.Statements.Where(s => !Declaration.IsMatch(s)).Select(Unwrap).ToList();
            string V = Regex.Escape(v);
            if (rest.Count is < 1 or > 2 || !Regex.IsMatch(rest[0], $@"^{V}\s*:=\s*{V}\s*\+\s*1$", Ci)
                || rest.Count == 2 && !rest[1].Equals(v, StringComparison.OrdinalIgnoreCase))
                continue;
            placements ??= Placements(report);
            var sites = EvaluatedIn(Name(f), report, formulas, placements, []);
            if (sites is { Count: > 0 } && sites.All(t => t == SectionType.Details))
                result.Add(Name(f));
        }
        return result;
    }

    /// <summary>
    /// What a carried string variable holds when a formula that adds to it starts, keyed by
    /// the formula's name and then the variable's. An address built in a Shared variable
    /// ("Shared Addr As String", then "Addr = Addr + {T.Line1} + Chr(10)" for each line that
    /// is not blank) reads the variable before writing it, and is paired with a formula that
    /// empties it ("Addr = """), placed in a section that prints before it, often a suppressed
    /// page header: Crystal still evaluates a suppressed section's formulas. So each time the
    /// address prints, the variable starts from what that reset left. Read as whatever an
    /// earlier evaluation left, the formula gave up and printed nothing, and a section
    /// suppressed when the address is empty never printed.
    ///
    /// Only that shape is taken: every other formula writing the variable in the whole report
    /// tree is of this report and sets it to one string constant (optionally under "if not
    /// InRepeatedGroupHeader") and nothing else, and is placed or used somewhere; the formula
    /// itself is evaluated somewhere, but not in the details, where it would run once for
    /// each record and carry each record's lines into the next. <paramref name="writers"/> is
    /// VariableWriters.Collect over the whole report tree.
    /// </summary>
    internal static Dictionary<string, Dictionary<string, string>> CarriedStarts(ReportDefinition report,
        Dictionary<string, HashSet<FormulaField>> writers)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var formulas = report.Fields.OfType<FormulaField>().ToList();
        Dictionary<string, List<Placement>>? placements = null;
        HashSet<string>? referenced = null;
        foreach (var (v, w) in writers)
        {
            if (w.Count < 2 || !w.All(formulas.Contains)) continue;
            var resets = w.Select(f => (Field: f, Value: ResetConstant(f, v))).Where(r => r.Value is not null).ToList();
            var others = w.Where(f => resets.All(r => r.Field != f)).ToList();
            if (others.Count != 1 || resets.Count == 0 || resets.Select(r => r.Value).Distinct().Count() != 1
                || resets[0].Value is not ['"', ..] and not ['\'', ..])
                continue;
            placements ??= Placements(report);
            referenced ??= ReferencedByAnotherFormula(report);
            if (!resets.All(r => placements.ContainsKey(Name(r.Field)) || referenced.Contains(Name(r.Field)))) continue;
            var sites = EvaluatedIn(Name(others[0]), report, formulas, placements, []);
            if (sites is not { Count: > 0 } || sites.Contains(SectionType.Details)) continue;
            if (!result.TryGetValue(Name(others[0]), out var starts))
                result[Name(others[0])] = starts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            starts[v] = resets[0].Value!;
        }
        return result;
    }

    // The constant a formula sets variable v to, when that is all it does: any statement
    // other than an evaluation time, a declaration, a bare name or its own result is an
    // assignment of a constant. Null otherwise, or when it does not assign v.
    private static string? ResetConstant(FormulaField f, string v)
    {
        string text = f.FormulaText;
        if (f.Syntax == FormulaSyntax.Basic || FormulaTranspiler.LooksLikeBasicSyntax(text))
        {
            if (BasicStatements.ToCrystal(text, f.ResultType) is not string asCrystal) return null;
            text = asCrystal;
        }
        if (SplitStatements(Regex.Replace(text, @"//[^\r\n]*", "")) is not { } statements) return null;
        string? value = null;
        foreach (var raw in statements)
        {
            string s = Unwrap(raw);
            if (EvaluationTime.IsMatch(s) || Regex.IsMatch(s, @"^[A-Za-z_]\w*$")) continue;
            var d = Declaration.Match(s);
            if (d.Success)
            {
                if (!d.Groups[5].Success) continue;
                s = d.Groups[4].Value + " := " + d.Groups[5].Value;
            }
            var onlyInNewGroup = Regex.Match(s, @"^if\s+not\s+InRepeatedGroupHeader\s+then\s+(.+)$", Ci | RegexOptions.Singleline);
            if (onlyInNewGroup.Success) s = Unwrap(onlyInNewGroup.Groups[1].Value);
            var assignment = Regex.Match(s, @"^([A-Za-z_]\w*)\s*:=\s*(.+)$", Ci | RegexOptions.Singleline);
            if (!assignment.Success) return null;
            if (assignment.Groups[1].Value == BasicStatements.Result) continue;
            string assigned = Unwrap(assignment.Groups[2].Value);
            if (!Constant.IsMatch(assigned)) return null;
            if (!assignment.Groups[1].Value.Equals(v, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not null && value != assigned) return null;
            value = assigned;
        }
        return value;
    }

    /// <summary>
    /// Running balances kept in a variable, keyed by the name of the formula that shows them,
    /// each written as the expression it amounts to. A statement opens each account at its
    /// balance forward and runs it down the lines: a formula in the account's group header sets
    /// the variable to the opening figure ("v := {@Opening}"), and one placed in the details
    /// adds each line's amount and shows the total so far ("v := v + {@Amount}; v"). Placed in
    /// an inner group's footer instead, it adds that group's total ("v := v + Sum({@Amount},
    /// {T.Inner})"). Basic syntax writes the same with "v = ..." and "formula = v". Crystal
    /// runs the header's formula as the group starts and the other once where it prints, in
    /// order, so what prints is the opening figure plus the amount summed over the group's
    /// records so far: RunningValue(amount, Sum, group). With no reset the variable starts at
    /// 0 and runs through the report. Written as "" as variables are, the statement's running
    /// balance column printed nothing.
    ///
    /// Only that shape is taken: a numeric variable that is Shared (a formula using one runs
    /// while printing records) or used under WhilePrintingRecords, written by these two
    /// formulas alone in the whole report tree; the reset placed only in one group's headers,
    /// its figure the same on every record of the group (<paramref name="groupConstant"/>, given
    /// the expression and the group's level); the accumulation placed only in details, or only
    /// in the footers of a group inside the reset's, adding that group's Sum; neither used by
    /// another formula or in a text object. <paramref name="writers"/> is
    /// VariableWriters.Collect over the whole report tree.
    /// </summary>
    internal static Dictionary<string, string> RunningBalances(ReportDefinition report,
        Dictionary<string, HashSet<FormulaField>> writers, Func<string, string?> transpile,
        Func<string, int, bool> groupConstant)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var carried = report.Fields.OfType<FormulaField>().Select(CarriedVariable).OfType<Carried>().ToList();
        if (!carried.Any(c => c.Accumulates)) return result;
        var placements = Placements(report);
        var referenced = ReferencedByAnotherFormula(report);

        // Where a formula prints: its placements when all are one kind of section at one
        // level, used by no other formula and in no text object; null otherwise.
        List<Placement>? Sites(Carried c) =>
            !referenced.Contains(Name(c.Field)) && placements.TryGetValue(Name(c.Field), out var at)
            && at.Count > 0 && !at.Any(p => p.Text) && at.Select(p => (p.Type, p.Level)).Distinct().Count() == 1
                ? at : null;

        foreach (var add in carried.Where(c => c.Accumulates))
        {
            if (!writers.TryGetValue(add.Variable, out var w) || !w.Contains(add.Field)) continue;
            var resets = carried.Where(c => !c.Accumulates && w.Contains(c.Field)
                && c.Variable.Equals(add.Variable, StringComparison.OrdinalIgnoreCase)).ToList();
            if (resets.Count > 1 || w.Count != 1 + resets.Count) continue;
            if (Sites(add) is not { } addAt || addAt[0].Type is not (SectionType.Details or SectionType.GroupFooter)) continue;

            // A reset placed nowhere never runs, and the variable runs on from 0. One in a group
            // header repeated on each page runs again there, part way through the group.
            int? resetLevel = null;
            string? reset = null;
            if (resets.Count == 1 && (placements.ContainsKey(Name(resets[0].Field)) || referenced.Contains(Name(resets[0].Field))))
            {
                if (Sites(resets[0]) is not { } resetAt || resetAt[0].Type != SectionType.GroupHeader) continue;
                resetLevel = resetAt[0].Level;
                if (report.Sections.Any(s => s.Type == SectionType.GroupHeader && s.GroupLevel == resetLevel && s.RepeatGroupHeader)
                    || report.Groups.Any(g => g.Level == resetLevel && g.RepeatGroupHeader))
                    continue;
                if (transpile(resets[0].Value) is not string resetExpr || !groupConstant(resetExpr[1..], resetLevel.Value)) continue;
                reset = resetExpr[1..];
            }

            // In a group's footer the piece added is that group's total, which by the footer is
            // the running sum of what it totals over the group's records.
            string piece = add.Value;
            if (addAt[0].Type == SectionType.GroupFooter)
            {
                int level = addAt[0].Level;
                var sum = Regex.Match(piece, @"^Sum\s*\((.+),\s*\{([^{}]+)\}\s*\)$", Ci | RegexOptions.Singleline);
                if (!sum.Success || !Balanced(sum.Groups[1].Value) || resetLevel >= level
                    || FormulaTranspiler.GroupingOnField?.Invoke(sum.Groups[2].Value.Trim()) != $"Group{level + 1}")
                    continue;
                piece = sum.Groups[1].Value;
            }
            if (transpile(piece) is not string pieceExpr || pieceExpr is "=\"\"" or "=0") continue;
            string running = $"RunningValue({pieceExpr[1..]}, Sum, {(resetLevel is int l ? $"\"Group{l + 1}\"" : "Nothing")})";
            result[Name(add.Field)] = reset is null ? "=" + running : $"=({reset}) + {running}";
        }
        return result;
    }

    // A formula that sets a carried numeric variable and shows it: "v := value" (a reset) or
    // "v := v + value" (an accumulation), optionally followed by "v", in either syntax.
    private sealed record Carried(FormulaField Field, string Variable, string Value, bool Accumulates);

    private static Carried? CarriedVariable(FormulaField f)
    {
        string text = Regex.Replace(f.FormulaText, @"//[^\r\n]*", "");
        bool whilePrinting = Regex.IsMatch(text, @"\bWhilePrintingRecords\b", Ci);
        if (f.Syntax == FormulaSyntax.Basic || FormulaTranspiler.LooksLikeBasicSyntax(f.FormulaText))
        {
            // Basic's evaluation time is a statement of its own, which the restatement does not take.
            string? asCrystal = BasicStatements.ToCrystal(
                Regex.Replace(f.FormulaText, @"(?im)^\s*WhilePrintingRecords\s*$", ""), f.ResultType);
            if (asCrystal is null) return null;
            text = asCrystal;
        }
        if (SplitStatements(text) is not { } statements) return null;
        string? v = null;
        bool shared = false;
        var rest = new List<string>();
        foreach (var s in statements)
        {
            if (EvaluationTime.IsMatch(s)) continue;
            var d = Declaration.Match(s);
            if (d.Success)
            {
                string name = d.Groups[4].Value;
                if (name == BasicStatements.Result) continue;
                string type = d.Groups[2].Value;
                if (v is not null || d.Groups[3].Length > 0 || d.Groups[1].Value.Equals("Local", StringComparison.OrdinalIgnoreCase)
                    || !(type.Equals("Number", StringComparison.OrdinalIgnoreCase) || type.Equals("Currency", StringComparison.OrdinalIgnoreCase)))
                    return null;
                v = name;
                shared = d.Groups[1].Value.Equals("Shared", StringComparison.OrdinalIgnoreCase);
                if (d.Groups[5].Success) rest.Add(name + " := " + d.Groups[5].Value);
                continue;
            }
            rest.Add(Unwrap(s));
        }
        if (v is null || !(shared || whilePrinting)) return null;
        string V = Regex.Escape(v);
        string result = Regex.Escape(BasicStatements.Result);
        // The Basic restatement shows the variable through its result: "r := (v); r".
        if (rest.Count == 3 && Regex.IsMatch(rest[1], $@"^{result}\s*:=\s*\(\s*{V}\s*\)$", Ci) && rest[2] == BasicStatements.Result)
            rest.RemoveRange(1, 2);
        else if (rest.Count == 2 && rest[1].Equals(v, StringComparison.OrdinalIgnoreCase))
            rest.RemoveAt(1);
        if (rest.Count != 1) return null;
        var assignment = Regex.Match(rest[0], $@"^{V}\s*:=\s*(.+)$", Ci | RegexOptions.Singleline);
        if (!assignment.Success) return null;
        string value = Unwrap(assignment.Groups[1].Value);
        var accumulation = Regex.Match(value, $@"^{V}\s*\+\s*(.+)$", Ci | RegexOptions.Singleline);
        string piece = accumulation.Success ? accumulation.Groups[1].Value.Trim() : value;
        if (piece.Contains(":=") || Regex.IsMatch(StripStrings(piece), $@"(?<![\w.{{@]){V}\b", Ci) || !Balanced(piece))
            return null;
        return new Carried(f, v, piece, accumulation.Success);
    }

    // The kinds of section where formula name is evaluated: where it is placed, where each
    // formula that refers to it is, and sections whose own formulas refer to it; null when a
    // selection formula or a text object uses it, where once per record is not certain.
    private static List<SectionType>? EvaluatedIn(string name, ReportDefinition report, List<FormulaField> formulas,
        Dictionary<string, List<Placement>> placements, HashSet<string> visited)
    {
        if (!visited.Add(name)) return [];
        var sites = new List<SectionType>();
        if (placements.TryGetValue(name, out var at))
        {
            if (at.Any(p => p.Text)) return null;
            sites.AddRange(at.Select(p => p.Type));
        }
        string reference = "{@" + name + "}";
        bool Uses(string? text) => text?.Contains(reference, StringComparison.OrdinalIgnoreCase) == true;
        if (Uses(report.RecordSelectionFormula) || Uses(report.GroupSelectionFormula)) return null;
        foreach (var s in report.Sections)
            if (Uses(s.SuppressFormula) || Uses(s.BackColorFormula) || Uses(s.NewPageBeforeFormula) || Uses(s.NewPageAfterFormula))
                sites.Add(s.Type);
        foreach (var g in formulas.Where(g => Uses(g.FormulaText)))
        {
            if (EvaluatedIn(Name(g), report, formulas, placements, visited) is not List<SectionType> more) return null;
            sites.AddRange(more);
        }
        return sites;
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
        bool reportFooter, int level, Func<string, string?> transpile, bool printedAlone = false)
    {
        var pieces = new List<string>();
        string? lineList = null;
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
            // Each record's line ended by a line break ("x := x + {T.Name} + chr(10)"). Crystal
            // prints a string's last line break as no line at all, where this engine starts an
            // empty line after it, and the footer grew a line taller than Crystal's. Printed as
            // it is shown, it is the lines joined by line breaks.
            var ended = Regex.Match(use.Piece!, @"^(.+?)\s*\+\s*chr\s*\(\s*10\s*\)$", Ci | RegexOptions.Singleline);
            if (printedAlone && !numeric && use.Condition is null && ended.Success && Balanced(ended.Groups[1].Value)
                && !Regex.IsMatch(StripStrings(ended.Groups[1].Value), @"[=<>&]|\b(if|then|else|and|or|not)\b", Ci)
                && transpile(ended.Groups[1].Value) is string line)
                lineList = $"Join(Aggregate({line[1..]}), Chr(10))";
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
        return pieces.Count == 1 ? lineList ?? $"Join(Aggregate({pieces[0]}), \"\")" : null;
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
    // A reference in a comment runs nothing: such formulas often open with a note naming
    // their partner ("//{@reset_x} goes in the group header"), and taking that for a use
    // left a footer's list printing nothing.
    private static HashSet<string> ReferencedByAnotherFormula(ReportDefinition report)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var texts = report.Fields.OfType<FormulaField>().Select(f => f.FormulaText)
            .Concat(report.Sections.Select(s => s.SuppressFormula ?? ""))
            .Append(report.RecordSelectionFormula ?? "")
            .Append(report.GroupSelectionFormula ?? "");
        foreach (var t in texts)
            foreach (Match m in Regex.Matches(Regex.Replace(t, @"//[^\r\n]*", ""), @"\{@([^}]+)\}"))
                set.Add(m.Groups[1].Value);
        return set;
    }
}
