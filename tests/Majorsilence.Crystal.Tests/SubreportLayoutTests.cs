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

    // The engine places a subreport by the item above it rather than by its own Top, and
    // ignores its container when placing it across, so it goes in a Rectangle placed by its
    // Top, keeping its own Left; the Rectangle carries the frame.
    [Test]
    public void AFreeFormSubreport_IsWrittenInAFrame_PlacedByItsTop()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Parent(new ObjectFormat { BorderLeft = 1, BorderTop = 1, BorderRight = 1, BorderBottom = 1 })));
        var ns = doc.Root!.Name.Namespace;
        var frame = Element(doc, "Rectangle", "Sub1_Frame");
        var sub = frame.Descendants(ns + "Subreport").Single();

        Assert.Multiple(() =>
        {
            Assert.That(frame.Element(ns + "Top")!.Value, Is.EqualTo("34pt"));
            Assert.That(frame.Element(ns + "Left")!.Value, Is.EqualTo("16pt"));
            Assert.That(sub.Element(ns + "Top")!.Value, Is.EqualTo("0pt"));
            Assert.That(sub.Element(ns + "Left")!.Value, Is.EqualTo("16pt"), "the engine reads it from the margin, not the frame");
            Assert.That(frame.Element(ns + "Style")?.Element(ns + "BorderStyle")?.Element(ns + "Top")?.Value, Is.EqualTo("Solid"));
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
