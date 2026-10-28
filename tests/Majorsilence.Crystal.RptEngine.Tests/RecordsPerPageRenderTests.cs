using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Crystal's New Page After N Visible Records on the Details area: a new page before the
/// record after every Nth, with the footers that close the Nth record's groups kept on its
/// page. N is the area's count, or its formula's value - a parameter on a receipt - and 0 is
/// no break.
/// </summary>
[TestFixture]
public class RecordsPerPageRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report(int count = 0, string? formula = null) => new()
    {
        ReportTitle = "Lines",
        Fields = [
            new DatabaseField { Name = "Account", ColumnName = "Account", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Line", ColumnName = "Line", TableName = "T", DataType = "String" },
            new ParameterField { Name = "LinesPerPage", DataType = "Number" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "T" }],
        Sections = [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 0 },
            new Section { Type = SectionType.Details, HeightTwips = 240,
                NewPageAfterRecords = count, NewPageAfterRecordsFormula = formula, Objects = [
                    new FieldObject { Name = "LineField", FieldName = "Line", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240, Objects = [
                new TextObject { Name = "Total", Text = "TOTAL", Bounds = new(0, 0, 1440, 240) }] }]
    };

    private static DataTable Lines(int count)
    {
        var data = new DataTable();
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Line", typeof(string));
        for (int i = 1; i <= count; i++)
            data.Rows.Add("A", $"L{i:00}");
        return data;
    }

    // Each page's printed text, in order.
    private static async Task<List<List<string>>> Pages(ReportDefinition definition, DataTable data,
        Dictionary<string, object?>? parameters = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "recsperpage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(definition,
                new RuntimeOverrides { Data = data, Parameters = parameters ?? [] }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>()
                .Select(p => p.Cast<PageItem>().OfType<PageText>().Select(t => t.Text).ToList())
                .ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ACount_StartsAPageAfterEveryNthRecord()
    {
        var pages = await Pages(Report(count: 5), Lines(12));

        Assert.That(pages.Select(p => p.Count(t => t.StartsWith('L'))), Is.EqualTo(new[] { 5, 5, 2 }));
        Assert.That(pages[^1], Does.Contain("TOTAL"));
    }

    // The group ends with the 10th record: its footer stays on that page, and no page follows.
    [Test]
    public async Task NoPageFollowsTheLastRecord()
    {
        var pages = await Pages(Report(count: 5), Lines(10));

        Assert.That(pages.Select(p => p.Count(t => t.StartsWith('L'))), Is.EqualTo(new[] { 5, 5 }));
        Assert.That(pages[^1], Does.Contain("TOTAL"));
    }

    [TestCase(5, new[] { 5, 5, 2 })]
    [TestCase(0, new[] { 12 })]
    public async Task AParameterFormula_TakesTheParametersValue(int perPage, int[] expected)
    {
        var pages = await Pages(Report(formula: "{?LinesPerPage}"), Lines(12),
            new Dictionary<string, object?> { ["LinesPerPage"] = perPage });

        Assert.That(pages.Select(p => p.Count(t => t.StartsWith('L'))), Is.EqualTo(expected));
    }
}
