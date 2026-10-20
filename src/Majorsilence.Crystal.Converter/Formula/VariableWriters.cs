using System.Text.RegularExpressions;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// Which formulas assign each variable, across a report and all its subreports.
///
/// A variable that is not Local keeps its value from one evaluation to the next, and a
/// Shared one is the same variable in a report and its subreports. So whether a formula's
/// own earlier value is the only value a variable can carry depends on whether any other
/// formula in the whole tree writes it (see <see cref="CrystalStatements"/>). Names are
/// matched without regard to case, as Crystal does, and two same-named variables in
/// different scopes count as one, which can only make a formula look less alone than it is.
/// </summary>
internal static class VariableWriters
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>For each variable name, the formulas that assign it.</summary>
    internal static Dictionary<string, HashSet<FormulaField>> Collect(ReportDefinition root)
    {
        var writers = new Dictionary<string, HashSet<FormulaField>>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in Tree(root))
            foreach (var f in report.Fields.OfType<FormulaField>())
                foreach (var name in Assigned(f.FormulaText))
                {
                    if (!writers.TryGetValue(name, out var set)) writers[name] = set = [];
                    set.Add(f);
                }
        return writers;
    }

    // Crystal syntax assigns with ":=" (a declaration's initializer included); Basic with
    // "name =" at the start of a statement, a statement that may follow "Then", for a name
    // the formula declares.
    private static IEnumerable<string> Assigned(string text)
    {
        string crystal = Regex.Replace(text, @"//[^\r\n]*", "");
        foreach (Match m in Regex.Matches(crystal, @"(?<![\w.{@])([A-Za-z_]\w*)\s*:=", Ci))
            yield return m.Groups[1].Value;

        var basicNames = Regex.Matches(text, @"(?im)^\s*(?:Dim|Shared|Global|Local)\s+(\w+(?:\s*,\s*\w+)*)\s+As\b")
            .SelectMany(m => m.Groups[1].Value.Split(',').Select(n => n.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (basicNames.Count == 0) yield break;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?:(?:Else\s*)?If\b.*?\bThen\s+)?([A-Za-z_]\w*)\s*=(?!=)", Ci);
            if (m.Success && basicNames.Contains(m.Groups[1].Value))
                yield return m.Groups[1].Value;
        }
    }

    private static IEnumerable<ReportDefinition> Tree(ReportDefinition root)
    {
        yield return root;
        foreach (var s in root.Sections)
            foreach (var sub in s.Objects.OfType<SubreportObject>())
                if (sub.Report is not null)
                    foreach (var r in Tree(sub.Report))
                        yield return r;
    }
}
