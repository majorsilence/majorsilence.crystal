using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Runtime;

/// <summary>
/// Takes a parsed .rpt (<see cref="ReportDefinition"/>) from bake-time overrides through
/// to RDL text, with no dependency on any specific render engine — callers (headless
/// PDF export, an interactive Avalonia viewer, etc.) each own the final "hand this RDL to
/// an engine and render" step themselves.
/// </summary>
public static class RenderPrep
{
    /// <summary>
    /// Applies to the model BEFORE <see cref="RdlConverter.Convert"/>, since RDL expresses
    /// these as static XML rather than something settable at render time. Table data and
    /// parameters are render-time concerns instead (RunGetData reads parameters directly,
    /// and SetData pushes table data straight onto the parsed RDL Report object) — neither
    /// ever touches this model, so they aren't handled here.
    /// </summary>
    /// <returns>
    /// One line per override that named nothing in the report (an object, formula, field,
    /// subreport or subreport parameter that does not exist). Such an override is skipped,
    /// not fatal, and the caller decides whether to surface the list.
    /// </returns>
    public static IReadOnlyList<string> ApplyBakeTimeOverrides(ReportDefinition report, RuntimeOverrides overrides)
    {
        var warnings = new List<string>();

        if (overrides.RecordSelectionFormula is not null)
            report.RecordSelectionFormula = overrides.RecordSelectionFormula;

        if (overrides.SortByFieldName is not null)
            ApplySortBy(report, overrides.SortByFieldName, warnings);

        ApplyFormulaText(report, overrides, warnings);

        var lookups = new ObjectLookups(overrides);
        ApplyObjectOverrides(report, overrides, lookups);
        foreach (var sub in AllSubreports(report))
            ApplyObjectOverrides(sub, overrides, lookups);
        lookups.ReportUnmatched(warnings);

        ApplySubreportParameters(report, overrides, warnings);
        return warnings;
    }

    // The report's own sort order is not decoded from the file yet, so SortFields is empty
    // for every parsed report: the override becomes the only sort. When the decode lands,
    // replacing the first sort field is what the real engine's callers do and what this does.
    private static void ApplySortBy(ReportDefinition report, string sortBy, List<string> warnings)
    {
        string name = sortBy.Trim().Trim('{', '}');
        int dot = name.IndexOf('.');
        string? table = dot >= 0 ? name[..dot] : null;
        string column = dot >= 0 ? name[(dot + 1)..] : name;

        var field = report.Fields.OfType<DatabaseField>().FirstOrDefault(f =>
            string.Equals(f.ColumnName, column, StringComparison.OrdinalIgnoreCase)
            && (table is null || string.Equals(f.TableName, table, StringComparison.OrdinalIgnoreCase)));
        if (field is null)
        {
            warnings.Add($"SortByFieldName: no database field '{sortBy}' in the report");
            return;
        }

        if (report.SortFields.Count > 0)
            report.SortFields[0].FieldName = field.ColumnName;
        else
            report.SortFields.Add(new SortField { FieldName = field.ColumnName });
    }

