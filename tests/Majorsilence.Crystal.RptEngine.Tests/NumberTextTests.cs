using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A number a formula turns into text prints as Crystal's ToText prints it: grouped
/// thousands whether or not the decimals are given, "1,045.00 * 1,147.00 * (15/30) =", and
/// two decimals for a number formula or parameter. A Date parameter prints its date and no
/// time, in a formula and on the page.
/// </summary>
[TestFixture]
public class NumberTextTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Demand",
        Fields = [
            new DatabaseField { Name = "Step", ColumnName = "Step", TableName = "T", DataType = "Float64" },
            new DatabaseField { Name = "Rate", ColumnName = "Rate", TableName = "T", DataType = "Float64" },
            new DatabaseField { Name = "Days", ColumnName = "Days", TableName = "T", DataType = "Int32" },
            new ParameterField { Name = "Period", DataType = "Float64" },
            new ParameterField { Name = "Bill Date", DataType = "DateTime", DateOnly = true },
            new FormulaField { Name = "Charge", FormulaText = "{T.Step} * {T.Rate}", ResultType = "Float64" },
            new FormulaField { Name = "Header", ResultType = "String", FormulaText =
                "ToText({T.Step}, 2) + ' * ' + ToText({T.Rate}, 2) + ' * (' + ToText({T.Days}, 0) + '/' + ToText({?Period}, 0) + ') ='" },
            new FormulaField { Name = "Total", ResultType = "String", FormulaText = "'Total ' + ToText({@Charge}) + ' for ' + ToText({?Period})" },
            new FormulaField { Name = "Dated", ResultType = "String", FormulaText = "'Billed ' + ToText({?Bill Date})" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "HeaderField", FieldName = "@Header", Bounds = new(0, 0, 4320, 240) },
                new FieldObject { Name = "TotalField", FieldName = "@Total", Bounds = new(4320, 0, 4320, 240) }] },
            new Section { Type = SectionType.ReportFooter, HeightTwips = 240, Objects = [
                new FieldObject { Name = "DatedField", FieldName = "@Dated", Bounds = new(0, 0, 4320, 240) },
                new TextObject { Name = "Due", Text = "Due {?Bill Date}", Bounds = new(4320, 0, 4320, 240) }] }]
    };

    [Test]
    public async Task ANumberTurnedIntoText_PrintsAsCrystalsToText()
    {
        var t = new DataTable();
        t.Columns.Add("Step", typeof(double));
        t.Columns.Add("Rate", typeof(double));
        t.Columns.Add("Days", typeof(int));
        t.Rows.Add(1045.0, 1147.0, 15);

        string dir = Path.Combine(Path.GetTempPath(), "numbertext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        List<string> printed;
        try
        {
            var overrides = new RuntimeOverrides
            {
                Data = t,
                Parameters = { ["Period"] = 30.0, ["Bill Date"] = new DateTime(2024, 1, 1) },
            };
            using var report = await ReportEngine.LoadAsync(Report(), overrides, dir, []);
            using var pages = await report.BuildPages();
            printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(x => x.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        string all = string.Join(" | ", printed);
        string date = new DateTime(2024, 1, 1).ToString("d");
        Assert.Multiple(() =>
        {
            Assert.That(printed, Does.Contain("1,045.00 * 1,147.00 * (15/30) ="), all);
            Assert.That(printed, Does.Contain("Total 1,198,615.00 for 30.00"), all);
            Assert.That(printed, Does.Contain("Billed " + date), all);
            Assert.That(printed, Does.Contain("Due " + date), all);
        });
    }
}
