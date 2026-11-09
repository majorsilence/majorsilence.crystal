using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// An empty page header section above one that holds the page's heading: Crystal prints it as
/// room above the heading on every page.
/// </summary>
[TestFixture]
public class EmptyPageHeaderTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static ReportDefinition Letter(Section spacer) => new()
    {
        Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Levies", DataType = "Float64" }],
        Sections =
        [
            spacer,
            new Section { Type = SectionType.PageHeader, HeightTwips = 480, Objects = [
                new FieldObject { Name = "AmountHead", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] },
        ]
    };

    private static List<string> HeaderRowHeights(ReportDefinition report) =>
        XDocument.Parse(new RdlConverter().Convert(report)).Descendants(Rdl + "Table").First()
            .Element(Rdl + "Header")!.Descendants(Rdl + "TableRow")
            .Select(r => r.Element(Rdl + "Height")!.Value).ToList();

    [Test]
    public void AnEmptyPageHeaderAboveTheHeading_KeepsItsRoom()
    {
        var spacer = new Section { Type = SectionType.PageHeader, HeightTwips = 2340 };

        Assert.That(HeaderRowHeights(Letter(spacer)), Is.EqualTo(new[] { "117pt", "24pt" }));
    }

    [Test]
    public void OneSuppressedOrSetToSuppressWhenBlank_TakesNone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HeaderRowHeights(Letter(new Section { Type = SectionType.PageHeader, HeightTwips = 2340, Suppress = true })),
                Is.EqualTo(new[] { "24pt" }));
            Assert.That(HeaderRowHeights(Letter(new Section { Type = SectionType.PageHeader, HeightTwips = 2340, SuppressBlank = true })),
                Is.EqualTo(new[] { "24pt" }));
        });
    }
}
