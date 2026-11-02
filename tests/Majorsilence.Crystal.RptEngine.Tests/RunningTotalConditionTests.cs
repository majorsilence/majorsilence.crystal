using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A running total evaluated on a formula adds only the records the formula picks, whatever
/// its operation, as Crystal's does: a count of a number field, a distinct count, an average and
/// a maximum. The count printed blank, since the engine's IIf failed converting the records
/// left out to the field's type. A formula's If with no Else gives a number field's 0 where
/// its test fails; it printed blank too.
/// </summary>
[TestFixture]
public class RunningTotalConditionTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static RunningTotalField Total(string name, string field, AggregateFunction function) => new()
    {
        Name = name, SummarizedFieldName = field, Function = function,
        EvaluateOn = RunningTotalCondition.OnFormula, EvaluateCondition = "{T.Kind} = 'Fee'",
    };

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Fee Runs",
        Fields =
        [
            new DatabaseField { Name = "T.Kind", TableName = "T", ColumnName = "Kind", DataType = "String" },
            new DatabaseField { Name = "T.Payer", TableName = "T", ColumnName = "Payer", DataType = "String" },
            new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
            Total("Fees", "Amount", AggregateFunction.Count),
            Total("Payers", "Payer", AggregateFunction.DistinctCount),
            Total("Mean", "Amount", AggregateFunction.Average),
            Total("Largest", "Amount", AggregateFunction.Maximum),
            new FormulaField { Name = "FeeAmount", FormulaText = "if {T.Kind} = 'Fee' then {T.Amount}", ResultType = "Float64" },
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
            [
                .. new[] { "#Fees", "#Payers", "#Mean", "#Largest", "@FeeAmount" }.Select((f, i) => (ReportObject)new FieldObject
                {
                    Name = "Show" + f[1..], FieldName = f, Bounds = new(i * 1440, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = f is "#Fees" or "#Payers" ? "0" : "0.00" },
                }),
            ] },
        ]
    };

    [Test]
    public async Task OnlyTheRecordsTheFormulaPicksAreAdded()
    {
        var data = new DataTable();
        data.Columns.Add("Kind", typeof(string));
        data.Columns.Add("Payer", typeof(string));
        data.Columns.Add("Amount", typeof(double));
        data.Rows.Add("Fee", "P1", 4.0);
        data.Rows.Add("Tax", "P2", 100.0);
        data.Rows.Add("Fee", "P1", 8.0);
        data.Rows.Add("Fee", "P3", 6.0);

        string dir = Path.Combine(Path.GetTempPath(), "rtcond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            var lines = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => t.Text.Length > 0 && t.Text != "Fee Runs")
                .GroupBy(t => t.Y).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(t => t.X).Select(t => t.Text))).ToList();

            // The tax line adds nothing: the totals stay as they were, and its amount is 0.
            Assert.That(lines, Is.EqualTo(new[]
            {
                "1 1 4.00 4.00 4.00",
                "1 1 4.00 4.00 0.00",
                "2 1 6.00 8.00 8.00",
                "3 2 6.00 8.00 6.00",
            }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
