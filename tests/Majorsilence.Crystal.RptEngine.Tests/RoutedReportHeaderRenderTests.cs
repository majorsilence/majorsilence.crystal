using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Report headers that need a data scope, each a table of its own, rendered end to end: each
/// starts where the one above it ends, right below it as designed, and further down when a
/// field above it grows, as Crystal prints report headers one after another.
/// </summary>
[TestFixture]
public class RoutedReportHeaderRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    // Three report headers of 72pt, 30pt and 30pt, each with a field, so each is a table: the
    // print date, a note that may grow, and a label beside a second print date.
    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Headers",
        Fields = [new DatabaseField { Name = "T.Note", TableName = "T", ColumnName = "Note", DataType = "String" }],
        Sections =
        [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                Objects = [new FieldObject { FieldName = "Print Date", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600,
                Objects = [new FieldObject { FieldName = "Note", Bounds = new(0, 0, 1440, 240),
                    Format = new ObjectFormat { CanGrow = true } }] },
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600,
                Objects = [new TextObject { Text = "Third", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { FieldName = "Print Date", Bounds = new(2880, 0, 1440, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240 },
        ]
    };

    private static async Task<List<PageText>> Printed(string note)
    {
        var data = new DataTable();
        data.Columns.Add("Note", typeof(string));
        data.Rows.Add(note);

        string dir = Path.Combine(Path.GetTempPath(), "routedheaders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().First().Cast<PageItem>().OfType<PageText>()
                .Where(t => !string.IsNullOrEmpty(t.Text)).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Each table is designed as tall as it prints. Its Details row, there only for the data
    // scope, never prints; at 1pt it made the first header's table 1pt taller than its band,
    // and the engine, which keeps the designed space between an item and the one above it,
    // printed the third header 1pt high.
    [Test]
    public async Task EachHeader_StartsWhereTheOneAboveItEnds()
    {
        var printed = await Printed("short");
        PageText At(string text) => printed.Single(t => t.Text == text);

        Assert.Multiple(() =>
        {
            Assert.That(At("short").Y - printed.Min(t => t.Y), Is.EqualTo(72).Within(0.05));
            Assert.That(At("Third").Y - printed.Min(t => t.Y), Is.EqualTo(102).Within(0.05));
        });
    }

    // The note grows to several lines, and the header below it moves down with it. With the
    // tables 1pt taller than their bands the third overlapped the second, so it was not placed
    // from it, and printed over the note.
    [Test]
    public async Task AHeaderThatGrows_PushesTheNextOneDown()
    {
        string note = string.Join(" ", Enumerable.Repeat("a note that wraps", 12));
        var printed = await Printed(note);
        var grown = printed.Single(t => t.Text == note);

        Assert.That(printed.Single(t => t.Text == "Third").Y, Is.GreaterThanOrEqualTo(grown.Y + grown.H - 0.05f));
    }
}
