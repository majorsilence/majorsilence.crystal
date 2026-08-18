using System.Text.RegularExpressions;
using Majorsilence.Crystal.Converter.Formula;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Converter;

/// <summary>
/// Converts Crystal Reports formula syntax to RDL (VB.NET) expression syntax.
///
/// Uses the Irony-based <see cref="CrystalFormulaParser"/> as the primary engine.
/// Falls back to the legacy regex-based approach when parsing fails, so that
/// common patterns keep working even for formula constructs not yet in the grammar.
/// </summary>
public static class FormulaTranspiler
{
    public static string ToRdlExpression(FormulaField formula)
    {
        string text = formula.Syntax == FormulaSyntax.Basic
            ? NormalizeBasic(formula.FormulaText)
            : formula.FormulaText;

        // Crystal special fields (Page Number, Total Page Count, ...) can be a formula's
        // entire body written bare, with no {} wrapper. These are multi-word phrases that
        // can never parse as a valid expression — the grammar sees two-plus bare
        // identifiers with nothing joining them ("Page" parses fine as an identifier,
        // then "Number" is unexpected leftover input) — so check for them up front rather
        // than let the grammar/regex-fallback pair pass the literal words straight
        // through as if they were VB.NET syntax (=Page Number, which the target engine
        // then rejects with "End of expression expected").
        if (BareSpecialFieldExpression(text.Trim()) is string bareSpecial)
            return bareSpecial;

        // Rewrite a summary *caption* into the call it describes, then let the normal
        // pipeline below do the actual work (see SummaryCaption).
        text = RewriteSummaryCaption(text);

        // CurrentFieldValue only exists inside Crystal's conditional-formatting hooks —
        // it's "the value of the field this format rule is attached to", a context a
        // DataSet expression simply doesn't have. There is no translation; the formula
        // can't evaluate. Degrade to an empty string (the same valid-but-empty-beats-
        // fatal tradeoff as variable declarations) instead of letting the identifier
        // through, where it breaks the enclosing call's reflection binding with a
        // misleading "Function Month is not known".
        if (Regex.IsMatch(text, @"\bCurrentFieldValue\b", RegexOptions.IgnoreCase))
            return "=\"\"";

        // Crystal permits a trailing decimal point on number literals ("Case 15000. To
        // 1000000.:"), which the grammar's NumberLiteral rejects — the whole formula then
        // falls through to the regex fallback and the Select Case reaches the engine as
        // raw text. Dropping a digit-trailing dot not followed by another digit (or an
        // identifier — that would be member access) is a no-op numerically.
        text = Regex.Replace(text, @"(?<=\d)\.(?![\d\w])", "");

        // Primary: Irony-based parse + emit
        string? result = CrystalFormulaParser.Instance.ToRdlExpression(text);
        if (result != null)
            return $"={result}";

        // Fallback: regex-based (handles constructs the grammar doesn't yet cover)
        result = RegexTranspile(text);
        return $"={result}";
    }

    // Same phrases RdlConverter.SpecialFieldExpression recognizes for a placed
    // FieldObject's FieldName, reached a different way here (a formula's whole body,
    // rather than an object's bracketed field reference). Report-context-dependent ones
    // (Report Title, Report Comments) aren't included — not observed in this bare-body
    // shape in the corpus, and FormulaTranspiler has no report context to resolve them.
    private static string? BareSpecialFieldExpression(string text) => text.ToLowerInvariant() switch
    {
        "page number"       => "=Globals!PageNumber",
        "total page count"  => "=Globals!TotalPages",
        "page n of m"       => "=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages",
        "print date"        => "=Format(Globals!ExecutionTime, \"d\")",
        "print time"        => "=Format(Globals!ExecutionTime, \"T\")",
        "modification date" => "=Format(Globals!ExecutionTime, \"d\")",
        "record number"     => "=RowNumber()",
        _                   => null,
    };

