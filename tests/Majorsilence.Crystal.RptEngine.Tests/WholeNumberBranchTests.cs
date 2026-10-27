using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A formula whose first branch is a whole number prints its other branch as Crystal prints
/// it: if IsNull({T.Amount}) then 0 else -{T.Amount} prints -853.75, and a ratio guarded
/// against a zero budget prints 115.43. The engine took the conditional's type from the 0 and
/// rounded the other branch to a whole number, -854.00 and 115.00, and IsNull of a null number
/// was never true.
/// </summary>
[TestFixture]
public class WholeNumberBranchTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Amounts",
        ConvertDatabaseNulls = false,
        ConvertOtherNulls = false,
        Fields = [
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" },
            new DatabaseField { Name = "Budget", ColumnName = "Budget", TableName = "T", DataType = "Float64" },
            new FormulaField { Name = "Credit", FormulaText = "if IsNull({T.Amount}) then 0 else {T.Amount} - ({T.Amount} * 2)", ResultType = "Float64" },
            new FormulaField { Name = "Ratio", FormulaText = "if {T.Budget} = 0 then 0 else {T.Amount} / {T.Budget} * 100", ResultType = "Float64" },
            new FormulaField { Name = "Band", FormulaText = "select {T.Budget} case 0: 0 default: {T.Amount} / 4", ResultType = "Float64" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "CreditField", FieldName = "@Credit", Bounds = new(0, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = "#,##0.00" } },
                new FieldObject { Name = "RatioField", FieldName = "@Ratio", Bounds = new(1440, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = "0.00'%'" } },
                new FieldObject { Name = "BandField", FieldName = "@Band", Bounds = new(2880, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = "#,##0.00" } }] }]
    };

    private static async Task<List<string>> Printed()
    {
        var t = new DataTable();
        t.Columns.Add("Amount", typeof(double));
        t.Columns.Add("Budget", typeof(double));
        t.Rows.Add(853.75, 739.6);
        t.Rows.Add(DBNull.Value, 0.0);

        string dir = Path.Combine(Path.GetTempPath(), "wholenumber-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = t }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(x => x.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AWholeNumberBranch_KeepsTheOtherBranchsFraction()
    {
        var printed = await Printed();
        string all = string.Join(" | ", printed);
        Assert.Multiple(() =>
        {
            Assert.That(printed, Does.Contain("-853.75"), all);
            Assert.That(printed, Does.Contain("115.43%"), all);
            Assert.That(printed, Does.Contain("213.44"), all);
            // The null amount is null to IsNull, and its credit prints 0.00, as its band does.
            Assert.That(printed.Count(p => p == "0.00"), Is.EqualTo(2), all);
            Assert.That(printed, Does.Contain("0.00%"), all);
        });
    }
}
