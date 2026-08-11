using System.Data;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine;

public enum ExportFormat { Pdf }

public sealed class ReportExportException(string message) : Exception(message);

/// <summary>
/// Renders a Crystal Reports .rpt file using this repo's own parser+converter
/// pipeline plus the (unrelated, third-party) Majorsilence.Reporting RDL engine —
/// no dependency on the real, licensed CrystalDecisions.CrystalReports.Engine runtime.
///
/// Call <see cref="Init"/> once per process before using this class (wraps the
/// underlying engine's own required one-time setup).
/// </summary>
public sealed class ReportEngine
{
    private static bool s_initialized;

    public static void Init()
    {
        if (s_initialized) return;
        RdlEngineConfig.RdlEngineConfigInit();
        s_initialized = true;
    }

    public ReportAnalysis Analyze(Stream rptFile)
    {
        var result = RptParser.Parse(rptFile);
        if (!result.Success || result.Report is null)
            throw new ReportExportException($"Failed to parse .rpt: {string.Join("; ", result.Errors)}");

        return BuildAnalysis(result.Report);
    }

    public async Task<byte[]> ExportAsync(Stream rptFile, RuntimeOverrides overrides, ExportFormat format)
    {
        if (!s_initialized)
            throw new InvalidOperationException($"Call {nameof(ReportEngine)}.{nameof(Init)}() once before use.");

        var result = RptParser.Parse(rptFile);
        if (!result.Success || result.Report is null)
            throw new ReportExportException($"Failed to parse .rpt: {string.Join("; ", result.Errors)}");

        ReportDefinition report = result.Report;
        ApplyBakeTimeOverrides(report, overrides);

        const string stem = "Report";
        string rdl = new RdlConverter().Convert(report, $"{stem}_");

        // Subreports are separate companion .rdl files that the engine lazily loads by
        // name from Folder at render time (Subreport.GetReport) — there's no in-memory
        // handle to hand it directly, so each one in the tree has to be converted and
        // written to a scratch directory first, mirroring the naming the parent RDL's
        // own <Subreport><ReportName> already embeds (RdlConverter.SubreportRdlName).
        string tempDir = Path.Combine(Path.GetTempPath(), "rptengine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            WriteSubreportCompanions(report, stem, tempDir);

            var rdlp = new RDLParser(rdl) { Folder = tempDir };
            using var engineReport = await rdlp.Parse();

            if (overrides.Data is not null)
                await engineReport.DataSets["DataSet1"].SetData(overrides.Data);

            System.Collections.IDictionary? parms = null;
            if (overrides.Parameters.Count > 0)
            {
                parms = new System.Collections.Hashtable();
                foreach (var kv in overrides.Parameters)
                    parms[kv.Key] = kv.Value;
            }
            await engineReport.RunGetData(parms);

            using var streamGen = new MemoryStreamGen();
            var presentationType = format switch
            {
                ExportFormat.Pdf => OutputPresentationType.PDF,
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            await engineReport.RunRender(streamGen, presentationType);
            return ((MemoryStream)streamGen.GetStream()).ToArray();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Mirrors EngineCompatibilityTests.WriteCompanions' naming exactly, since the parent
    // RDL's own <Subreport><ReportName> elements (RdlConverter.SubreportRdlName) were
    // generated using the same prefix chain.
    private static void WriteSubreportCompanions(ReportDefinition report, string namePrefixStem, string dir)
    {
        foreach (var sub in report.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>()
                     .Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{namePrefixStem}_", sub.SubreportName);
            string path = Path.Combine(dir, name + ".rdl");
            File.WriteAllText(path, new RdlConverter().Convert(sub.Report!, $"{name}_"));
            WriteSubreportCompanions(sub.Report!, name, dir);
        }
    }

    // Applied to the model BEFORE RdlConverter.Convert, since RDL expresses these as
    // static XML rather than something settable at render time. Table data and
    // parameters are render-time concerns instead (see ExportAsync) — RunGetData reads
    // parameters directly, and SetData pushes table data straight onto the parsed RDL
    // Report object, neither ever touches this model.
    private static void ApplyBakeTimeOverrides(ReportDefinition report, RuntimeOverrides overrides)
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

    private static IEnumerable<ReportDefinition> AllSubreports(ReportDefinition report)
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

    private static ReportAnalysis BuildAnalysis(ReportDefinition report)
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
