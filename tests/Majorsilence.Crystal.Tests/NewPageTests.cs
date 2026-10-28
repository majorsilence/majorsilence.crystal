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

    // New Page After N Visible Records is the Details area's: the runtime reports 5 for
    // paging_records_per_page's (AreaFormat.VisibleRecordNumberPerPage) and 0 for BeforeTV's.
    [Test]
    public void AnAreasNewPageAfterNRecords_IsReadForItsSections()
    {
        var paged = Report(External("synthetic__paging_records_per_page.rpt")).Sections.Where(s => s.Type == SectionType.Details).ToList();
        var plain = Report(Corpus("benbrahim777__BeforeTV.rpt")).Sections.Where(s => s.Type == SectionType.Details).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(paged.Select(s => s.NewPageAfterRecords), Is.All.EqualTo(5));
            Assert.That(paged.Select(s => s.NewPageAfterRecordsFormula), Is.All.Null);
            Assert.That(plain.Select(s => s.NewPageAfterRecords), Is.All.EqualTo(0));
        });
    }

    private static ReportDefinition Lines(int count = 0, string? formula = null, bool suppressDetails = false,
        int groupHeaderHeight = 0) => new()
    {
        Fields = [new DatabaseField { Name = "Account", ColumnName = "Account", TableName = "T", DataType = "String" },
                  new DatabaseField { Name = "Line", ColumnName = "Line", TableName = "T", DataType = "String" },
                  new ParameterField { Name = "LinesPerPage", DataType = "Number" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "T" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = groupHeaderHeight, Objects = groupHeaderHeight == 0 ? [] : [
                new FieldObject { Name = "AccountField", FieldName = "T.Account", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = suppressDetails,
                NewPageAfterRecords = count, NewPageAfterRecordsFormula = formula, Objects = [
                    new FieldObject { Name = "LineField", FieldName = "T.Line", Bounds = new(0, 0, 1440, 240) }] },
        ]
    };

    private static XElement? DetailRecordGrouping(ReportDefinition report) =>
        XDocument.Parse(new RdlConverter().Convert(report)).Descendants(Rdl + "Grouping")
            .FirstOrDefault(g => (string?)g.Attribute("Name") == "CrystalDetailRecord");

    // The break goes before the record after every Nth, gated on the record's number.
    [TestCase(5, null, "=Fields!CrystalDetailRow.Value > 1 And (Fields!CrystalDetailRow.Value - 1) Mod 5 = 0")]
    [TestCase(0, "4", "=Fields!CrystalDetailRow.Value > 1 And (Fields!CrystalDetailRow.Value - 1) Mod 4 = 0")]
    [TestCase(3, "{?LinesPerPage}", "=(Parameters!LinesPerPage.Value) >= 1 And Fields!CrystalDetailRow.Value > 1 And (Fields!CrystalDetailRow.Value - 1) Mod IIf((Parameters!LinesPerPage.Value) >= 1, (Parameters!LinesPerPage.Value), 1) = 0")]
    public void NewPageAfterNRecords_BreaksBeforeTheRecordAfterEveryNth(int count, string? formula, string condition)
    {
        var grouping = DetailRecordGrouping(Lines(count, formula));

        Assert.Multiple(() =>
        {
            Assert.That(grouping, Is.Not.Null);
            Assert.That((string?)grouping!.Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
            Assert.That(grouping.Element(Rdl + "PageBreakAtEnd"), Is.Null);
            Assert.That((string?)grouping.Element(Rdl + "PageBreakCondition"), Is.EqualTo(condition));
        });
    }

    // N of 0 is no break; details that never print count no records; and a group header that
    // prints would be left behind on the old page, so it takes the break away too.
    [TestCase(0, null, false, 0)]
    [TestCase(5, "0", false, 0)]
    [TestCase(5, null, true, 0)]
    [TestCase(5, null, false, 240)]
    public void NewPageAfterNRecords_IsNotWritten_WhereItBreaksNothingOrCannotBeKept(int count, string? formula,
        bool suppressDetails, int groupHeaderHeight)
    {
        var report = Lines(count, formula, suppressDetails, groupHeaderHeight);

        Assert.Multiple(() =>
        {
            Assert.That(DetailRecordGrouping(report), Is.Null);
            Assert.That(new RdlConverter().Convert(report), Does.Not.Contain("CrystalDetailRow"));
        });
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
