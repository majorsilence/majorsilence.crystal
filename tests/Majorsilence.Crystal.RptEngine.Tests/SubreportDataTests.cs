using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Data pushed to a subreport (<see cref="RuntimeOverrides.SubreportData"/>): the engine asks
/// for each subreport's data as it loads it, and the table given for that subreport is what
/// it prints. Checked on the laid-out pages, not on a PDF's text.
/// </summary>
[TestFixture]
public class SubreportDataTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Orders(string title) => new()
    {
        ReportTitle = title,
        Fields = [new DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" }],
        Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
            new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 2880, 240) }] }]
    };

    private static SubreportObject Placed(string name, ReportDefinition report, int top) =>
        new() { Name = name, SubreportName = name, Bounds = new(0, top, 5760, 1200), Report = report };

    private static ReportDefinition Main(params SubreportObject[] subreports) => new()
    {
        ReportTitle = "Main",
        Sections = [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600, Objects = [
                new TextObject { Name = "Title", Text = "Main title", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 4000, Objects = [.. subreports] }]
    };

    private static DataTable Items(params string[] items)
    {
        var table = new DataTable();
        table.Columns.Add("Item", typeof(string));
        foreach (var item in items) table.Rows.Add(item);
        return table;
    }

    private static async Task<(List<string> Printed, List<string> Warnings)> Render(ReportDefinition report, RuntimeOverrides overrides)
    {
        string dir = Path.Combine(Path.GetTempPath(), "subreportdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var warnings = new List<string>();
            using var engineReport = await ReportEngine.LoadAsync(report, overrides, dir, warnings);
            using var pages = await engineReport.BuildPages();
            Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(8), string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
            var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();
            return (printed, warnings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PushedRows_PrintInTheSubreport()
    {
        var overrides = new RuntimeOverrides { SubreportData = { ["Orders"] = Items("PUSHED-ONE", "PUSHED-TWO") } };

        var (printed, warnings) = await Render(Main(Placed("Orders", Orders("Orders"), 0)), overrides);

        Assert.That(printed, Is.SupersetOf(new[] { "Main title", "PUSHED-ONE", "PUSHED-TWO" }));
        Assert.That(warnings, Is.Empty);
    }

    // Titles can repeat, so each subreport is told apart by its companion's stem, not its title.
    [Test]
    public async Task TwoSubreportsWithTheSameTitle_EachPrintsItsOwnRows()
    {
        var overrides = new RuntimeOverrides
        {
            SubreportData = { ["OrdersA"] = Items("ONLY-IN-A"), ["OrdersB"] = Items("ONLY-IN-B") }
        };

        var (printed, _) = await Render(Main(Placed("OrdersA", Orders("Orders"), 0), Placed("OrdersB", Orders("Orders"), 1440)), overrides);

        Assert.That(printed.Count(t => t == "ONLY-IN-A"), Is.EqualTo(1));
        Assert.That(printed.Count(t => t == "ONLY-IN-B"), Is.EqualTo(1));
    }

    [Test]
    public async Task TheSubreportName_MatchesCaseInsensitively()
    {
        var overrides = new RuntimeOverrides { SubreportData = { ["orders"] = Items("PUSHED-ONE") } };

        var (printed, warnings) = await Render(Main(Placed("Orders", Orders("Orders"), 0)), overrides);

        Assert.That(printed, Does.Contain("PUSHED-ONE"));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public async Task ANameThatMatchesNoSubreport_IsReported_AndTheOthersStillPrint()
    {
        var overrides = new RuntimeOverrides
        {
            SubreportData = { ["Orders"] = Items("PUSHED-ONE"), ["NoSuchSubreport"] = Items("NEVER") }
        };

        var (printed, warnings) = await Render(Main(Placed("Orders", Orders("Orders"), 0)), overrides);

        Assert.That(printed, Does.Contain("PUSHED-ONE"));
        Assert.That(printed, Does.Not.Contain("NEVER"));
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("NoSuchSubreport"));
    }

    // A public report whose report footer holds a subreport reading its own copy of the
    // customer and order tables, parsed from the file as a host would render it.
    [Test]
    public async Task APublicReportsSubreport_PrintsTheRowsPushedToIt()
    {
        string path = Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Top5USAsubCanada.rpt", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        var report = RptParser.Parse(path).Report!;

        var orders = new DataTable();
        orders.Columns.Add("Customer Name", typeof(string));
        orders.Columns.Add("Country", typeof(string));
        orders.Columns.Add("Order Amount", typeof(decimal));
        orders.Rows.Add("ZZZ-SUBREPORT-CUSTOMER", "Canada", 123m);
        var main = new DataTable();
        main.Columns.Add("Customer Name", typeof(string));
        main.Columns.Add("Country", typeof(string));
        main.Columns.Add("Region", typeof(string));
        main.Columns.Add("Order Amount", typeof(decimal));
        main.Rows.Add("ZZZ-MAIN-CUSTOMER", "USA", "West", 999m);
        var overrides = new RuntimeOverrides { Data = main, SubreportData = { ["Subreport1"] = orders } };

        var (printed, warnings) = await Render(report, overrides);

        Assert.That(printed, Does.Contain("ZZZ-MAIN-CUSTOMER"), "the main report's own rows");
        Assert.That(printed, Does.Contain("ZZZ-SUBREPORT-CUSTOMER"), "the subreport's rows");
        Assert.That(warnings, Is.Empty);
    }
}
