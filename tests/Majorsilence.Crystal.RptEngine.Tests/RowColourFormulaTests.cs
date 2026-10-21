using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A Details band coloured by a formula on the record number colours its rows alternately,
/// as worrall__AlphaISOsByCountry's does: "select RecordNumber mod 2 case 0: crYellow
/// case 1: crLime". The Select becomes a Switch that reads RowNumber once per case, and the
/// engine's RowNumber counted its own evaluations until Reporting 27.0.0-alpha.4, so the
/// second case fell behind and every fourth row was left uncoloured.
/// </summary>
[TestFixture]
public class RowColourFormulaTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Colours",
        Fields = [new DatabaseField { Name = "T.Name", TableName = "T", ColumnName = "Name", DataType = "String" }],
        Sections =
        [
            new Section
            {
                Type = SectionType.Details, HeightTwips = 300, BackColor = "#FFFF00",
                BackColorFormula = "select RecordNumber mod 2\ncase 0: crYellow\ncase 1: crLime",
                Objects = [new FieldObject { FieldName = "Name", Bounds = new(0, 0, 1440, 240) }],
            },
        ]
    };

    [Test]
    [Ignore("Needs Majorsilence.Reporting 27.0.0-alpha.4, where RowNumber is the row's position rather than a count of its evaluations.")]
    public async Task RecordNumberModTwo_AlternatesLimeAndYellow()
    {
        var data = new DataTable();
        data.Columns.Add("Name", typeof(string));
        foreach (var n in new[] { "r1", "r2", "r3", "r4", "r5", "r6" }) data.Rows.Add(n);

        string dir = Path.Combine(Path.GetTempPath(), "rowcolour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            var rows = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => t.Text.StartsWith('r')).OrderBy(t => t.Y).ToList();

            Assert.That(rows.Select(t => t.Text), Is.EqualTo(new[] { "r1", "r2", "r3", "r4", "r5", "r6" }));
            Assert.That(rows.Select(t => t.SI.BackgroundColor.Name),
                Is.EqualTo(new[] { "Lime", "Yellow", "Lime", "Yellow", "Lime", "Yellow" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
