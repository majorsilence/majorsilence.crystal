using System.Data;
using System.Diagnostics;
using Majorsilence.Crystal.RptEngine;
using Majorsilence.Crystal.Runtime;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// End-to-end smoke tests: parse a real .rpt, apply runtime overrides, convert, and
/// actually render via Majorsilence.Reporting.RdlEngine.SkiaSharp — not just schema-check
/// the emitted RDL (that's what Majorsilence.Crystal.Tests/EngineCompatibilityTests.cs
/// already covers). A well-formed, non-trivial PDF proves the pipeline completes; the
/// pdftotext-based checks (skipped gracefully when poppler isn't installed) additionally
/// prove pushed runtime data actually reaches the rendered output, not just "didn't throw".
/// </summary>
[TestFixture]
public class ReportEngineTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static string CorpusPath(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static FileStream OpenCorpusFile(string name)
    {
        string path = CorpusPath(name);
        Assume.That(File.Exists(path), Is.True, $"{name} not found — run scripts/download-test-rpts.sh");
        return File.OpenRead(path);
    }

    private static void AssertWellFormedPdf(byte[] bytes)
    {
        Assert.That(bytes.Length, Is.GreaterThan(1000), "rendered PDF is suspiciously small");
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 5), Is.EqualTo("%PDF-"));
        string tail = System.Text.Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 32), Math.Min(32, bytes.Length));
        Assert.That(tail, Does.Contain("%%EOF"));
    }

    // Best-effort: extracts text via the `pdftotext` CLI (poppler) if present on PATH,
    // otherwise skips the assertion rather than failing on environments without it —
    // same "verify when the environment supports it, skip gracefully otherwise" pattern
    // already used for WMF/EMF rasterization elsewhere in this test suite.
    private static string? TryExtractPdfText(byte[] pdfBytes)
    {
        string tmpPdf = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmpPdf, pdfBytes);
            var psi = new ProcessStartInfo("pdftotext", $"\"{tmpPdf}\" -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
        finally
        {
            File.Delete(tmpPdf);
        }
    }

    [Test]
    public async Task ExportAsync_SingleTableReport_RendersPushedDataTable()
    {
        using var rpt = OpenCorpusFile("benbrahim777__CustomerList.rpt");

        var dt = new DataTable();
        dt.Columns.Add("Customer ID", typeof(string));
        dt.Columns.Add("Customer Name", typeof(string));
        dt.Columns.Add("Address1", typeof(string));
        dt.Columns.Add("City", typeof(string));
        dt.Columns.Add("Region", typeof(string));
        dt.Columns.Add("E-mail", typeof(string));
        dt.Rows.Add("PUSHED-001", "ZZZ-PUSHED-CUSTOMER-ZZZ", "1 Test St", "Testville", "TS", "test@example.com");

        var overrides = new RuntimeOverrides { Data = dt };
        byte[] pdf = await new ReportEngine().ExportAsync(rpt, overrides, ExportFormat.Pdf);

        AssertWellFormedPdf(pdf);

        string? text = TryExtractPdfText(pdf);
        Assume.That(text, Is.Not.Null, "pdftotext not available on PATH — skipping content-level check");
        Assert.That(text, Does.Contain("PUSHED"),
            "pushed DataTable values must reach the rendered PDF, not just avoid throwing");
    }

    [Test]
    public async Task ExportAsync_GroupedReportWithSubreport_RendersWithoutThrowing()
    {
        // Subreport-owned table data has no confirmed push mechanism yet (see the
        // project's planning notes) — this only pushes the main report's DataSet1 and
        // confirms the overall structure (grouping, subreport placement) still renders.
        //
        // Uses Top5USAsubCanada rather than Top5USAwithSub: the latter's subreport hits
        // a separate, already-flagged pre-existing converter bug (a free-form Sum()
        // missing its DataSet scope, plus a highlight formula whose Sum() scope argument
        // is a field expression instead of a constant) unrelated to what this test is
        // verifying — using it would make this test fail for a reason that has nothing
        // to do with the runtime-override/render pipeline being tested here.
        using var rpt = OpenCorpusFile("benbrahim777__Top5USAsubCanada.rpt");

        var dt = new DataTable();
        dt.Columns.Add("Customer Name", typeof(string));
        dt.Columns.Add("Country", typeof(string));
        dt.Columns.Add("Region", typeof(string));
        dt.Columns.Add("Order Amount", typeof(decimal));
        dt.Rows.Add("MAIN-PUSHED-CUSTOMER", "USA", "West", 999m);

        var overrides = new RuntimeOverrides { Data = dt };
        byte[] pdf = await new ReportEngine().ExportAsync(rpt, overrides, ExportFormat.Pdf);

        AssertWellFormedPdf(pdf);
    }

    [Test]
    public async Task ExportAsync_SuppressOverride_DoesNotThrow_ForADiscoveredObjectName()
    {
        // Discover a real object name via Analyze rather than guessing one, then confirm
        // applying a Suppress override for it renders successfully end-to-end.
        string objectName;
        using (var forAnalyze = OpenCorpusFile("benbrahim777__CustomerList.rpt"))
        {
            var analysis = new ReportEngine().Analyze(forAnalyze);
            Assume.That(analysis.ReportObjects, Is.Not.Empty, "no report objects found to suppress");
            objectName = analysis.ReportObjects[0].ObjectName;
        }

        using var rpt = OpenCorpusFile("benbrahim777__CustomerList.rpt");
        var overrides = new RuntimeOverrides { Suppress = { [objectName] = true } };
        byte[] pdf = await new ReportEngine().ExportAsync(rpt, overrides, ExportFormat.Pdf);

        AssertWellFormedPdf(pdf);
    }

    [Test]
    public void Analyze_SingleTableReport_ReturnsTableAndColumns()
    {
        using var rpt = OpenCorpusFile("benbrahim777__CustomerList.rpt");
        var analysis = new ReportEngine().Analyze(rpt);

        Assert.That(analysis.DataTables, Is.Not.Empty);
        Assert.That(analysis.DataTables.SelectMany(t => t.ColumnNames), Does.Contain("Customer Name"));
        Assert.That(analysis.ReportObjects, Is.Not.Empty);
    }

    [Test]
    public void Analyze_ReportWithSubreport_ListsSubreportRecursively()
    {
        using var rpt = OpenCorpusFile("benbrahim777__Top5USAwithSub.rpt");
        var analysis = new ReportEngine().Analyze(rpt);

        Assert.That(analysis.Subreports, Is.Not.Empty);
        Assert.That(analysis.Subreports[0].DataTables, Is.Not.Empty,
            "the subreport's own table metadata must be reachable too");
    }
}
