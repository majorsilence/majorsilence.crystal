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

        // Primary: Irony-based parse + emit
        string? result = CrystalFormulaParser.Instance.ToRdlExpression(text);
        if (result != null)
            return $"={result}";

        // Fallback: regex-based (handles constructs the grammar doesn't yet cover)
        result = RegexTranspile(text);
        return $"={result}";
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

    // Crystal variable-declaration patterns that cannot be mapped to SSRS VB.NET
    private static readonly Regex CrystalVarDecl =
        new(@"\b(Local|Global|Shared)\s+(Number|String|Boolean|Currency|Date|DateTime|Time|Range)Var\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Regex fallback (retained from original implementation) ─────────────────

    private static string RegexTranspile(string formula)
    {
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

        // {@FormulaName} — reference to another formula field; map to DataSet field
        formula = Regex.Replace(formula, @"\{@([^}]+)\}",
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
