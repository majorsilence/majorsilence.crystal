using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Crystal's Keep Together on a section that grows: a report footer whose subreport does not
/// fit in what is left of the page prints whole on the next page, and stays where it is with
/// Keep Together off.
/// </summary>
[TestFixture]
public class KeepTogetherRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Child() => new()
    {
        ReportTitle = "Child",
        Fields = [new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" }],
        Sections = [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [
                new TextObject { Name = "ChildTitle", Text = "CHILD-TITLE", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 2880, 240) }] }]
    };

    private static ReportDefinition Parent(bool keepTogether) => new()
    {
        ReportTitle = "Parent",
        Fields = [new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Line", ColumnName = "Line", TableName = "T", DataType = "String" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "LineField", FieldName = "Line", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.ReportFooter, HeightTwips = 600, KeepTogether = keepTogether, Objects = [
                new TextObject { Name = "Caption", Text = "JOBS", Bounds = new(0, 0, 1440, 240) },
                new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 240, 4320, 300), Report = Child() }] }]
    };

    // 40 lines of a sixth of an inch leave about three inches of a Letter page; the footer's
    // 30 subreport rows need five.
    [TestCase(true, 2)]
    [TestCase(false, 1)]
    public async Task AReportFooterThatDoesNotFit_PrintsWholeOnTheNextPage(bool keepTogether, int footerPage)
    {
        var lines = new DataTable();
        lines.Columns.Add("Line", typeof(string));
        for (int i = 0; i < 40; i++)
            lines.Rows.Add($"Line{i}");
        var items = new DataTable();
        items.Columns.Add("Item", typeof(string));
        for (int i = 0; i < 30; i++)
            items.Rows.Add($"Item{i}");
        var overrides = new RuntimeOverrides { Data = lines };
        overrides.SubreportData["Sub1"] = items;

        string dir = Path.Combine(Path.GetTempPath(), "keeptogether-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Parent(keepTogether), overrides, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>()
                .Select((p, i) => p.Cast<PageItem>().OfType<PageText>().Select(t => (Page: i + 1, t.Text)).ToList())
                .SelectMany(t => t).ToList();
            int PageOf(string text) => texts.First(t => t.Text == text).Page;

            Assert.Multiple(() =>
            {
                Assert.That(PageOf("Line39"), Is.EqualTo(1), "the details fit on the first page");
                Assert.That(PageOf("JOBS"), Is.EqualTo(footerPage));
                Assert.That(PageOf("CHILD-TITLE"), Is.EqualTo(footerPage));
                Assert.That(texts.Count(t => t.Text.StartsWith("Item")), Is.EqualTo(30));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
