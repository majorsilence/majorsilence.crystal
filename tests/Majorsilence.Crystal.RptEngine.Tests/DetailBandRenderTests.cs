using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Several Details sections rendered end to end (#7): each record prints one line per band,
/// in band order, each object at its own Left and its band's own Top, and a band whose
/// suppress formula is true for a record is left out of that record alone.
/// </summary>
[TestFixture]
public class DetailBandRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static FieldObject Field(string name, int left, int width, int top = 0) =>
        new() { FieldName = name, Bounds = new(left, top, width, 240) };

    // Three bands of 300 twips (15pt): code and name, then the note across both, then the
    // kind - which is suppressed where it says "hide".
    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Bands",
        Fields =
        [
            new DatabaseField { Name = "T.Code", TableName = "T", ColumnName = "Code", DataType = "String" },
            new DatabaseField { Name = "T.Name", TableName = "T", ColumnName = "Name", DataType = "String" },
            new DatabaseField { Name = "T.Note", TableName = "T", ColumnName = "Note", DataType = "String" },
            new DatabaseField { Name = "T.Kind", TableName = "T", ColumnName = "Kind", DataType = "String" },
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [Field("Code", 0, 1400), Field("Name", 2880, 2000)] },
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [Field("Note", 1440, 4320)] },
            new Section { Type = SectionType.Details, HeightTwips = 300, Suppress = true,
                SuppressFormula = "{T.Kind} = 'hide'", Objects = [Field("Kind", 0, 1400)] },
        ]
    };

    private static async Task<List<PageText>> Printed()
    {
        var data = new DataTable();
        foreach (var c in new[] { "Code", "Name", "Note", "Kind" }) data.Columns.Add(c, typeof(string));
        data.Rows.Add("A1", "Alpha", "first note", "show");
        data.Rows.Add("B2", "Beta", "second note", "hide");

        string dir = Path.Combine(Path.GetTempPath(), "detailbands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => !string.IsNullOrEmpty(t.Text)).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task EachRecord_PrintsOneLinePerBand_InBandOrder()
    {
        var printed = await Printed();

        var lines = printed.GroupBy(t => Math.Round(t.Y, 1)).OrderBy(g => g.Key)
            .Select(g => string.Join(" ", g.OrderBy(t => t.X).Select(t => t.Text))).ToList();

        Assert.That(lines, Is.EqualTo(new[]
        {
            "A1 Alpha", "first note", "show",
            "B2 Beta", "second note",
        }));
    }

    [Test]
    public async Task EachObject_SitsAtItsOwnLeft_AndEachBandAtItsOwnPitch()
    {
        var printed = await Printed();
        PageText At(string text) => printed.Single(t => t.Text == text);

        Assert.Multiple(() =>
        {
            // 1440 and 2880 twips right of the code: 72pt and 144pt.
            Assert.That(At("first note").X - At("A1").X, Is.EqualTo(72).Within(0.5));
            Assert.That(At("Alpha").X - At("A1").X, Is.EqualTo(144).Within(0.5));
            Assert.That(At("show").X - At("A1").X, Is.EqualTo(0).Within(0.5));
            // A band below each other, 15pt apart; the second record after all three.
            Assert.That(At("first note").Y - At("A1").Y, Is.EqualTo(15).Within(0.5));
            Assert.That(At("show").Y - At("A1").Y, Is.EqualTo(30).Within(0.5));
            Assert.That(At("B2").Y - At("A1").Y, Is.EqualTo(45).Within(0.5));
            Assert.That(At("second note").Y - At("B2").Y, Is.EqualTo(15).Within(0.5));
        });
    }
}
