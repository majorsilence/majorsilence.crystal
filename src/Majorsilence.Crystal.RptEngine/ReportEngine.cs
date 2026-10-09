using System.Data;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine;

/// <summary>
/// The outputs the engine can render. A host's other export types have no equivalent here:
/// the template itself (Crystal's "CrystalReport" export), plain text, and Word. Excel comes
/// out as .xlsx, not the .xls the Crystal runtime writes.
/// </summary>
public enum ExportFormat
{
    Pdf,
    /// <summary>The table rows as comma-separated text.</summary>
    Csv,
    /// <summary>An .xlsx laid out as the page is.</summary>
    Excel,
    /// <summary>An .xlsx of the data grid only, no page layout.</summary>
    ExcelDataOnly,
    Rtf
}

public static class ExportFormatInfo
{
    /// <summary>The file extension (without the dot) and media type for a format's output.</summary>
    public static (string Extension, string MediaType) For(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => ("pdf", "application/pdf"),
        ExportFormat.Csv => ("csv", "text/csv"),
        ExportFormat.Excel or ExportFormat.ExcelDataOnly =>
            ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ExportFormat.Rtf => ("rtf", "application/rtf"),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

public sealed class ReportExportException(string message) : Exception(message);

/// <summary>
/// The rendered bytes with their extension and media type, and the overrides that were
/// skipped because they named nothing in the report.
/// </summary>
public sealed record ExportResult(byte[] Bytes, string Extension, string MediaType, IReadOnlyList<string> Warnings);

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

        return RenderPrep.Analyze(result.Report);
    }

    public async Task<byte[]> ExportAsync(Stream rptFile, RuntimeOverrides overrides, ExportFormat format)
        => (await ExportWithWarningsAsync(rptFile, overrides, format)).Bytes;

    /// <summary>
    /// As <see cref="ExportAsync"/>, also returning one line per override that named nothing
    /// in the report and was skipped. A bad key never fails the render, so a host that wants
    /// to tell its caller about one reads them here.
    /// </summary>
    /// <summary>
    /// The engine's report for <paramref name="report"/>, its data pushed and fetched, ready to
    /// render: the overrides applied, the RDL and its subreport companions written to
    /// <paramref name="tempDir"/>, which the caller deletes when done with the report.
    /// </summary>
    internal static async Task<Report> LoadAsync(ReportDefinition report, RuntimeOverrides overrides, string tempDir,
        List<string> warnings)
    {
        warnings.AddRange(RenderPrep.ApplyBakeTimeOverrides(report, overrides));
        var (parms, parameterWarnings) = ParameterCoercion.Coerce(report, overrides.Parameters);
        warnings.AddRange(parameterWarnings);
        var subreportData = RenderPrep.SubreportDataByCompanion(report, overrides, warnings);

        // Rendered offline, twice over. The template names its own database, and a host
        // rendering uploaded templates must never open a connection one asks for: the RDL is
        // written without the connection string, and the engine's skip flag below makes its
        // connect path return before opening anything, so neither change alone is load-bearing.
        // Data comes from SetData; a dataset given none renders empty.
        var (mainRdl, subreportRdls) = RenderPrep.ConvertWithSubreports(report, omitConnections: true, keyedDescriptions: true);

        // Subreports are separate companion .rdl files that the engine lazily loads by
        // name from Folder at render time (Subreport.GetReport) — there's no in-memory
        // handle to hand it directly, so each one in the tree has to be written to a
        // scratch directory first.
        foreach (var (name, rdl) in subreportRdls)
            File.WriteAllText(Path.Combine(tempDir, name + ".rdl"), rdl);

        var rdlp = new RDLParser(mainRdl) { Folder = tempDir, SkipDatabaseSchemaValidation = true };
        var engineReport = await rdlp.Parse();

        // The main report's data: per-table data joined here with the file's links, or one
        // table the caller joined already, given the qualified column names the RDL reads.
        DataTable? mainData = null;
        if (overrides.TableData.Count > 0)
        {
            if (overrides.Data is not null)
                warnings.Add("Data: ignored, since TableData was given.");
            mainData = TableJoiner.Join(report, overrides.TableData, warnings);
        }
        else if (overrides.Data is not null)
        {
            mainData = TableJoiner.QualifyColumns(report, overrides.Data);
        }
        if (mainData is not null)
            await engineReport.DataSets["DataSet1"].SetData(mainData);

        // A subreport's data. The engine raises SubreportDataRetrieval as each subreport is
        // about to fetch its data, with the report switched to that subreport's definition, so
        // a table set on its dataset then is what it renders from; its Description is the
        // companion's stem, written for this. Handled only when there is data to hand over:
        // with a handler attached the engine stops caching a subreport's data between its
        // instances, since the handler might change it.
        if (subreportData.Count > 0)
        {
            engineReport.SubreportDataRetrieval += (_, e) =>
            {
                if (e.Report.Description is string stem && subreportData.TryGetValue(stem, out var table)
                    && e.Report.DataSets?["DataSet1"] is { } dataSet)
                    dataSet.SetData(table).GetAwaiter().GetResult();
            };
        }

        await engineReport.RunGetData(parms);
        return engineReport;
    }

    public async Task<ExportResult> ExportWithWarningsAsync(Stream rptFile, RuntimeOverrides overrides, ExportFormat format)
    {
        if (!s_initialized)
            throw new InvalidOperationException($"Call {nameof(ReportEngine)}.{nameof(Init)}() once before use.");

        var result = RptParser.Parse(rptFile);
        if (!result.Success || result.Report is null)
            throw new ReportExportException($"Failed to parse .rpt: {string.Join("; ", result.Errors)}");

        var warnings = new List<string>();
        string tempDir = Path.Combine(Path.GetTempPath(), "rptengine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var engineReport = await LoadAsync(result.Report, overrides, tempDir, warnings);

            using var streamGen = new MemoryStreamGen();
            var presentationType = format switch
            {
                ExportFormat.Pdf => OutputPresentationType.PDF,
                ExportFormat.Csv => OutputPresentationType.CSV,
                ExportFormat.Excel => OutputPresentationType.Excel2007,
                ExportFormat.ExcelDataOnly => OutputPresentationType.Excel2007DataOnly,
                ExportFormat.Rtf => OutputPresentationType.RTF,
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            await engineReport.RunRender(streamGen, presentationType);
            var (extension, mediaType) = ExportFormatInfo.For(format);
            return new ExportResult(((MemoryStream)streamGen.GetStream()).ToArray(), extension, mediaType, warnings);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
