using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A running total evaluated on a change of group adds the first record of each instance of
/// the group, as Crystal's does: an account repeated in a new month is an instance of its own,
/// and so is a record with no account. It added every record.
/// </summary>
[TestFixture]
public class RunningTotalEvaluateOnGroupTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static RunningTotalField Total(string name, AggregateFunction function, int group, int reset) => new()
    {
        Name = name, SummarizedFieldName = "Amount", Function = function,
        EvaluateOn = RunningTotalCondition.OnChangeOfGroup, EvaluateGroup = group,
        ResetOn = RunningTotalCondition.OnChangeOfGroup, ResetGroup = reset,
    };

    private static ReportDefinition Report(GroupDefinition[] groups, params RunningTotalField[] totals) => new()
    {
        ReportTitle = "Account Firsts",
        Fields =
        [
            new DatabaseField { Name = "T.Region", TableName = "T", ColumnName = "Region", DataType = "String" },
            new DatabaseField { Name = "T.Posted", TableName = "T", ColumnName = "Posted", DataType = "DateTime" },
            new DatabaseField { Name = "T.Account", TableName = "T", ColumnName = "Account", DataType = "String" },
            new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
            .. totals,
        ],
        Groups = [.. groups],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
            [
                .. totals.Select((t, i) => (ReportObject)new FieldObject { Name = "Show" + t.Name, FieldName = "#" + t.Name,
                    Bounds = new(i * 1440, 0, 1440, 240),
                    Format = new ObjectFormat { FormatString = t.Function == AggregateFunction.Count ? "0" : "0.00" } }),
            ] },
        ]
    };

    private static GroupDefinition Group(int level, string field, GroupCondition condition = GroupCondition.EachValue) =>
        new() { Level = level, FieldName = field, TableName = "T", Condition = condition };

    private static DataTable Data(params (string Region, DateTime Posted, string? Account, double Amount)[] rows)
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Posted", typeof(DateTime));
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Amount", typeof(double));
        foreach (var r in rows)
            data.Rows.Add(r.Region, r.Posted, r.Account, r.Amount);
        return data;
    }

    private static async Task<List<string>> Lines(ReportDefinition definition, DataTable data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rteval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(definition, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => t.Text.Length > 0 && t.Text != "Account Firsts")
                .GroupBy(t => t.Y).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(t => t.X).Select(t => t.Text))).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task EachInstanceOfTheGroupAddsItsFirstRecord()
    {
        var day = new DateTime(2024, 1, 5);
        // Out of order, so the groups' sort is what brings each account's records together.
        var data = Data(("South", day, "A1", 8.0), ("North", day, "A1", 1.0), ("North", day, "A2", 4.0),
            ("South", day, null, 32.0), ("North", day, "A1", 2.0), ("South", day, "A1", 16.0));

        var lines = await Lines(Report([Group(0, "Region"), Group(1, "Account")],
            Total("PerRegion", AggregateFunction.Sum, 2, 1), Total("PerAccount", AggregateFunction.Sum, 2, 2),
            Total("Accounts", AggregateFunction.Count, 2, 1), Total("Mean", AggregateFunction.Average, 2, 1)), data);

        // North A1 (1, 2), North A2 (4), South with no account (32), South A1 (8, 16).
        Assert.That(lines, Is.EqualTo(new[]
        {
            "1.00 1.00 1 1.00",
            "1.00 1.00 1 1.00",
            "5.00 4.00 2 2.50",
            "32.00 32.00 1 32.00",
            "40.00 8.00 2 20.00",
            "40.00 8.00 2 20.00",
        }));
    }

    [Test]
    public async Task AnAccountInANewMonthIsANewInstance()
    {
        var data = Data(("North", new DateTime(2024, 2, 3), "A1", 4.0), ("North", new DateTime(2024, 1, 5), "A1", 1.0),
            ("North", new DateTime(2024, 2, 5), "A2", 16.0), ("North", new DateTime(2024, 1, 20), "A1", 2.0),
            ("North", new DateTime(2024, 2, 4), "A1", 8.0));

        var lines = await Lines(Report([Group(0, "Region"), Group(1, "Posted", GroupCondition.Monthly), Group(2, "Account")],
            Total("PerRegion", AggregateFunction.Sum, 3, 1)), data);

        // January's A1 (1, 2), February's A1 (4, 8), February's A2 (16).
        Assert.That(lines, Is.EqualTo(new[] { "1.00", "1.00", "5.00", "5.00", "21.00" }));
    }
}
