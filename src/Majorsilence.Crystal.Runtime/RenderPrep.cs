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
    public static void ApplyBakeTimeOverrides(ReportDefinition report, RuntimeOverrides overrides)
    {
        if (overrides.RecordSelectionFormula is not null)
            report.RecordSelectionFormula = overrides.RecordSelectionFormula;

        if (overrides.SortByFieldName is not null && report.SortFields.Count > 0)
            report.SortFields[0].FieldName = overrides.SortByFieldName;

        foreach (var formula in report.Fields.OfType<FormulaField>())
        {
            if (overrides.FormulaFieldText.TryGetValue(formula.Name, out string? newText))
                formula.FormulaText = newText;
        }

        ApplyObjectOverrides(report, overrides);
        foreach (var sub in AllSubreports(report))
            ApplyObjectOverrides(sub, overrides);
    }

    private static void ApplyObjectOverrides(ReportDefinition report, RuntimeOverrides overrides)
    {
        foreach (var section in report.Sections)
        foreach (var obj in section.Objects)
        {
            if (obj.Name.Length == 0) continue;

            if (overrides.Suppress.TryGetValue(obj.Name, out bool suppress))
                obj.SuppressOverride = suppress;

            if (overrides.Resize.TryGetValue(obj.Name, out int width))
                obj.Bounds = obj.Bounds with { Width = width };

            if (obj is TextObject text && overrides.ObjectText.TryGetValue(obj.Name, out string? newText))
                text.Text = newText;
        }

        foreach (var move in overrides.MoveObjectPosition)
        {
            var obj = report.Sections.SelectMany(s => s.Objects)
                .FirstOrDefault(o => string.Equals(o.Name, move.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (obj is null) continue;

            obj.Bounds = move.Axis switch
            {
                MoveAxis.Left => obj.Bounds with { Left = move.Relative ? obj.Bounds.Left + move.Amount : move.Amount },
                MoveAxis.Top => obj.Bounds with { Top = move.Relative ? obj.Bounds.Top + move.Amount : move.Amount },
                _ => obj.Bounds
            };
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
        ReportDefinition report, string namePrefixStem = "Report")
    {
        string mainRdl = new RdlConverter().Convert(report, $"{namePrefixStem}_");
        var companions = new Dictionary<string, string>();
        CollectSubreportCompanions(report, namePrefixStem, companions);
        return (mainRdl, companions);
    }

    private static void CollectSubreportCompanions(ReportDefinition report, string namePrefixStem,
        Dictionary<string, string> companions)
    {
        foreach (var sub in report.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>()
                     .Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{namePrefixStem}_", sub.SubreportName);
            companions[name] = new RdlConverter().Convert(sub.Report!, $"{name}_");
            CollectSubreportCompanions(sub.Report!, name, companions);
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
