using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A group's bands print what Crystal prints for them: a group's name referred to as
/// "Group #N" alone, a footer of no height, and a total placed clear of its column.
/// </summary>
[TestFixture]
public class GroupBandPlacementTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    // An order list grouped on region, an amount beside each customer and a total for each region.
    private static ReportDefinition Orders(Section footer, params ReportObject[] header) => new()
    {
        Fields =
        [
            new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "Orders", DataType = "String" },
            new DatabaseField { Name = "Customer", ColumnName = "Customer", TableName = "Orders", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" },
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "Orders" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [.. header] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "CustomerField", FieldName = "Customer", Bounds = new(1080, 0, 2760, 221) },
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(3960, 0, 1260, 221) }] },
            footer,
        ]
    };

    private static Section Footer(int height, params ReportObject[] objects) =>
        new() { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = height, Objects = [.. objects] };

    private static XDocument Converted(ReportDefinition report) => XDocument.Parse(new RdlConverter().Convert(report));

    private static XElement Band(XDocument rdl, string band) =>
        rdl.Descendants(Rdl + "TableGroup").First().Element(Rdl + band)!;

    private static double Points(string? measure) =>
        double.Parse(measure!.Replace("pt", ""), System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    public void AFieldNamedGroupNumberAlone_PrintsTheGroupsName()
    {
        // Older reports name the group's name field "Group #1"; it printed as "[Group #1]".
        var rdl = Converted(Orders(Footer(0),
            new FieldObject { Name = "GroupName", FieldName = "Group #1", Bounds = new(0, 0, 1440, 240) }));

        Assert.That(Band(rdl, "Header").Descendants(Rdl + "Value").Select(v => v.Value),
            Does.Contain("=Fields!Region.Value").And.No.Contain("[Group #1]"));
    }

    [Test]
    public void GroupNumberAloneInAText_PrintsTheGroupsName()
    {
        // "{Group #1} - {Orders.Customer}" printed "{Group #1} - " and the customer.
        var rdl = Converted(Orders(Footer(0),
            new TextObject { Name = "Caption", Text = "{Group #1} - {Orders.Customer}", Bounds = new(0, 0, 4320, 240) }));

        string caption = Band(rdl, "Header").Descendants(Rdl + "Value").Select(v => v.Value)
            .Single(v => v.Contains("Customer"));
        Assert.That(caption, Does.StartWith("=Fields!Region.Value & \" - \"").And.Not.Contain("Group #1"));
    }

    [Test]
    public void AnEmptyFooterOfNoHeight_TakesNoRoom()
    {
        // Closed up in Crystal, it was given a line of its own, which doubled the pitch of a
        // group that changes on every record.
        var rdl = Converted(Orders(Footer(0)));

        Assert.That(Band(rdl, "Footer").Descendants(Rdl + "TableRow").Select(r => r.Element(Rdl + "Height")?.Value),
            Is.EqualTo(new[] { "0pt" }));
    }

    [Test]
    public void AFooterHoldingSomethingWithNoHeight_KeepsALine()
    {
        var rdl = Converted(Orders(Footer(0,
            new FieldObject { Name = "Total", FieldName = "Amount", SummaryFunction = AggregateFunction.Sum,
                Bounds = new(3960, 0, 1260, 221) })));

        Assert.That(Band(rdl, "Footer").Descendants(Rdl + "TableRow").First().Element(Rdl + "Height")?.Value,
            Is.EqualTo("12pt"));
    }

    [Test]
    public void ATotalClearOfItsColumn_PrintsWhereItIsPlaced()
    {
        // The total stands under the customers, left of the amount column; it went into the
        // amount column, a column's width to the right of where Crystal prints it.
        var rdl = Converted(Orders(Footer(240,
            new TextObject { Name = "TotalLabel", Text = "Totals", Bounds = new(0, 0, 1680, 229) },
            new FieldObject { Name = "Total", FieldName = "Amount", SummaryFunction = AggregateFunction.Sum,
                Bounds = new(2520, 0, 1260, 185) })));

        var total = Band(rdl, "Footer").Descendants(Rdl + "Textbox")
            .Single(t => t.Element(Rdl + "Value")?.Value == "=Sum(Fields!Amount.Value)");
        Assert.That(Points(total.Element(Rdl + "Left")?.Value), Is.EqualTo(126).Within(0.1));
    }

    [Test]
    public void ATotalUnderItsColumn_StaysInTheColumnsCell()
    {
        var rdl = Converted(Orders(Footer(240,
            new FieldObject { Name = "Total", FieldName = "Amount", SummaryFunction = AggregateFunction.Sum,
                Bounds = new(3960, 0, 1260, 185) })));

        var cells = Band(rdl, "Footer").Descendants(Rdl + "TableCell").ToList();
        Assert.That(cells, Has.Count.GreaterThan(1));
        Assert.That(cells.Last().Descendants(Rdl + "Value").Single().Value, Is.EqualTo("=Sum(Fields!Amount.Value)"));
    }
}
