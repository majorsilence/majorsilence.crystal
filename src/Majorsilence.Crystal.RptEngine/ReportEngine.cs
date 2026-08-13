using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
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

        return RenderPrep.Analyze(result.Report);
    }

    public async Task<byte[]> ExportAsync(Stream rptFile, RuntimeOverrides overrides, ExportFormat format)
    {
        if (!s_initialized)
            throw new InvalidOperationException($"Call {nameof(ReportEngine)}.{nameof(Init)}() once before use.");

        var result = RptParser.Parse(rptFile);
        if (!result.Success || result.Report is null)
            throw new ReportExportException($"Failed to parse .rpt: {string.Join("; ", result.Errors)}");

        ReportDefinition report = result.Report;
        RenderPrep.ApplyBakeTimeOverrides(report, overrides);

        var (mainRdl, subreportRdls) = RenderPrep.ConvertWithSubreports(report);

        // Subreports are separate companion .rdl files that the engine lazily loads by
        // name from Folder at render time (Subreport.GetReport) — there's no in-memory
        // handle to hand it directly, so each one in the tree has to be written to a
        // scratch directory first.
        string tempDir = Path.Combine(Path.GetTempPath(), "rptengine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (name, rdl) in subreportRdls)
                File.WriteAllText(Path.Combine(tempDir, name + ".rdl"), rdl);

            var rdlp = new RDLParser(mainRdl) { Folder = tempDir };
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
}
