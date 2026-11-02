using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A number whose Currency Symbol formula gives its symbol, and a growing field drawn shorter
/// than its line, rendered.
/// </summary>
[TestFixture]
public class CurrencyAndGrowthRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static DataTable Rows(params (string Note, double Amount)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("Note");
        t.Columns.Add("Amount", typeof(double));
        foreach (var (note, amount) in rows)
            t.Rows.Add(note, amount);
        return t;
    }

    private static ReportDefinition Report(ObjectFormat amountFormat, ObjectFormat? noteFormat = null, int rowTwips = 300) => new()
    {
        ReportTitle = "Ledger",
        Fields = [
            new DatabaseField { Name = "Note", ColumnName = "Note", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" },
            new ParameterField { Name = "Currency", DataType = "String" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = rowTwips, Objects = [
                new FieldObject { Name = "NoteField", FieldName = "Note", Bounds = new(0, 0, 2880, Math.Min(rowTwips, 240)), Format = noteFormat },
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(3600, 0, 1440, 240), Format = amountFormat }] }]
    };

    private static async Task<List<PageText>> Texts(ReportDefinition definition, DataTable data,
        Dictionary<string, string>? parameters = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "currency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var overrides = new RuntimeOverrides { Data = data };
            foreach (var (name, value) in parameters ?? [])
                overrides.Parameters[name] = value;
            using var report = await ReportEngine.LoadAsync(definition, overrides, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>().OfType<PageText>())
                .Where(t => t.Text.Trim().Length > 0).OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // "{?Currency}" as the symbol prints the parameter's text where the format holds "$", and
    // nothing where the parameter is empty.
    [TestCase("A", "A1,234.50")]
    [TestCase("", "1,234.50")]
    public async Task ACurrencySymbolFormula_PrintsItsSymbol(string currency, string expected)
    {
        var format = new ObjectFormat
        {
            FormatString = "\"$\"#,##0.00",
            CurrencySymbolFormula = "{?Currency}",
            CurrencySymbolType = 1,
            FormatsBySymbolType = ["#,##0.00", "\"\u0001\"#,##0.00", "\"\u0001\"#,##0.00"],
        };
        var texts = await Texts(Report(format), Rows(("first", 1234.5)), new() { ["Currency"] = currency });

        Assert.That(texts.Select(t => t.Text.Trim()), Does.Contain(expected));
    }

    // A growing field of Arial 10, drawn 180 twips tall at the foot of its row, makes the row
    // its 12pt line in Crystal: the rows print 12pt apart, not 9pt.
    [Test]
    public async Task AGrowingFieldShorterThanItsLine_SpacesTheRowsByTheLine()
    {
        var note = new ObjectFormat { FontName = "Arial", FontSize = 10, CanGrow = true };
        var texts = await Texts(Report(new ObjectFormat(), note, rowTwips: 180), Rows(("first", 1), ("second", 2)));
        var first = texts.Single(t => t.Text.Trim() == "first");
        var second = texts.Single(t => t.Text.Trim() == "second");

        Assert.That(second.Y - first.Y, Is.EqualTo(12).Within(0.1));
    }
}