    private static void ApplyFormulaText(ReportDefinition report, RuntimeOverrides overrides, List<string> warnings)
    {
        if (overrides.FormulaFieldText.Count == 0) return;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, newText) in overrides.FormulaFieldText)
        {
            string bare = key.TrimStart('@');
            foreach (var formula in report.Fields.OfType<FormulaField>()
                         .Where(f => string.Equals(f.Name.TrimStart('@'), bare, StringComparison.OrdinalIgnoreCase)))
            {
                formula.FormulaText = newText;
                matched.Add(key);
            }
        }
        foreach (var key in overrides.FormulaFieldText.Keys.Where(k => !matched.Contains(k)))
            warnings.Add($"FormulaFieldText: no formula field named '{key}'");
    }

    // Case-insensitive views of the per-object dictionaries, plus which keys matched any
    // object in the main report or a subreport, so an unmatched key is reported once.
    private sealed class ObjectLookups(RuntimeOverrides overrides)
    {
        public readonly Dictionary<string, bool> Suppress = new(overrides.Suppress, StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> CanGrow = new(overrides.CanGrow, StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Resize = new(overrides.Resize, StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ObjectText = new(overrides.ObjectText, StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Matched = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> MovesMatched = [];

        public void ReportUnmatched(List<string> warnings)
        {
            foreach (var key in overrides.Suppress.Keys.Where(k => !Matched.Contains("Suppress:" + k)))
                warnings.Add($"Suppress: no report object named '{key}'");
            foreach (var key in overrides.CanGrow.Keys.Where(k => !Matched.Contains("CanGrow:" + k)))
                warnings.Add($"CanGrow: no report object named '{key}'");
            foreach (var key in overrides.Resize.Keys.Where(k => !Matched.Contains("Resize:" + k)))
                warnings.Add($"Resize: no report object named '{key}'");
            foreach (var key in overrides.ObjectText.Keys.Where(k => !Matched.Contains("ObjectText:" + k)))
                warnings.Add($"ObjectText: no text object named '{key}'");
            for (int i = 0; i < overrides.MoveObjectPosition.Count; i++)
                if (!MovesMatched.Contains(i))
                    warnings.Add($"MoveObjectPosition: no report object named '{overrides.MoveObjectPosition[i].ObjectName}'");
        }
    }

    private static void ApplyObjectOverrides(ReportDefinition report, RuntimeOverrides overrides, ObjectLookups l)
    {
        foreach (var section in report.Sections)
        foreach (var obj in section.Objects)
        {
            if (obj.Name.Length == 0) continue;

            if (l.Suppress.TryGetValue(obj.Name, out bool suppress))
            {
                obj.SuppressOverride = suppress;
                l.Matched.Add("Suppress:" + obj.Name);
            }

            if (l.CanGrow.TryGetValue(obj.Name, out bool canGrow))
            {
                obj.Format = obj.Format with { CanGrow = canGrow };
                l.Matched.Add("CanGrow:" + obj.Name);
            }

            if (l.Resize.TryGetValue(obj.Name, out int width))
            {
                obj.Bounds = obj.Bounds with { Width = width };
                l.Matched.Add("Resize:" + obj.Name);
            }

            if (obj is TextObject text && l.ObjectText.TryGetValue(obj.Name, out string? newText))
            {
                text.Text = newText;
                l.Matched.Add("ObjectText:" + obj.Name);
            }
        }

        for (int i = 0; i < overrides.MoveObjectPosition.Count; i++)
        {
            var move = overrides.MoveObjectPosition[i];
            var hit = report.Sections
                .SelectMany(s => s.Objects.Select(o => (Section: s, Object: o)))
                .FirstOrDefault(x => string.Equals(x.Object.Name, move.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (hit.Object is null) continue;
            l.MovesMatched.Add(i);

            var (section, obj) = hit;
            if (move.Axis == MoveAxis.Left)
            {
                obj.Bounds = obj.Bounds with { Left = move.Relative ? obj.Bounds.Left + move.Amount : move.Amount };
                continue;
            }

            // A Top move stays inside the section, as the real engine's callers keep it:
            // never above 0, never below where the object's bottom would leave the section.
            int wanted = move.Relative ? obj.Bounds.Top + move.Amount : move.Amount;
            int ceiling = Math.Max(0, section.HeightTwips - obj.Bounds.Height);
            obj.Bounds = obj.Bounds with { Top = Math.Max(0, Math.Min(wanted, ceiling)) };
        }
    }

    private static void ApplySubreportParameters(ReportDefinition report, RuntimeOverrides overrides, List<string> warnings)
    {
        foreach (var (subName, values) in overrides.SubreportParameters)
        {
            var subs = AllSubreportObjects(report)
                .Where(s => string.Equals(s.SubreportName, subName, StringComparison.OrdinalIgnoreCase) && s.Report is not null)
                .ToList();
            if (subs.Count == 0)
            {
                warnings.Add($"SubreportParameters: no subreport named '{subName}'");
                continue;
            }

            foreach (var (paramName, value) in values)
            {
                string bare = FormulaTranspiler.StripSapParamWrapper(paramName).Trim('@', '?', '{', '}');
                bool any = false;
                foreach (var sub in subs)
                {
                    var declared = sub.Report!.Fields.OfType<ParameterField>().FirstOrDefault(p =>
                        string.Equals(FormulaTranspiler.StripSapParamWrapper(p.Name).Trim('@', '?', '{', '}'), bare,
                            StringComparison.OrdinalIgnoreCase));
                    if (declared is null) continue;
                    sub.ParameterValueOverrides[declared.Name] = value;
                    any = true;
                }
                if (!any)
                    warnings.Add($"SubreportParameters: subreport '{subName}' has no parameter named '{paramName}'");
            }
        }
    }

    private static IEnumerable<SubreportObject> AllSubreportObjects(ReportDefinition report)
    {
        foreach (var section in report.Sections)
        foreach (var sub in section.Objects.OfType<SubreportObject>())
        {
            yield return sub;
            if (sub.Report is not null)
                foreach (var nested in AllSubreportObjects(sub.Report))
                    yield return nested;
        }
    }

    /// <summary>Every subreport in the tree, recursively (a subreport can itself contain nested subreports).</summary>
    public static IEnumerable<ReportDefinition> AllSubreports(ReportDefinition report)
    {
        foreach (var section in report.Sections)
        foreach (var sub in section.Objects.OfType<SubreportObject>())
        {
            if (sub.Report is null) continue;
            yield return sub.Report;
            foreach (var nested in AllSubreports(sub.Report))
                yield return nested;
        }
    }

    /// <summary>
    /// Converts the main report plus every subreport in its tree to RDL text, without
    /// writing anything to disk — the naming (<see cref="RdlConverter.SubreportRdlName"/>)
    /// matches exactly what the parent RDL's own <c>&lt;Subreport&gt;&lt;ReportName&gt;</c>
    /// elements expect, so a caller that writes the returned companions to a directory and
    /// points a render engine's "Folder" at it (however that engine lazily loads
    /// subreports) will resolve correctly.
    /// </summary>
    public static (string MainRdl, Dictionary<string, string> SubreportRdlByFileStem) ConvertWithSubreports(
        ReportDefinition report, string namePrefixStem = "Report", bool omitConnections = false)
    {
        string mainRdl = new RdlConverter { OmitConnections = omitConnections }.Convert(report, $"{namePrefixStem}_");
        var companions = new Dictionary<string, string>();
        CollectSubreportCompanions(report, namePrefixStem, companions, omitConnections);
        return (mainRdl, companions);
    }

    private static void CollectSubreportCompanions(ReportDefinition report, string namePrefixStem,
        Dictionary<string, string> companions, bool omitConnections)
    {
        foreach (var sub in report.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>()
                     .Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{namePrefixStem}_", sub.SubreportName);
            companions[name] = new RdlConverter { OmitConnections = omitConnections }.Convert(sub.Report!, $"{name}_");
            CollectSubreportCompanions(sub.Report!, name, companions, omitConnections);
        }
    }

    public static ReportAnalysis Analyze(ReportDefinition report)
    {
        var parameters = report.Fields.OfType<ParameterField>().ToList();
        return new ReportAnalysis
        {
            Parameters = parameters.Select(p => p.Name).ToList(),
            ParametersExtended = parameters.ToDictionary(p => p.Name, p => p.DataType),
            DataTables = BuildDataTables(report),
            Subreports = report.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>()
                .Where(s => s.Report is not null)
                .Select(s => BuildSubreportAnalysis(s.SubreportName, s.Report!))
                .ToList(),
            ReportObjects = BuildReportObjects(report)
        };
    }

    private static SubreportAnalysis BuildSubreportAnalysis(string name, ReportDefinition report) => new()
    {
        SubreportName = name,
        Parameters = report.Fields.OfType<ParameterField>().Select(p => p.Name).ToList(),
        DataTables = BuildDataTables(report)
    };

    // DataSource.Tables is frequently empty — Crystal's table/column metadata lives in
    // the encrypted QESession stream (see BACKLOG.md's "Connection strings" entry), which
    // can't be decoded. RdlConverter itself already falls back to the DatabaseField list
    // for the same reason (WriteDataSets.BuildSelectFromFields) — mirror that here rather
    // than reporting an empty table list whenever a report hits that (common) case.
    private static List<DataTableAnalysis> BuildDataTables(ReportDefinition report)
    {
        var fromDataSource = report.DataSources.SelectMany(ds => ds.Tables)
            .Select(t => new DataTableAnalysis
            {
                TableName = t.Name,
                ColumnNames = t.Columns.Select(c => c.Name).ToList()
            })
            .ToList();
        if (fromDataSource.Count > 0)
            return fromDataSource;

        return report.Fields.OfType<DatabaseField>()
            .GroupBy(f => f.TableName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DataTableAnalysis
            {
                TableName = g.Key,
                ColumnNames = g.Select(f => f.ColumnName).ToList()
            })
            .ToList();
    }

    private static List<ReportObjectAnalysis> BuildReportObjects(ReportDefinition report) =>
        report.Sections.SelectMany(s => s.Objects)
            .Where(o => o.Name.Length > 0)
            .Select(o => new ReportObjectAnalysis
            {
                ObjectName = o.Name,
                Width = o.Bounds.Width,
                TopPosition = o.Bounds.Top,
                ObjectValue = o is TextObject text ? text.Text : o.GetType().FullName ?? o.GetType().Name
            })
            .ToList();
}
