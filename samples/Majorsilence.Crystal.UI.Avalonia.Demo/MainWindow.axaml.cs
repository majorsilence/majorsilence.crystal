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
        // Spaced out, not hyphenated — a single unbroken word wider than its column
        // has nowhere to wrap and legitimately overflows into the next cell (confirmed
        // in the engine's own word-wrap: GraphicsExtended.DrawStringJustified only
        // special-cases that for justified text, which these left-aligned cells don't
        // use). That's normal text-layout behavior for an unrealistic test string, not
        // a bug — so give it real word breaks instead of chasing an engine change.
        dt.Rows.Add("PUSHED-001", "ZZZ PUSHED CUSTOMER ZZZ", "1 Avalonia Ave", "Avaloniaville", "AV", "avalonia@example.com");
        // A handful more rows so the demo actually shows multi-row table layout,
        // page-header repeat behavior, and the Table Footer — not just one row.
        dt.Rows.Add("1", "City Cyclists", "7464 South Kingsway", "Sterling Heights", "MI", "Christianson@bba.com");
        dt.Rows.Add("2", "Pathfinders", "410 Eighth Avenue", "DeKalb", "IL", "Manley@arubasport.com");
        dt.Rows.Add("3", "Bike-A-Holics Anonymous", "7429 Arbutus Boulevard", "Blacklick", "OH", "Jannis@downunderbikes.com");
        dt.Rows.Add("4", "Psycho-Cycle", "8287 Scott Road", "Huntsville", "AL", "Mast@canbikes.com");
        dt.Rows.Add("5", "Sporting Wheels Inc.", "480 Grant Way", "San Diego", "CA", "Reyess@kangerootrikes.com");
        dt.Rows.Add("6", "Rockshocks for Jocks", "1984 Sydney Street", "Austin", "TX", "Davis@brucebikes.com");
        dt.Rows.Add("7", "Poser Cycles", "8194 Peter Avenue", "Eden Prairie", "MN", "Smith@peddlesofperth.com");
        dt.Rows.Add("8", "Spokes 'N Wheels Ltd.", "3802 Georgia Court", "Des Moines", "IA", "Chester@koalaroad.com");
        dt.Rows.Add("9", "Trail Blazer's Place", "6938 Beach Street", "Madison", "WI", "Burris@devilbikes.com");
        dt.Rows.Add("10", "Rowdy Rims Company", "4861 Second Road", "Newbury Park", "CA", "Shoemaker@piccolobike.com");

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
