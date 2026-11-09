using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Details formatted with multiple columns, as on a sheet of mailing labels: record 108 holds
/// the layout, and printed Down then Across the details fill the body's columns.
/// </summary>
[TestFixture]
public class DetailColumnsTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static void Int32BE(List<byte> data, int value) =>
        data.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void Text(List<byte> data, string text)
    {
        Int32BE(data, text.Length + 1);
        data.AddRange(System.Text.Encoding.Latin1.GetBytes(text));
        data.Add(0);
    }

    private static TslvRecord Layout(string category, string stock, int width, int height, int horizontalGap,
        int verticalGap, bool downThenAcross)
    {
        var data = new List<byte>();
        Text(data, category);
        Text(data, stock);
        Int32BE(data, width);
        Int32BE(data, height);
        Int32BE(data, horizontalGap);
        Int32BE(data, verticalGap);
        data.AddRange([downThenAcross ? (byte)1 : (byte)0, 0, 0]);
        return new TslvRecord { Tag = 108, Data = data.ToArray() };
    }

    [Test]
    public void TheLayout_IsReadAfterTheStockNames()
    {
        var layout = RptParser.ReadDetailColumns(Layout("Address", "Sheet 30", 3780, 1440, 180, 0, downThenAcross: true));

        Assert.That(layout, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(layout!.WidthTwips, Is.EqualTo(3780));
            Assert.That(layout.HeightTwips, Is.EqualTo(1440));
            Assert.That(layout.HorizontalGapTwips, Is.EqualTo(180));
            Assert.That(layout.VerticalGapTwips, Is.EqualTo(0));
            Assert.That(layout.DownThenAcross, Is.True);
        });
    }

    [Test]
    public void OneDetailToThePageWidth_StoresNoWidth()
    {
        Assert.That(RptParser.ReadDetailColumns(Layout("", "", 0, 0, 0, 0, downThenAcross: true)), Is.Null);
        Assert.That(RptParser.ReadDetailColumns(Layout("", "", 5760, 2880, 270, 0, downThenAcross: false))!.DownThenAcross,
            Is.False);
    }

    private static ReportDefinition Labels(bool downThenAcross) => new()
    {
        Page = new PageLayout { WidthTwips = 12240, HeightTwips = 15840, LeftMarginTwips = 225, RightMarginTwips = 225 },
        DetailColumns = new DetailColumns
        {
            WidthTwips = 5760, HeightTwips = 2880, HorizontalGapTwips = 270, DownThenAcross = downThenAcross
        },
        Fields = [new DatabaseField { Name = "Address", ColumnName = "Address", TableName = "Owners", DataType = "String" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AddressField", FieldName = "Address", Bounds = new(60, 0, 5600, 240),
                    Format = new ObjectFormat { CanGrow = true } }] },
        ]
    };

    private static XDocument Converted(ReportDefinition report) => XDocument.Parse(new RdlConverter().Convert(report));

    [Test]
    public void DownThenAcross_FillsTheBodysColumnsWithCellsOfTheDetailSize()
    {
        var rdl = Converted(Labels(downThenAcross: true));
        var report = rdl.Root!;
        var body = report.Element(Rdl + "Body")!;
        var row = body.Descendants(Rdl + "Details").Single().Descendants(Rdl + "TableRow").Single();

        Assert.Multiple(() =>
        {
            // 8.1875in between the margins: two 4in labels and the 0.1875in gap between them.
            Assert.That(body.Element(Rdl + "Columns")?.Value, Is.EqualTo("2"));
            Assert.That(body.Element(Rdl + "ColumnSpacing")?.Value, Is.EqualTo("13.5pt"));
            Assert.That(report.Element(Rdl + "Width")?.Value, Is.EqualTo("288pt"));
            Assert.That(row.Element(Rdl + "Height")?.Value, Is.EqualTo("144pt"));
        });
    }

    // Crystal prints a detail in its cell, a growing address running on to the foot of the
    // cell and cut there, rather than growing the cell.
    [Test]
    public void AGrowingFieldInACell_RunsToTheFootOfTheCellAndNoFurther()
    {
        var textbox = Converted(Labels(downThenAcross: true)).Descendants(Rdl + "Details").Single()
            .Descendants(Rdl + "Textbox").Single();

        Assert.Multiple(() =>
        {
            Assert.That(textbox.Element(Rdl + "CanGrow")?.Value, Is.EqualTo("false"));
            // padded to the cell's foot it would have room for its first line alone
            Assert.That(double.Parse((textbox.Element(Rdl + "Style")?.Element(Rdl + "PaddingBottom")?.Value ?? "0pt")
                .Replace("pt", ""), System.Globalization.CultureInfo.InvariantCulture), Is.LessThan(12));
        });
    }

    [Test]
    public void AcrossThenDown_IsLeftOneToARow()
    {
        var rdl = Converted(Labels(downThenAcross: false));
        var body = rdl.Root!.Element(Rdl + "Body")!;

        Assert.Multiple(() =>
        {
            Assert.That(body.Element(Rdl + "Columns"), Is.Null);
            Assert.That(rdl.Root!.Element(Rdl + "Width")?.Value, Is.EqualTo("589.5pt"));
        });
    }
}
