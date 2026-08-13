using System.Data;
using Avalonia;
using Avalonia.Headless;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.UI.RdlAvalonia.Viewer;

namespace Majorsilence.Crystal.UI.Avalonia.Demo;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("HEADLESS_VERIFY") == "1")
        {
            BuildAvaloniaApp()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            RunHeadlessVerification().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static string CorpusPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/rpt-corpus", name));

    // Runs the same load path a real window would (RptReportViewer.SetReportAsync ->
    // AvaloniaReportViewer.SetReportAsync -> RunGetData/BuildPages/ReportCanvas.SetReport),
    // just under Avalonia's headless platform instead of a real display, so it can run in
    // this shell and still exercise the actual control stack rather than a stand-in.
    private static async Task RunHeadlessVerification()
    {
        string rptPath = CorpusPath("benbrahim777__CustomerList.rpt");

        var dt = new DataTable();
        dt.Columns.Add("Customer ID", typeof(string));
        dt.Columns.Add("Customer Name", typeof(string));
        dt.Columns.Add("Address1", typeof(string));
        dt.Columns.Add("City", typeof(string));
        dt.Columns.Add("Region", typeof(string));
        dt.Columns.Add("E-mail", typeof(string));
        dt.Rows.Add("PUSHED-001", "ZZZ-PUSHED-CUSTOMER-ZZZ", "1 Avalonia Ave", "Avaloniaville", "AV", "avalonia@example.com");

        var document = new RptReportDocument
        {
            OpenRpt = () => File.OpenRead(rptPath),
            Overrides = new RuntimeOverrides { Data = dt }
        };

        var viewer = new RptReportViewer();
        await viewer.SetReportAsync(document);

        Console.WriteLine($"Page count: {viewer.CurrentPages?.PageCount ?? -1}");

        string? outPath = Environment.GetEnvironmentVariable("DEMO_PNG_OUT");
        if (!string.IsNullOrEmpty(outPath) && viewer.CurrentPages is { PageCount: > 0 } pages)
        {
            var bitmap = ReportCanvas.RenderPageThumbnail(pages, 0, 900, dpi: 150.0);
            bitmap?.Save(outPath);
            Console.WriteLine($"Wrote {outPath}");
        }
    }
}
