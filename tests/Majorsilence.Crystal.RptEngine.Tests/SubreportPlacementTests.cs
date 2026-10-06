using System.Data;
using System.Xml.Linq;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Where a subreport lands on the engine's page (#37): at its own position, below an item
/// above it, and as wide as its placed object.
/// </summary>
[TestFixture]
public class SubreportPlacementTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Child() => new()
    {
        ReportTitle = "Child",
        Fields = [new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" }],
        Sections = [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600, BackColor = "#808080", Objects = [
                new TextObject { Name = "ChildTitle", Text = "CHILD-TITLE", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 2880, 240) }] }]
    };

    // An item above the subreport in the same section, with a gap between them, which the
    // engine dropped: the subreport's content printed right under "Above".
    private static ReportDefinition Parent() => new()
    {
        ReportTitle = "Parent",
        Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 5000, Objects = [
            new TextObject { Name = "Above", Text = "ABOVE", Bounds = new(240, 240, 2250, 225) },
            new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(320, 680, 8000, 4000), Report = Child() }] }]
    };

    [Test]
    public async Task ASubreport_PrintsAtItsOwnPosition_BelowTheItemAboveIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "subplacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A subreport draws only when its dataset has a row (Reporting #345), so it gets one.
            var items0 = new DataTable();
            items0.Columns.Add("Item", typeof(string));
            items0.Rows.Add("ONE");
            using var report = await ReportEngine.LoadAsync(Parent(), new RuntimeOverrides { SubreportData = { ["Sub1"] = items0 } }, dir, []);
            using var pages = await report.BuildPages();
            var items = pages.Cast<Page>().First().Cast<PageItem>().ToList();
            var above = items.OfType<PageText>().Single(t => t.Text == "ABOVE");
            var childTitle = items.OfType<PageText>().Single(t => t.Text == "CHILD-TITLE");

            // The subreport object is 680 twips (34pt) below the section top and 320 twips (16pt)
            // in; "ABOVE" is 240 twips (12pt) below and 240 in. Both are measured from where the
            // engine put "ABOVE", so the page's margins drop out.
            Assert.Multiple(() =>
            {
                Assert.That(childTitle.Y - above.Y, Is.EqualTo(34f - 12f).Within(0.5f));
                Assert.That(childTitle.X - above.X, Is.EqualTo(16f - 12f).Within(0.5f));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ASubreportCompanion_IsAsWideAsItsPlacedObject_AndTheModelIsLeftAlone()
    {
        var parent = Parent();
        var child = parent.Sections[0].Objects.OfType<SubreportObject>().Single().Report!;
        int storedWidth = child.Page.WidthTwips;

        var (_, companions) = RenderPrep.ConvertWithSubreports(parent);
        var doc = XDocument.Parse(companions.Values.Single());
        var ns = doc.Root!.Name.Namespace;
        var backdrop = doc.Descendants(ns + "Rectangle").Single(r => ((string?)r.Attribute("Name"))!.StartsWith("Backdrop_"));

        Assert.Multiple(() =>
        {
            Assert.That(backdrop.Element(ns + "Width")!.Value, Is.EqualTo("400pt"), "the 8000-twip object, with no side margins");
            Assert.That(child.Page.WidthTwips, Is.EqualTo(storedWidth));
        });
    }
}
