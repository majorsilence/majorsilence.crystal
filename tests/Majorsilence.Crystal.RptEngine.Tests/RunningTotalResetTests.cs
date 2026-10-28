using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A running total that starts again when a field changes, or whose reset formula asks
/// whether the field changed from the record before, starts again with each account where the
/// report groups on the account, as Crystal's does: the record on which the account changes is
/// the first of the new run. It ran on over the whole report.
/// </summary>
[TestFixture]
public class RunningTotalResetTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Account Runs",
        Fields =
        [
            new DatabaseField { Name = "T.Region", TableName = "T", ColumnName = "Region", DataType = "String" },
            new DatabaseField { Name = "T.Account", TableName = "T", ColumnName = "Account", DataType = "String" },
            new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
            new RunningTotalField { Name = "ByField", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum,
                ResetOn = RunningTotalCondition.OnChangeOfField, ResetCondition = "T.Account" },
            new RunningTotalField { Name = "ByFormula", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum,
                ResetOn = RunningTotalCondition.OnFormula, ResetCondition = "{T.Account} <> Previous ({T.Account})" },
        ],
        Groups =
        [
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" },
            new GroupDefinition { Level = 1, FieldName = "Account", TableName = "T" },
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
            [
                new FieldObject { Name = "Line", FieldName = "#ByField", Bounds = new(0, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = "0.00" } },
                new FieldObject { Name = "Again", FieldName = "#ByFormula", Bounds = new(1440, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = "0.00" } },
            ] },
        ]
    };

    [Test]
    public async Task AResetOnAFieldStartsAgainWithEachGroupOnIt()
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Amount", typeof(double));
        // Out of order, so the groups' sort is what brings each account's records together.
        data.Rows.Add("South", "B1", 8.0);
        data.Rows.Add("North", "A1", 1.0);
        data.Rows.Add("North", "A2", 2.0);
        data.Rows.Add("South", "B1", 16.0);
        data.Rows.Add("North", "A1", 4.0);

        string dir = Path.Combine(Path.GetTempPath(), "rtreset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => t.Text).Where(t => t.Length > 0 && t != "Account Runs").ToList();
            // Each record's two running totals, in account order.
            Assert.That(string.Join(" ", printed), Is.EqualTo(
                "1.00 1.00 5.00 5.00 2.00 2.00 8.00 8.00 24.00 24.00"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
