using Avalonia.Controls;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.UI.Avalonia;

/// <summary>
/// Avalonia UserControl hosting a (locally modified — see the csproj comment)
/// <c>Majorsilence.Reporting.UI.RdlAvalonia.Viewer.AvaloniaReportViewer</c>, exposing an
/// API shaped after <c>BaseReportControl</c> in
/// TownSuiteTypes.UserInterface.Winforms.Reports: a report-document abstraction in,
/// a <see cref="ReportLoaded"/> event out, and a full toolbar (zoom, page nav, find,
/// thumbnails, parameters, export) for free from the inner control.
/// </summary>
public partial class RptReportViewer : UserControl
{
    private string? _tempDir;

    public RptReportViewer()
    {
        InitializeComponent();
        InnerViewer.ReportLoaded += (_, _) => ReportLoaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forwarded from the inner viewer's own <c>ReportLoaded</c> event.</summary>
    public event EventHandler? ReportLoaded;

    /// <summary>The pages built by the most recent successful load — e.g. for programmatic thumbnail/PNG export.</summary>
    public Pages? CurrentPages => InnerViewer.CurrentPages;

    public async Task SetReportAsync(RptReportDocument document)
    {
        var result = RptParser.Parse(document.OpenRpt());
        if (!result.Success || result.Report is null)
            throw new InvalidOperationException($"Failed to parse .rpt: {string.Join("; ", result.Errors)}");

        var report = result.Report;
        RenderPrep.ApplyBakeTimeOverrides(report, document.Overrides);

        var (mainRdl, subreportRdls) = RenderPrep.ConvertWithSubreports(report);

        CleanUpTempDir();
        string tempDir = Path.Combine(Path.GetTempPath(), "crystal-ui-avalonia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDir = tempDir;

        foreach (var (name, rdl) in subreportRdls)
            File.WriteAllText(Path.Combine(tempDir, name + ".rdl"), rdl);

        var rdlp = new RDLParser(mainRdl) { Folder = tempDir };
        var engineReport = await rdlp.Parse();

        if (document.Overrides.Data is not null)
            await engineReport.DataSets["DataSet1"].SetData(document.Overrides.Data);

        if (document.Overrides.Parameters.Count > 0)
        {
            // AvaloniaReportViewer.SetReportParametersAmpersandSeparated does no
            // encoding/decoding of its own — matching that contract as-is rather than
            // introducing a mismatched encoding on just one side.
            string paramString = string.Join("&",
                document.Overrides.Parameters.Select(kv => $"{kv.Key}={kv.Value}"));
            InnerViewer.SetReportParametersAmpersandSeparated(paramString);
        }

        await InnerViewer.SetReportAsync(engineReport);
    }

    /// <summary>Re-renders against the same already-loaded report (e.g. after changing parameters).</summary>
    public Task ReloadAsync() => InnerViewer.RebuildAsync();

    private void CleanUpTempDir()
    {
        if (_tempDir is null) return;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        _tempDir = null;
    }
}
