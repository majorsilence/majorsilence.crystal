using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// New Page Before and After as Crystal applies them: set on an area as well as on a
/// section, on any section of a group's band, and on a report footer. The area values are
/// the Crystal runtime's own (AreaFormat.EnableNewPageBefore and EnableNewPageAfter).
/// </summary>
[TestFixture]
public class NewPageTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static string Corpus(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Report(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!;
    }

    private static XElement Grouping(ReportDefinition report) =>
        XDocument.Parse(new RdlConverter().Convert(report)).Descendants(Rdl + "Grouping").First(g => (string?)g.Attribute("Name") == "Group1");

    [Test]
    public void AnAreasNewPageBefore_StartsAPageBeforeItsFirstSection()
    {
        // The customer statement's group header area sets New Page Before; none of its three
        // sections does. Each customer's statement starts a page, and ran on from the last.
        var report = Report(External("meridian_sales__01_customer_statement.rpt"));
        var headers = report.Sections.Where(s => s.Type == SectionType.GroupHeader && s.GroupLevel == 0).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(headers, Has.Count.EqualTo(3));
            Assert.That(headers.Select(s => s.NewPageBefore), Is.EqualTo(new[] { true, false, false }));
            Assert.That((string?)Grouping(report).Element(Rdl + "PageBreakAtEnd"), Is.EqualTo("true"));
        });
    }

    [Test]
    public void AnAreasNewPageAfter_StartsAPageAfterItsLastSection()
    {
        // CustomFunctions' country footer area: New Page After, gated by "not onlastrecord".
        var footers = Report(Corpus("souvikduttachoudhury__CustomFunctions.rpt")).Sections
            .Where(s => s.Type == SectionType.GroupFooter && s.GroupLevel == 0).ToList();

        Assert.That(footers.Select(s => s.NewPageAfter), Is.EqualTo(new[] { false, true }));
    }

    private static ReportDefinition Statements(Section footer, bool detailColumns) => new()
    {
        Fields = [new DatabaseField { Name = "Account", ColumnName = "Account", TableName = "T", DataType = "String" },
                  new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "T" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AccountField", FieldName = "T.Account", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = !detailColumns, Objects = [
                new FieldObject { Name = "AmountField", FieldName = "T.Amount", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240, Objects = [
                new TextObject { Name = "Total", Text = "Total", Bounds = new(0, 0, 1440, 240) }] },
            footer,
        ]
    };

    // A statement's footer is several sections; the break is on its last, and is a break
    // between statements however many sections come before it.
    [TestCase(true)]
    [TestCase(false)]
    public void NewPageAfterOnALaterFooterSection_BreaksBetweenGroups(bool detailColumns)
    {
        var footer = new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240, NewPageAfter = true,
            Objects = [new TextObject { Name = "Remit", Text = "Please remit", Bounds = new(0, 0, 2880, 240) }] };

        Assert.That((string?)Grouping(Statements(footer, detailColumns)).Element(Rdl + "PageBreakAtEnd"), Is.EqualTo("true"));
    }

    // A formula decides a band group's break as it does a details table's: "not OnLastRecord"
    // is a break after every group but the last, and was dropped for groups with no columns.
    [Test]
    public void ANewPageAfterFormula_BreaksABandGroup()
    {
        var footer = new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240,
            NewPageAfterFormula = "not OnLastRecord",
            Objects = [new TextObject { Name = "Remit", Text = "Please remit", Bounds = new(0, 0, 2880, 240) }] };

        Assert.That((string?)Grouping(Statements(footer, detailColumns: false)).Element(Rdl + "PageBreakAtEnd"), Is.EqualTo("true"));
    }

    // Top5USAsubCanada's report footer, which holds its Canada subreport, starts a page of
    // its own: the area's New Page Before, on the band's row.
    [Test]
    public void AReportFootersNewPageBefore_BreaksTheTableAtItsRow()
    {
        var report = Report(Corpus("benbrahim777__Top5USAsubCanada.rpt"));
        var footer = report.Sections.Single(s => s.Type == SectionType.ReportFooter);
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var band = doc.Descendants(Rdl + "Rectangle").Where(r => r.Element(Rdl + "PageBreakAtStart") is not null).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(footer.NewPageBefore, Is.True);
            Assert.That(band, Has.Count.EqualTo(1));
            Assert.That(band[0].Descendants(Rdl + "Subreport"), Is.Not.Empty);
        });
    }
}
