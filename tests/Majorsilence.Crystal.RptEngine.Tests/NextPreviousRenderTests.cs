using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Next, Previous and OnLastRecord in section formulas, rendered: Next and Previous are the
/// records the report prints after and before the current one, across groups and whatever
/// order the rows came in; past either end they are null, and a condition reading a null one
/// is false.
/// </summary>
[TestFixture]
public class NextPreviousRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static FieldObject Field(string name, int left = 0) =>
        new() { Name = name + "Field", FieldName = name, Bounds = new(left, 0, 1400, 240) };

    private static ReportDefinition Report(Section header, Section gap, Section footer) => new()
    {
        ReportTitle = "Records",
        Fields = [
            new DatabaseField { Name = "Cust", ColumnName = "Cust", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Src", ColumnName = "Src", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Code", ColumnName = "Code", TableName = "T", DataType = "String" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Cust", TableName = "T" }],
        Sections = [
            header,
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects = [Field("Code"), Field("Src", 2000)] },
            gap,
            footer,
            new Section { Type = SectionType.ReportFooter, HeightTwips = 300, Objects = [
                new TextObject { Name = "End", Text = "END", Bounds = new(0, 0, 1440, 240) }] }]
    };

    private static Section Header(string? suppress = null) => new()
    {
        Type = SectionType.GroupHeader, HeightTwips = 300, GroupLevel = 0, Objects = [
            new FieldObject { Name = "HeaderSrc", FieldName = "Src", Bounds = new(0, 0, 1400, 240), SuppressFormula = suppress }]
    };

    private static Section Gap(string? suppress = null) => new()
    {
        Type = SectionType.Details, HeightTwips = 300, SuppressFormula = suppress, Objects = [
            new TextObject { Name = "Gap", Text = "GAP", Bounds = new(0, 0, 1440, 240) }]
    };

    private static Section Footer(string? newPageAfter = null) => new()
    {
        Type = SectionType.GroupFooter, HeightTwips = 300, GroupLevel = 0, NewPageAfterFormula = newPageAfter, Objects = [
            new TextObject { Name = "Foot", Text = "FOOT", Bounds = new(0, 0, 1440, 240) }]
    };

    // The rows arrive out of order: the table sorts them into the order Crystal prints.
    private static DataTable Rows(params (string Cust, string Src, string Code)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("Cust"); t.Columns.Add("Src"); t.Columns.Add("Code");
        foreach (var (cust, src, code) in rows)
            t.Rows.Add(cust, src, code);
        return t;
    }

    private static DataTable Mixed() => Rows(("B", "Y", "b1"), ("A", "X", "a1"), ("B", "Z", "b2"), ("A", "X", "a2"), ("A", "Y", "a3"));

    private static async Task<List<List<string>>> Pages(ReportDefinition definition, DataTable data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nextprev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(definition, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().Select(p => p.Cast<PageItem>().OfType<PageText>().OrderBy(t => t.Y).ThenBy(t => t.X)
                .Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList()).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // "Suppress all but the last line of a source": a3's next record is b1, in the next group,
    // with the same source; b2 is the last record, whose Next is null, and its line prints.
    [Test]
    public async Task ADetailsSuppressFormulasNext_IsTheNextRecordPrinted()
    {
        var pages = await Pages(Report(Header(), Gap("{T.Src} = Next({T.Src})"), Footer()), Mixed());
        var lines = pages.SelectMany(p => p).Where(t => t.Length == 2 || t == "GAP").ToList();

        Assert.That(lines, Is.EqualTo(new[] { "a1", "a2", "GAP", "a3", "b1", "GAP", "b2", "GAP" }));
    }

    // A group header sees its first record, whose Previous is the last record of the group
    // before; the first group's has none, and its field prints.
    [Test]
    public async Task AGroupHeadersPrevious_IsTheLastRecordOfTheGroupBefore()
    {
        var pages = await Pages(Report(Header("{T.Src} = Previous({T.Src})"), Gap("true"), Footer()),
            Rows(("A", "X", "a1"), ("A", "Y", "a2"), ("B", "Y", "b1"), ("C", "X", "c1")));
        var headers = pages.SelectMany(p => p).Where(t => t is "X" or "Y").ToList();

        // Each detail prints its source too: X, Y under A; Y under B with no header value; X
        // under C after its header's X.
        Assert.That(headers, Is.EqualTo(new[] { "X", "X", "Y", "Y", "X", "X" }));
    }

    // A footer's New Page After formula breaks after every group but the last, and the report
    // footer prints on the last group's page.
    [TestCase("{T.Cust} <> Next({T.Cust})")]
    [TestCase("not OnLastRecord")]
    public async Task AFootersNewPageAfterFormula_StartsNoPageAfterTheLastGroup(string formula)
    {
        var pages = await Pages(Report(Header(), Gap("true"), Footer(formula)), Mixed());

        Assert.Multiple(() =>
        {
            Assert.That(pages, Has.Count.EqualTo(2));
            Assert.That(pages[0], Does.Contain("a3").And.Not.Contain("b1"));
            Assert.That(pages[1], Does.Contain("b2").And.Contain("END"));
        });
    }

    // New Page After "Not OnLastRecord" on a header that prints nothing: each group but the
    // first starts a page, unless its header's record is the report's last.
    [Test]
    public async Task NotOnLastRecordOnAnEmptyHeader_KeepsALastGroupOfOneRecordOnThePage()
    {
        var empty = new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 0, Suppress = true,
            NewPageAfterFormula = "NOT OnLastRecord" };
        var lastOfOne = await Pages(Report(empty, Gap("true"), Footer()), Rows(("A", "X", "a1"), ("A", "X", "a2"), ("B", "Y", "b1")));
        var lastOfTwo = await Pages(Report(empty, Gap("true"), Footer()), Rows(("A", "X", "a1"), ("B", "Y", "b1"), ("B", "Y", "b2")));

        Assert.Multiple(() =>
        {
            Assert.That(lastOfOne, Has.Count.EqualTo(1));
            Assert.That(lastOfTwo, Has.Count.EqualTo(2));
            Assert.That(lastOfTwo[1], Does.Contain("b1").And.Contain("END"));
        });
    }
}
