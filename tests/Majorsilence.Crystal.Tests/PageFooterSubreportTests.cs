using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using System.Xml.Linq;

namespace Majorsilence.Crystal.Tests;

// A subreport in a page footer: Crystal prints as much of it as the footer holds, and its
// PageNumber and TotalPageCount are its own.
[TestFixture]
public class PageFooterSubreportTests
{
    private static ReportDefinition Inner() => new()
    {
        Fields = [new DatabaseField { Name = "Lines.Amount", TableName = "Lines", ColumnName = "Amount", DataType = "Float64" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "Amt", FieldName = "Lines.Amount", Bounds = new(0, 0, 1440, 240) },
                new TextObject { Name = "Tendered", Text = "Tendered", Bounds = new(1440, 0, 1440, 240),
                    SuppressFormula = "TotalPageCount <> PageNumber" }] }
        ]
    };

    private static ReportDefinition Outer(SectionType holds, ReportDefinition inner) => new()
    {
        Fields = [new DatabaseField { Name = "Receipt.Number", TableName = "Receipt", ColumnName = "Number", DataType = "String" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { FieldName = "Receipt.Number", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = holds, HeightTwips = 2880, Objects = [
                new SubreportObject { Name = "Copy", SubreportName = "Copy", Bounds = new(0, 0, 7200, 300), Report = inner }] }
        ]
    };

    private static XElement? FirstPageOnly(ReportDefinition outer)
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(outer));
        var ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "Subreport").Single().Element(ns + "Custom")?.Element(ns + "FirstPageOnly");
    }

    [Test]
    public void ASubreportInAPageFooter_PrintsItsFirstPageOnly()
    {
        Assert.That(FirstPageOnly(Outer(SectionType.PageFooter, Inner()))?.Value, Is.EqualTo("true"));
    }

    [Test]
    public void ASubreportElsewhere_RunsOn()
    {
        Assert.That(FirstPageOnly(Outer(SectionType.ReportFooter, Inner())), Is.Null);
    }

    [TestCase(SectionType.PageFooter, "=(1 <> 1)")]
    [TestCase(SectionType.ReportFooter, "=(Globals!TotalPages <> Globals!PageNumber)")]
    public void ItsPageNumbersAreItsOwn(SectionType holds, string hidden)
    {
        var inner = Inner();
        var outer = Outer(holds, inner);

        var doc = XDocument.Parse(new RdlConverter { Root = outer }.Convert(inner));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "Hidden").Select(h => h.Value), Has.Member(hidden));
    }
}
