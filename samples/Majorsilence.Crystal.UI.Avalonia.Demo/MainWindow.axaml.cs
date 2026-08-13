using System.Data;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.UI.RdlAvalonia.Viewer;

namespace Majorsilence.Crystal.UI.Avalonia.Demo;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadDemoReportAsync();
    }

    private static string CorpusPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/rpt-corpus", name));

    private async Task LoadDemoReportAsync()
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

        await Viewer.SetReportAsync(document);

        string? outPath = Environment.GetEnvironmentVariable("DEMO_PNG_OUT");
        if (!string.IsNullOrEmpty(outPath) && Viewer.CurrentPages is { PageCount: > 0 } pages)
        {
            var bitmap = ReportCanvas.RenderPageThumbnail(pages, 0, 900, dpi: 150.0);
            bitmap?.Save(outPath);
        }

        // Only exit automatically when explicitly asked to (scripted/CI use) — a normal
        // interactive run must stay open so there's a window to actually look at.
        if (Environment.GetEnvironmentVariable("DEMO_EXIT_AFTER_LOAD") == "1")
        {
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }
}
