using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A formula's summary over a group, Sum({T.Amount}, {T.Account}), is the total of the group
/// the record it is evaluated on belongs to, as Crystal's is. It summed every record in the
/// report.
/// </summary>
[TestFixture]
public class GroupSummaryFormulaTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static FieldObject Show(string formula, int column) => new()
    {
        Name = "Show" + formula + column, FieldName = "@" + formula,
        Bounds = new(column * 1440, 0, 1440, 240), Format = new ObjectFormat { FormatString = "0.00" },
    };

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Group Summaries",
        Fields =
        [
            new DatabaseField { Name = "T.Region", TableName = "T", ColumnName = "Region", DataType = "String" },
            new DatabaseField { Name = "T.Account", TableName = "T", ColumnName = "Account", DataType = "String" },
            new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
            new FormulaField { Name = "AccountTotal", FormulaText = "Sum ({T.Amount}, {T.Account})", ResultType = "Number" },
            new FormulaField { Name = "RegionTotal", FormulaText = "Sum ({T.Amount}, {T.Region}) + 0", ResultType = "Number" },
            new FormulaField { Name = "RegionLargest", FormulaText = "Maximum ({T.Amount}, {T.Region})", ResultType = "Number" },
        ],
        Groups =
        [
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T", Condition = GroupCondition.EachValue },
            new GroupDefinition { Level = 1, FieldName = "Account", TableName = "T", Condition = GroupCondition.EachValue },
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 300,
                Objects = [Show("AccountTotal", 0), Show("RegionTotal", 1), Show("RegionLargest", 2)] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 300,
                Objects = [Show("AccountTotal", 3), Show("RegionTotal", 4)] },
        ]
    };

    [Test]
    public async Task EachRecordShowsTheTotalOfItsOwnGroup()
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Account", typeof(string));
        data.Columns.Add("Amount", typeof(double));
        // Out of order, so the groups' sort is what brings each account's records together.
        foreach (var (region, account, amount) in new[]
                 {
                     ("South", "A1", 8.0), ("North", "A1", 1.0), ("North", "A2", 4.0),
                     ("South", "A3", 32.0), ("North", "A1", 2.0), ("South", "A1", 16.0),
                 })
            data.Rows.Add(region, account, amount);

        string dir = Path.Combine(Path.GetTempPath(), "grpsum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        List<string> lines;
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            lines = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => t.Text.Length > 0 && t.Text != "Group Summaries")
                .GroupBy(t => t.Y).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(t => t.X).Select(t => t.Text))).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        // North: A1 (1, 2), A2 (4); South: A1 (8, 16), A3 (32). A region's footer is evaluated
        // on its last record, so its account total is the last account's.
        Assert.That(lines, Is.EqualTo(new[]
        {
            "3.00 7.00 4.00",
            "3.00 7.00 4.00",
            "4.00 7.00 4.00",
            "4.00 7.00",
            "24.00 56.00 32.00",
            "24.00 56.00 32.00",
            "32.00 56.00 32.00",
            "32.00 56.00",
        }));
    }
}
