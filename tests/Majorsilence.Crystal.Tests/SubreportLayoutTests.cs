using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

// A report placed as a subreport (#37): its frame, where it sits, how wide it is, and its
// stacked report headers.
[TestFixture]
public class SubreportLayoutTests
{
    private static string CrystalCmdSample(string name) =>
        Path.GetFullPath($"../../../../../../CrystalCmd/dotnet/Majorsilence.CrystalCmd.NetFrameworkServer/Majorsilence.CrystalCmd.Tests/{name}", AppContext.BaseDirectory);

    // The Crystal runtime reports a single-line border on all four sides of this sample's
    // subreport, and none on the public Top5USAsubCanada's.
    [Test]
    public void ASubreportObjectsBorder_IsRead()
    {
        string framed = CrystalCmdSample("thereport_with_subreport_with_dotnet_dataset.rpt");
        string unframed = Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Top5USAsubCanada.rpt", AppContext.BaseDirectory);
        Assume.That(File.Exists(framed) && File.Exists(unframed), Is.True, "needs a CrystalCmd checkout beside this one and the corpus");

        var withBorder = RptParser.Parse(framed).Report!.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>().Single().Format;
        var without = RptParser.Parse(unframed).Report!.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>().Single().Format;

        Assert.Multiple(() =>
        {
            Assert.That((withBorder.BorderLeft, withBorder.BorderTop, withBorder.BorderRight, withBorder.BorderBottom), Is.EqualTo(((byte)1, (byte)1, (byte)1, (byte)1)));
            Assert.That((without.BorderLeft, without.BorderTop, without.BorderRight, without.BorderBottom), Is.EqualTo(((byte)0, (byte)0, (byte)0, (byte)0)));
        });
    }

    private static ReportDefinition Child() => new()
    {
        ReportTitle = "Child",
        Sections = [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600, BackColor = "#808080", Objects = [
                new TextObject { Name = "Title", Text = "Title", Bounds = new(120, 120, 2880, 360) }] },
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600, Objects = [
                new TextObject { Name = "Label", Text = "Label", Bounds = new(0, 376, 1440, 224) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240 }]
    };

