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
            // No data at all: the subreport prints its header with no rows, as Crystal's does (#38).
            using var report = await ReportEngine.LoadAsync(Parent(), new RuntimeOverrides(), dir, []);
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

    // A subreport grows to hold its rows and moves nothing else in its section, as Crystal's
    // does: a bill's usage graph prints beside the charges listed below its top. The engine
    // moved every item below a growing one down by all of its growth, so the charges printed
    // below the graph.
    [Test]
    public async Task AGrowingSubreport_MovesNothingBesideIt()
    {
        var parent = new ReportDefinition
        {
            ReportTitle = "Parent",
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 4000, Objects = [
                new TextObject { Name = "Above", Text = "ABOVE", Bounds = new(5040, 0, 1440, 240) },
                new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(240, 120, 4320, 300), Report = Child() },
                new TextObject { Name = "Beside", Text = "BESIDE", Bounds = new(5040, 720, 1440, 240) }] }]
        };
        var rows = new DataTable();
        rows.Columns.Add("Item", typeof(string));
        for (int i = 0; i < 8; i++)
            rows.Rows.Add($"Item{i}");
        var overrides = new RuntimeOverrides();
        overrides.SubreportData["Sub1"] = rows;

        string dir = Path.Combine(Path.GetTempPath(), "subbeside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(parent, overrides, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>().First().Cast<PageItem>().OfType<PageText>().ToList();
            var above = texts.Single(t => t.Text == "ABOVE");
            var beside = texts.Single(t => t.Text == "BESIDE");

            Assert.That(texts.Count(t => t.Text.StartsWith("Item")), Is.EqualTo(8), "the subreport grew to hold its rows");
            Assert.That(beside.Y - above.Y, Is.EqualTo(36f).Within(0.5f), "720 twips below, where it is placed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Crystal prints a subreport's report header and footer when it has no rows, and its
    // details once, their fields blank. The engine drew nothing in its place until each
    // companion carried a dataset with one row that no data region reads (#38). The report
    // footer, which the
    // converter writes into the details table's footer band, printed only from Reporting 27,
    // which draws a table's header and footer when it has no rows and no NoRows message.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ASubreportWithNoRows_PrintsItsHeaderAndFooter_AndBlankDetails(bool emptyTable)
    {
        var child = Child();
        child.Sections.Add(new Section { Type = SectionType.ReportFooter, HeightTwips = 300, Objects = [
            new TextObject { Name = "ChildFooter", Text = "CHILD-FOOTER", Bounds = new(0, 0, 2880, 240) }] });
        var parent = Parent();
        var sub = parent.Sections[0].Objects.OfType<SubreportObject>().Single();
        sub.Report = child;

        var overrides = new RuntimeOverrides();
        if (emptyTable)
        {
            var none = new DataTable();
            none.Columns.Add("Item", typeof(string));
            overrides.SubreportData["Sub1"] = none;
        }

        string dir = Path.Combine(Path.GetTempPath(), "subnorows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(parent, overrides, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();

            Assert.That(texts, Does.Contain("ABOVE"));
            Assert.That(texts.Count(t => t == "CHILD-TITLE"), Is.EqualTo(1), "the subreport's report header, once");
            Assert.That(texts.Count(t => t == "CHILD-FOOTER"), Is.EqualTo(1), "its report footer, once");
            Assert.That(texts.IndexOf("CHILD-FOOTER"), Is.GreaterThan(texts.IndexOf("CHILD-TITLE")), "below its header");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A subreport given no rows makes the pass a report with no rows makes: the Crystal
    // runtime prints its group header, Details and group footer once, between its report
    // header and footer, their fields blank. With rows each prints as often as it would.
    [TestCase(0)]
    [TestCase(2)]
    public async Task AGroupedSubreport_WithNoRows_PrintsItsGroupSectionsOnce(int rows)
    {
        var child = new ReportDefinition
        {
            ReportTitle = "GroupedChild",
            Fields = [new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Item", TableName = "Orders" }],
            Sections = [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 300, Objects = [
                    new TextObject { Name = "ChildTitle", Text = "CHILD-TITLE", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.GroupHeader, HeightTwips = 300, GroupLevel = 0, Objects = [
                    new TextObject { Name = "ChildGroup", Text = "CHILD-GROUP", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 300, Objects = [
                    new TextObject { Name = "ChildLine", Text = "CHILD-LINE", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(2880, 0, 2880, 240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 300, GroupLevel = 0, Objects = [
                    new TextObject { Name = "ChildTotal", Text = "CHILD-TOTAL", Bounds = new(0, 0, 2880, 240) }] }]
        };
        var parent = Parent();
        parent.Sections[0].Objects.OfType<SubreportObject>().Single().Report = child;

        var data = new DataTable();
        data.Columns.Add("Item", typeof(string));
        for (int i = 0; i < rows; i++)
            data.Rows.Add($"Item{i}");
        var overrides = new RuntimeOverrides();
        overrides.SubreportData["Sub1"] = data;

        string dir = Path.Combine(Path.GetTempPath(), "subgroups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(parent, overrides, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();

            int each = Math.Max(rows, 1);
            Assert.Multiple(() =>
            {
                Assert.That(texts.Count(t => t == "CHILD-TITLE"), Is.EqualTo(1));
                Assert.That(texts.Count(t => t == "CHILD-GROUP"), Is.EqualTo(each));
                Assert.That(texts.Count(t => t == "CHILD-LINE"), Is.EqualTo(each));
                Assert.That(texts.Count(t => t == "CHILD-TOTAL"), Is.EqualTo(each));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Crystal cuts a subreport's contents at the right edge of the object it is placed in: a
    // receipt laid out across the page, placed in a narrow subreport, prints its first column
    // and the start of its second. What starts past the edge does not print, and a line across
    // it ends there. The model is left as it was.
    [Test]
    public async Task ASubreport_PrintsNothingPastItsObjectsRightEdge()
    {
        var child = new ReportDefinition
        {
            ReportTitle = "Child",
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 600, Objects = [
                new TextObject { Name = "Inside", Text = "INSIDE", Bounds = new(0, 0, 1440, 240) },
                new TextObject { Name = "Past", Text = "PAST", Bounds = new(4320, 0, 1440, 240) },
                new LineObject { Name = "Rule", Bounds = new(0, 360, 5760, 0) }] }]
        };
        var parent = new ReportDefinition
        {
            ReportTitle = "Parent",
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 2000, Objects = [
                new TextObject { Name = "Above", Text = "ABOVE", Bounds = new(0, 0, 1440, 240) },
                new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 480, 2880, 700), Report = child }] }]
        };

        string dir = Path.Combine(Path.GetTempPath(), "subclip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(parent, new RuntimeOverrides(), dir, []);
            using var pages = await report.BuildPages();
            var items = pages.Cast<Page>().First().Cast<PageItem>().ToList();
            var texts = items.OfType<PageText>().Select(t => t.Text).ToList();
            var above = items.OfType<PageText>().Single(t => t.Text == "ABOVE");
            var rule = items.OfType<PageLine>().Single();

            Assert.Multiple(() =>
            {
                Assert.That(texts, Does.Contain("INSIDE"));
                Assert.That(texts, Does.Not.Contain("PAST"), "starts past the 2in object's edge");
                Assert.That(rule.X2 - above.X, Is.EqualTo(144f).Within(0.5f), "the 4in line ends at the edge");
                Assert.That(child.Sections[0].Objects.Count, Is.EqualTo(3));
                Assert.That(child.Sections[0].Objects.OfType<LineObject>().Single().Bounds.Width, Is.EqualTo(5760));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A group footer's subreport the converter puts in one of the band's cells prints where it
    // is placed: at the band's left, as the details' first field is.
    [Test]
    public async Task ASubreportInABandsCell_PrintsAtTheBandsLeft()
    {
        var parent = new ReportDefinition
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
                    new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 0, 2000, 300), Report = Child() }] }]
        };
        var rows = new DataTable();
        rows.Columns.Add("Item", typeof(string));
        rows.Columns.Add("Qty", typeof(string));
        rows.Rows.Add("Widget", "3");
        var overrides = new RuntimeOverrides { Data = rows };

        string dir = Path.Combine(Path.GetTempPath(), "subcell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(parent, overrides, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>().First().Cast<PageItem>().OfType<PageText>().ToList();
            var item = texts.Single(t => t.Text == "Widget");
            var childTitle = texts.Single(t => t.Text == "CHILD-TITLE");

            Assert.That(childTitle.X - item.X, Is.EqualTo(0f).Within(1f));
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
