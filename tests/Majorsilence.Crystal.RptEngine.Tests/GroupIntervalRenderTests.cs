using System.Data;
using System.Globalization;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A group on a date breaks on its interval ("for each month") and prints the interval's
/// start as its name, as Crystal's own exports of one report grouped each way do: a week
/// from its Sunday, two weeks in periods that 2023-12-24 starts, half months on the 1st and
/// the 16th, months, quarters and half years as their first month, and so on.
/// </summary>
[TestFixture]
public class GroupIntervalRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static readonly DateTime[] Dates =
    [
        new(2023, 12, 31, 8, 0, 0),
        new(2024, 1, 3, 9, 12, 0),
        new(2024, 1, 5, 14, 3, 0),
        new(2024, 1, 8, 8, 20, 0),
        new(2024, 1, 20, 19, 44, 0),
        new(2024, 2, 14, 10, 10, 0),
        new(2024, 4, 21, 20, 10, 0),
        new(2024, 7, 2, 11, 0, 0),
    ];

    private static ReportDefinition Report(GroupCondition condition) => new()
    {
        ReportTitle = "Intervals",
        Fields =
        [
            new DatabaseField { Name = "T.Created", TableName = "T", ColumnName = "Created", DataType = "DateTime" },
            new DatabaseField { Name = "T.Code", TableName = "T", ColumnName = "Code", DataType = "String" },
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Created", TableName = "T", Condition = condition }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 300, Objects =
                [new FieldObject { FieldName = "Group #1 Name", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [new FieldObject { FieldName = "Code", Bounds = new(3000, 0, 1440, 240) }] },
        ]
    };

    // The group names as printed, top to bottom: the text of every line that holds no code.
    private static async Task<List<string>> GroupNames(GroupCondition condition)
    {
        var data = new DataTable();
        data.Columns.Add("Created", typeof(DateTime));
        data.Columns.Add("Code", typeof(string));
        // Out of order, so the groups' own sort is what puts them in time order.
        foreach (var d in Dates.Reverse())
            data.Rows.Add(d, "C" + d.ToString("MMdd", CultureInfo.InvariantCulture));

        string dir = Path.Combine(Path.GetTempPath(), "groupinterval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(condition), new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => !string.IsNullOrEmpty(t.Text) && !t.Text.StartsWith('C'))
                .OrderBy(t => t.Y).Select(t => t.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Day(int y, int m, int d) => new DateTime(y, m, d).ToString("d", CultureInfo.CurrentCulture);

    [Test]
    public async Task Monthly_OneGroupPerMonth_NamedYearAndMonth()
    {
        Assert.That(await GroupNames(GroupCondition.Monthly),
            Is.EqualTo(new[] { "2023-12", "2024-01", "2024-02", "2024-04", "2024-07" }));
    }

    [Test]
    public async Task Quarterly_And_Semiannually_AreNamedByTheirFirstMonth()
    {
        Assert.That(await GroupNames(GroupCondition.Quarterly),
            Is.EqualTo(new[] { "2023-10", "2024-01", "2024-04", "2024-07" }));
        Assert.That(await GroupNames(GroupCondition.Semiannually),
            Is.EqualTo(new[] { "2023-07", "2024-01", "2024-07" }));
    }

    [Test]
    public async Task Annually_IsNamedByTheYear()
    {
        Assert.That(await GroupNames(GroupCondition.Annually), Is.EqualTo(new[] { "2023", "2024" }));
    }

    [Test]
    public async Task Weekly_StartsOnSunday()
    {
        // 2023-12-31 is a Sunday; the 3rd and 5th share its week, the 8th starts the next
        // (the 7th being its Sunday).
        Assert.That(await GroupNames(GroupCondition.Weekly), Is.EqualTo(new[]
        {
            Day(2023, 12, 31), Day(2024, 1, 7), Day(2024, 1, 14), Day(2024, 2, 11),
            Day(2024, 4, 21), Day(2024, 6, 30),
        }));
    }

    [Test]
    public async Task Biweekly_PeriodsStartWhereCrystalsDo()
    {
        // Crystal's two-week periods start on 2023-12-24 and 2024-01-07, and every fourteen
        // days from there: the 20th of January is still in the period of the 7th.
        Assert.That(await GroupNames(GroupCondition.Biweekly), Is.EqualTo(new[]
        {
            Day(2023, 12, 24), Day(2024, 1, 7), Day(2024, 2, 4), Day(2024, 4, 14), Day(2024, 6, 23),
        }));
    }

    [Test]
    public async Task Semimonthly_BreaksOnTheFirstAndTheSixteenth()
    {
        Assert.That(await GroupNames(GroupCondition.Semimonthly), Is.EqualTo(new[]
        {
            Day(2023, 12, 16), Day(2024, 1, 1), Day(2024, 1, 16), Day(2024, 2, 1),
            Day(2024, 4, 16), Day(2024, 7, 1),
        }));
    }

    [Test]
    public async Task ByHour_PrintsTheDateAndTheHour()
    {
        var names = await GroupNames(GroupCondition.ByHour);
        Assert.That(names[1], Is.EqualTo(Day(2024, 1, 3) + " 9:00AM"));
        Assert.That(names[2], Is.EqualTo(Day(2024, 1, 5) + " 2:00PM"));
    }

    [Test]
    public async Task ByAmPm_PrintsMidnightOrNoon()
    {
        var names = await GroupNames(GroupCondition.ByAmPm);
        Assert.That(names[1], Is.EqualTo(Day(2024, 1, 3) + " 12:00AM"));
        Assert.That(names[2], Is.EqualTo(Day(2024, 1, 5) + " 12:00PM"));
    }
}
