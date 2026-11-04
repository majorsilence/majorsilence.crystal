using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A section set to underlay the sections after it: they print from its top, over it, and
/// what comes after them no higher than its bottom. A receipt's group header holds its
/// heading and, at its foot, its total; its lines print over it, under the heading, and the
/// group's footer and the next receipt below the total.
/// </summary>
[TestFixture]
public class UnderlayRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Receipts() => new()
    {
        ReportTitle = "Receipts",
        Fields =
        [
            new DatabaseField { Name = "T.Account", TableName = "T", ColumnName = "Account", DataType = "String" },
            new DatabaseField { Name = "T.Line", TableName = "T", ColumnName = "Line", DataType = "String" },
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "T" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 4000, Underlay = true, Objects =
            [
                new FieldObject { Name = "Heading", FieldName = "Group #1 Name", Bounds = new(0, 0, 2880, 240) },
                new TextObject { Name = "Foot", Text = "FOOT", Bounds = new(0, 3700, 2880, 240) }
            ] },
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [new FieldObject { Name = "LineField", FieldName = "Line", Bounds = new(3000, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 300, Objects =
                [new TextObject { Name = "Closing", Text = "CLOSING", Bounds = new(0, 0, 2880, 240) }] },
        ]
    };

    [Test]
    public async Task AnUnderlaidGroupHeader_HasItsLinesPrintedOverIt_AndItsFooterBelowIt()
    {
        var data = new DataTable();
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Line", typeof(string));
        data.Rows.Add("A1", "L1");
        data.Rows.Add("A1", "L2");
        data.Rows.Add("A2", "L3");

        string dir = Path.Combine(Path.GetTempPath(), "underlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Receipts(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            var texts = pages.Cast<Page>().First().Cast<PageItem>().OfType<PageText>().ToList();
            float Y(string text) => texts.First(t => t.Text == text).Y;
            float top = Y("A1");

            // Measured from the first heading, so the page's margins drop out: the lines from the
            // header's top, 300 twips (15pt) apart, the closing line 4,000 twips (200pt) down,
            // below the header's foot, and the next receipt 300 below that.
            Assert.Multiple(() =>
            {
                Assert.That(Y("L1") - top, Is.EqualTo(0f).Within(0.5f));
                Assert.That(Y("L2") - top, Is.EqualTo(15f).Within(0.5f));
                Assert.That(texts.Where(t => t.Text == "FOOT").Select(t => t.Y - top).First(), Is.EqualTo(185f).Within(0.5f));
                Assert.That(Y("CLOSING") - top, Is.EqualTo(200f).Within(0.5f));
                Assert.That(Y("A2") - top, Is.EqualTo(215f).Within(0.5f));
                Assert.That(Y("L3") - top, Is.EqualTo(215f).Within(0.5f));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