    private static ReportDefinition Parent(ObjectFormat subFormat) => new()
    {
        ReportTitle = "Parent",
        Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 5000, Objects = [
            new TextObject { Name = "Above", Text = "Above", Bounds = new(240, 240, 2250, 225) },
            new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(320, 680, 8000, 4000), Report = Child(), Format = subFormat }] }]
    };

    private static XElement Element(XDocument doc, string local, string name) =>
        doc.Descendants(doc.Root!.Name.Namespace + local).Single(e => (string?)e.Attribute("Name") == name);

    // A subreport goes in a Rectangle placed where the object is, which carries the frame,
    // and sits at the Rectangle's top left corner. The engine adds the Rectangle's offset to
    // the subreport's own, so a subreport repeating the Rectangle's Left would print 16pt
    // further right again. The frame is held in a Rectangle of its own that is placed where
    // the object is (see ASubreport_IsHeldToItsBandsBottom).
    [Test]
    public void AFreeFormSubreport_IsWrittenInAFrame_PlacedByItsTop()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Parent(new ObjectFormat { BorderLeft = 1, BorderTop = 1, BorderRight = 1, BorderBottom = 1 })));
        var ns = doc.Root!.Name.Namespace;
        var frame = Element(doc, "Rectangle", "Sub1_Frame");
        var room = Element(doc, "Rectangle", "Sub1_Room");
        var sub = frame.Descendants(ns + "Subreport").Single();

        Assert.Multiple(() =>
        {
            Assert.That(room.Element(ns + "Top")!.Value, Is.EqualTo("34pt"));
            Assert.That(room.Element(ns + "Left")!.Value, Is.EqualTo("16pt"));
            Assert.That(frame.Element(ns + "Top")!.Value, Is.EqualTo("0pt"));
            Assert.That(frame.Element(ns + "Left")!.Value, Is.EqualTo("0pt"));
            Assert.That(sub.Element(ns + "Top")!.Value, Is.EqualTo("0pt"));
            Assert.That(sub.Element(ns + "Left")!.Value, Is.EqualTo("0pt"), "the frame already places it across");
            Assert.That(frame.Element(ns + "Style")?.Element(ns + "BorderStyle")?.Element(ns + "Top")?.Value, Is.EqualTo("Solid"));
        });
    }

    // A subreport grows to hold what it prints and, like any growing object, moves nothing
    // else in its section: a bill's usage graph, run as a subreport, prints beside the charges
    // listed below its top. Its frame goes in a frameless Rectangle down to the band's bottom,
    // which is above nothing for the engine to move, and keeps its own height inside it.
    [Test]
    public void ASubreport_IsHeldToItsBandsBottom()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 2400, Objects = [
                new SubreportObject { Name = "Graph", SubreportName = "Graph", Bounds = new(240, 120, 4320, 300), Report = Child() },
                new TextObject { Name = "Charge", Text = "Charge", Bounds = new(5040, 600, 1440, 240) }] }]
        };

        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var room = Element(doc, "Rectangle", "Graph_Room");
        var frame = Element(doc, "Rectangle", "Graph_Frame");

        Assert.Multiple(() =>
        {
            Assert.That(frame.Parent!.Parent, Is.SameAs(room));
            Assert.That(room.Element(ns + "Top")!.Value, Is.EqualTo("6pt"));
            Assert.That(room.Element(ns + "Height")!.Value, Is.EqualTo("114pt"), "to the band's bottom");
            Assert.That(room.Element(ns + "Style"), Is.Null, "the Rectangle draws no frame of its own");
            Assert.That(frame.Element(ns + "Height")!.Value, Is.EqualTo("15pt"), "the frame keeps its own height");
        });
    }

    // A second subreport placed under a first keeps its place too: the Crystal runtime prints
    // it over the first's rows once they reach it. So the first is held to the band's bottom.
    [Test]
    public void ASubreportOverAnother_IsHeldToTheBandsBottom()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 3000, Objects = [
                new SubreportObject { Name = "Lower", SubreportName = "Lower", Bounds = new(0, 1800, 8000, 300), Report = Child() },
                new SubreportObject { Name = "Upper", SubreportName = "Upper", Bounds = new(0, 600, 8000, 300), Report = Child() }] }]
        };

        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var room = Element(doc, "Rectangle", "Upper_Room");

        Assert.Multiple(() =>
        {
            Assert.That(room.Element(ns + "Top")!.Value, Is.EqualTo("30pt"));
            Assert.That(room.Element(ns + "Height")!.Value, Is.EqualTo("120pt"), "to the band's bottom, 3000 twips");
            Assert.That(Element(doc, "Rectangle", "Lower_Room").Element(ns + "Height")!.Value, Is.EqualTo("60pt"), "the lower to the band's bottom");
        });
    }

    // A group footer's subreport that goes in one of the band's cells is written in a frame
    // as a free-form band's is, as wide as its object, not straight in the cell, where the
    // engine counted the page's left margin twice and sized it to the column.
    [Test]
    public void ASubreportInABandsCell_IsWrittenInAFrame()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Fields = [
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" },
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Qty", ColumnName = "Qty", TableName = "Orders", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Item", TableName = "Orders" }],
            Sections = [
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 2880, 240) },
                    new FieldObject { Name = "QtyField", FieldName = "Qty", Bounds = new(2880, 0, 2880, 240) }] },
                new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 400, Objects = [
                    new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 40, 2000, 300), Report = Child() }] }]
        };

        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var sub = Element(doc, "Subreport", "Sub1");
        var frame = sub.Parent!.Parent!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)frame.Attribute("Name"), Is.EqualTo("Sub1_Frame"));
            Assert.That(frame.Parent!.Parent!.Name.LocalName, Is.EqualTo("TableCell"));
            Assert.That(frame.Element(ns + "Top")!.Value, Is.EqualTo("2pt"), "at its depth in the band");
            Assert.That(frame.Element(ns + "Width")!.Value, Is.EqualTo("100pt"), "as wide as its 2000-twip object");
            Assert.That(sub.Element(ns + "Left")!.Value, Is.EqualTo("0pt"));
        });
    }

    [Test]
    public void ASubreportWithNoBorder_HasAFrameWithNoStyle()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Parent(new ObjectFormat())));
        var frame = Element(doc, "Rectangle", "Sub1_Frame");

        Assert.That(frame.Element(doc.Root!.Name.Namespace + "Style"), Is.Null);
    }

    // Object positions are relative to their own section, so Report Header b starts where
    // Report Header a ends, objects and backdrop alike.
    [Test]
    public void ASecondReportHeader_StartsBelowTheFirst()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Child()));
        var ns = doc.Root!.Name.Namespace;

        Assert.Multiple(() =>
        {
            Assert.That(Element(doc, "Textbox", "Title").Element(ns + "Top")!.Value, Is.EqualTo("6pt"));
            Assert.That(Element(doc, "Textbox", "Label").Element(ns + "Top")!.Value, Is.EqualTo("48.8pt"), "600 twips of the first header, then its own 376");
            Assert.That(doc.Descendants(ns + "Rectangle").Single(r => ((string?)r.Attribute("Name"))!.StartsWith("Backdrop_")).Element(ns + "Top")!.Value, Is.EqualTo("0pt"));
        });
    }

    [Test]
    public void ASecondReportHeadersBackdrop_StartsBelowTheFirst()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Two bands",
            Sections = [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 600, BackColor = "#808080" },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 400, BackColor = "#C0C0C0", Objects = [
                    new TextObject { Name = "Label", Text = "Label", Bounds = new(0, 0, 1440, 224) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240 }]
        };
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var tops = doc.Descendants(ns + "Rectangle").Where(r => ((string?)r.Attribute("Name"))!.StartsWith("Backdrop_"))
            .Select(r => r.Element(ns + "Top")!.Value).ToList();

        Assert.That(tops, Is.EqualTo(new[] { "0pt", "30pt" }));
    }
}