    // Crystal's auto-generated caption for an inserted summary field — "Sum of
    // DunningData.OpenSum", "Average of Orders.Amount" — is sometimes stored as the
    // formula's actual body rather than just its display label. It's prose, not syntax,
    // so it can't parse; the words leak through the regex fallback and reach the engine
    // as "=Sum of Fields!OpenSum.Value" ("End of expression expected. At column 7").
    // Rewriting it to the call it describes ("Sum(DunningData.OpenSum)") lets the rest of
    // the pipeline resolve the field reference exactly as it would in a hand-written
    // formula, rather than duplicating that logic here. Anchored to the whole body so a
    // real expression that merely contains " of " is never touched.
    private static readonly Regex SummaryCaption = new(
        @"^\s*(Sum|Average|Count|Distinct\s*Count|Maximum|Minimum|" +
        @"Standard\s*Deviation|StdDev|Variance)\s+of\s+(\S.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static string RewriteSummaryCaption(string formula)
    {
        var m = SummaryCaption.Match(formula);
        if (!m.Success) return formula;

        string fn = Regex.Replace(m.Groups[1].Value, @"\s+", "").ToLowerInvariant() switch
        {
            "sum"                => "Sum",
            "average"            => "Avg",
            "count"              => "Count",
            "distinctcount"      => "CountDistinct",
            "maximum"            => "Max",
            "minimum"            => "Min",
            "standarddeviation"  => "StDev",
            "stddev"             => "StDev",
            "variance"           => "Var",
            _                    => "Sum",
        };
        return $"{fn}({m.Groups[2].Value})";
    }

    // ── Basic syntax pre-processor ──────────────────────────────────────────────

    // Crystal Basic adds "Formula = expr" at the top level and uses "End If"/"End Select".
    // Normalise before parsing so the grammar can treat both dialects identically.
    private static string NormalizeBasic(string formula)
    {
        // Strip leading "Formula = " or "formula ="
        formula = Regex.Replace(formula, @"(?i)^\s*formula\s*=\s*", "");

        // "End If" → nothing (the grammar has single-expr If/Then/Else)
        formula = Regex.Replace(formula, @"(?i)\bEnd\s+If\b", "");
        formula = Regex.Replace(formula, @"(?i)\bEnd\s+Select\b", "");

        return formula.Trim();
    }

    // Crystal variable-declaration patterns that cannot be mapped to SSRS VB.NET.
    // The Local/Global/Shared scope prefix is *optional* in Crystal — a bare
    // "stringvar timeString := ..." declares a local just as "Local StringVar ..." does —
    // so the scope group is optional here too. It used to be required, which let the
    // scopeless form slip past this guard entirely and reach the emitted RDL as
    // untranslatable junk (a reference to a variable RDL has no concept of), turning a
    // degraded-but-valid field into a fatal one. See CrystalFormulaGrammar's varDecl
    // note: this regex is deliberately the *only* place variable declarations are
    // handled, and it handles them by giving up cleanly.
    // Two spellings: Crystal syntax's "[scope] TypeVar name" and Basic syntax's
    // "Shared/Global/Local/Dim name as Type" — the private-corpus reports lean almost
    // entirely on the Basic form ("Shared CustomerAddress as string"), which used to
    // slip straight past this guard and reach the engine as raw text.
    private static readonly Regex CrystalVarDecl =
        new(@"\b(?:(?:Local|Global|Shared)\s+)?(?:Number|String|Boolean|Currency|Date|DateTime|Time|Range)Var\b" +
            @"|\b(?:Local|Global|Shared|Dim)\s+\w+(?:\s*\(\s*\))?\s+as\s+(?:number|string|boolean|currency|date|datetime|time|double|single|integer|long|decimal)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Regex fallback (retained from original implementation) ─────────────────

    private static string RegexTranspile(string formula)
    {
        // The Irony grammar skips // and /* */ comments as NonGrammarTerminals, but this
        // fallback only runs when that parse FAILED — and the failing formulas routinely
        // open with Crystal's boilerplate "// This conditional formatting formula..."
        // block, which then leaks into the emitted RDL as literal slashes ("Constant or
        // Identifier expected but not found. Found '/'"). Strip comments first.
        formula = Regex.Replace(formula, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        formula = Regex.Replace(formula, @"//[^\r\n]*", "");

        // A formula that was nothing but comments (placeholder conditional-format hooks
        // are commonly saved that way) is now empty — "=" alone is invalid RDL.
        if (string.IsNullOrWhiteSpace(formula))
            return "\"\"";

        // Crystal variable declarations (Local NumberVar, etc.) cannot be transpiled
        if (CrystalVarDecl.IsMatch(formula))
            return "\"\""; // emit empty string so the RDL field remains valid
        // {TableName.FieldName} → Fields!SanitizedFieldName.Value
        formula = Regex.Replace(formula, @"\{([^.?@}]+)\.([^}]+)\}",
            m => $"Fields!{SanitizeIdentifier(m.Groups[2].Value)}.Value");

        // {?ParameterName} → Parameters!ParameterName.Value ("$[Id]"-wrapped SAP Business
        // One parameter names unwrap to their real name first — "?$[BOY_AB_TODATE]" is the
        // parameter "BOY_AB_TODATE", not literally "$[BOY_AB_TODATE]").
        formula = Regex.Replace(formula, @"\{\?([^}]+)\}",
            m => $"Parameters!{SanitizeIdentifier(StripSapParamWrapper(m.Groups[1].Value))}.Value");

        // Bare ?ParameterName (without braces, e.g. stored in RecordSelectionFormula) → same
        formula = Regex.Replace(formula, @"(?<![A-Za-z0-9_!])\?(\$\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)",
            m => $"Parameters!{SanitizeIdentifier(StripSapParamWrapper(m.Groups[1].Value))}.Value");

        // {@FormulaName} / {#RunningTotalName} — references to another formula or
        // running-total field; both map to DataSet fields with the marker stripped
        formula = Regex.Replace(formula, @"\{[@#]([^}]+)\}",
            m => $"Fields!{SanitizeIdentifier(m.Groups[1].Value)}.Value");

        // Bare @FormulaName / #RunningTotalName (without braces) → same DataSet-field
        // mapping. Excludes anything already preceded by "!" so a just-substituted
        // "Fields!X.Value" or "Parameters!X.Value" above never gets re-matched.
        formula = Regex.Replace(formula, @"(?<![A-Za-z0-9_!])@([A-Za-z_][A-Za-z0-9_]*)",
            m => $"Fields!{SanitizeIdentifier(m.Groups[1].Value)}.Value");
        formula = Regex.Replace(formula, @"(?<![A-Za-z0-9_!])#([A-Za-z_][A-Za-z0-9_]*)",
            m => $"Fields!{SanitizeIdentifier(m.Groups[1].Value)}.Value");

        // Bare Table.Column (without braces) → Fields!Column.Value, same rule the braced
        // {Table.Column} form above uses (the table half is discarded — Column names are
        // unique per flattened DataSet in this converter). "(?<![!.\w])" keeps this from
        // re-matching text a prior substitution already produced (e.g. the ".Value" tail
        // of a just-emitted "Fields!X.Value").
        formula = Regex.Replace(formula, @"(?<![!.\w])([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)(?!\s*\()",
            m => $"Fields!{SanitizeIdentifier(m.Groups[2].Value)}.Value");

        // GroupName({field}) is the current group's value — the field itself (same
        // unwrap the emitter does; simple-argument forms only in this fallback). The
        // 2-arg form's second argument is a date-grouping condition — drop it.
        formula = Regex.Replace(formula, @"(?i)\bGroupName\s*\(([^(),]*)(?:,[^()]*)?\)", "($1)");

        // NthLargest(1, field [, groupField]) is the maximum (same rewrite the emitter
        // does). Only the literal-1 form is translated; any other N has no Max
        // equivalent and is left to surface rather than silently reporting a wrong value.
        formula = Regex.Replace(formula, @"(?i)\bNthLargest\s*\(\s*1\s*,\s*([^(),]*)(?:,[^()]*)?\)", "Max($1)");

        formula = ApplyFunctionMappings(formula);
        formula = TranspileIfThenElse(formula);

        return formula.Trim();
    }

    // SAP Business One parameter fields are named "$[InternalId]" rather than a plain
    // identifier (e.g. Crystal field name "?$[BOY_AB_TODATE]" for parameter
    // "BOY_AB_TODATE") — unwrap that bracket before sanitizing, or the "$[" / "]"
    // characters get flattened into underscores and the result never matches the
    // parameter's real name.
    internal static string StripSapParamWrapper(string name)
    {
        name = name.Trim();
        return name.StartsWith("$[") && name.EndsWith(']')
            ? name[2..^1]
            : name;
    }

    private static string ApplyFunctionMappings(string formula)
    {
        foreach (var (from, to) in RdlEmitter.FunctionMap)
        {
            formula = Regex.Replace(formula, $@"(?<!\w){Regex.Escape(from)}\s*\(",
                $"{to}(", RegexOptions.IgnoreCase);
        }

        // Bare identifier replacements (no parentheses)
        var bareMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PageNumber"]    = "Globals!PageNumber",
            ["TotalPageCount"] = "Globals!TotalPages",
            ["OnFirstRecord"] = "(RowNumber() = 1)",
            ["OnLastRecord"]  = "(RowNumber() = CountRows())",
            ["CurrentDate"]   = "Today()",
            ["CurrentTime"]   = "TimeOfDay()",
            ["Today"]         = "Today()",
            ["Now"]           = "Now()",
            // Crystal color constants
            ["crBlack"]   = "\"Black\"",   ["crMaroon"] = "\"#800000\"",
            ["crGreen"]   = "\"Green\"",   ["crOlive"]  = "\"Olive\"",
            ["crNavy"]    = "\"Navy\"",    ["crPurple"] = "\"Purple\"",
            ["crTeal"]    = "\"Teal\"",    ["crSilver"] = "\"Silver\"",
            ["crRed"]     = "\"Red\"",     ["crLime"]   = "\"Lime\"",
            ["crYellow"]  = "\"Yellow\"",  ["crBlue"]   = "\"Blue\"",
            ["crFuchsia"] = "\"Fuchsia\"", ["crAqua"]   = "\"Aqua\"",
            ["crWhite"]   = "\"White\"",   ["crNoColor"]= "\"Transparent\"",
        };
        foreach (var (from, to) in bareMap)
        {
            // "(?!\s*\()", not "\s*(?!\()" — the earlier form *consumed* the trailing
            // whitespace as part of the match, so Regex.Replace silently deleted it (e.g.
            // "CurrentDate\nELSE" -> "Today()ELSE", eating the newline that separated them
            // and breaking a later Then/Else split that depends on that whitespace still
            // being there). A pure lookahead checks the same "not followed by a call" rule
            // without consuming/replacing the whitespace itself.
            formula = Regex.Replace(formula, $@"(?<!\w){Regex.Escape(from)}(?!\w)(?!\s*\()",
                to, RegexOptions.IgnoreCase);
        }

        return formula;
    }

    private static string TranspileIfThenElse(string formula)
    {
        // \s* (not \s+) — Crystal allows "if(cond) THEN" with no space before the "(".
        // Singleline so "." spans the embedded newlines Crystal formulas commonly have
        // between If/Then/Else clauses (e.g. "if(...) THEN\nCurrentDate\nELSE\n...").
        var match = Regex.Match(formula,
            @"\bIf\b\s*(.+?)\s+\bThen\b\s+(.+?)\s+\bElse\b\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            return $"IIf({match.Groups[1].Value.Trim()}, " +
                   $"{match.Groups[2].Value.Trim()}, " +
                   $"{match.Groups[3].Value.Trim()})";
        }
        return formula;
    }

    // Produce a valid SSRS identifier from a Crystal field/column name (same rule as RdlConverter.SanitizeName)
    internal static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Field";
        string s = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        return char.IsDigit(s[0]) ? "_" + s : s;
    }
}
