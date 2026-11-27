using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using System.Xml.Linq;

namespace Majorsilence.Crystal.Tests;

// A report that reads no table prints its Details once and its report footer under them,
// once. So does a subreport that reads none.
[TestFixture]
public class NoTableReportFooterTests
{
    private static ReportDefinition Summary() => new()
    {
        Sections =
        [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 900, Objects = [
                new TextObject { Name = "Title", Text = "Summary", Bounds = new(0, 60, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 220 },
            new Section { Type = SectionType.ReportFooter, HeightTwips = 400, Objects = [
                new TextObject { Name = "Totals", Text = "Totals", Bounds = new(0, 160, 1440, 240) }] }
        ]
    };

    private static ReportDefinition List(ReportDefinition summary) => new()
    {
        Sections =
        [
            new Section { Type = SectionType.PageHeader, HeightTwips = 600, Objects = [
                new TextObject { Name = "Heading", Text = "List", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 220 },
            new Section { Type = SectionType.ReportFooter, HeightTwips = 600, Objects = [
                new TextObject { Name = "Grand", Text = "Grand Total", Bounds = new(7200, 120, 1440, 240) }] },
            new Section { Type = SectionType.PageFooter, HeightTwips = 700, Objects = [
                new SubreportObject { Name = "Summary", SubreportName = "Summary", Bounds = new(200, 80, 9000, 300), Report = summary }] }
        ]
    };

    // The page footer's subreport is written into a table, which wrote the report footer's
    // text as a row of its own beside the one placed under the Details.
    [Test]
    public void ItsReportFooter_PrintsOnce()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(List(Summary())));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "Value").Count(v => v.Value == "Grand Total"), Is.EqualTo(1));
    }

    [Test]
    public void ASubreportsReportFooter_IsUnderItsDetails()
    {
        var summary = Summary();
        var doc = XDocument.Parse(new RdlConverter { Root = List(summary) }.Convert(summary));
        var ns = doc.Root!.Name.Namespace;

        var totals = doc.Descendants(ns + "Textbox").Single(t => t.Element(ns + "Value")?.Value == "Totals");
        // the report header's 900 twips, the Details' 220 and the text's own 160 down its section
        Assert.That(totals.Element(ns + "Top")?.Value, Is.EqualTo("64pt"));
    }
}
