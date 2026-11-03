using System.Data;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A group in original order prints its records as they come, starting an instance wherever
/// its value or a group's above it changes, so one value can head several instances; the
/// groups around it still sort.
/// </summary>
[TestFixture]
public class OriginalOrderGroupTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report(params GroupDefinition[] groups) => new()
    {
        ReportTitle = "Runs",
        Fields =
        [
            new DatabaseField { Name = "T.Region", TableName = "T", ColumnName = "Region", DataType = "String" },
            new DatabaseField { Name = "T.Dept", TableName = "T", ColumnName = "Dept", DataType = "String" },
            new DatabaseField { Name = "T.Code", TableName = "T", ColumnName = "Code", DataType = "String" },
        ],
        Groups = [.. groups],
        Sections =
        [
            .. groups.Select(g => new Section
            {
                Type = SectionType.GroupHeader, GroupLevel = g.Level, HeightTwips = 300, Objects =
                    [new FieldObject { FieldName = $"Group #{g.Level + 1} Name", Bounds = new(g.Level * 1440, 0, 1440, 240) }]
            }),
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [new FieldObject { FieldName = "Code", Bounds = new(3000, 0, 1440, 240) }] },
        ]
    };

    private static DataTable Data(params (string Region, string Dept, string Code)[] rows)
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Dept", typeof(string));
        data.Columns.Add("Code", typeof(string));
        foreach (var (region, dept, code) in rows)
            data.Rows.Add(region, dept, code);
        return data;
    }

    // Every line printed, top to bottom.
    private static async Task<List<string>> Printed(ReportDefinition definition, DataTable data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "originalorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(definition, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Where(t => !string.IsNullOrEmpty(t.Text) && t.Text != "Runs")
                .OrderBy(t => t.Y).Select(t => t.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OriginalOrder_PrintsEachRunAsItComes()
    {
        var report = Report(new GroupDefinition { Level = 0, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder });
        var data = Data(("", "D2", "c1"), ("", "D2", "c2"), ("", "D1", "c3"), ("", "D3", "c4"), ("", "D1", "c5"));

        Assert.That(await Printed(report, data),
            Is.EqualTo(new[] { "D2", "c1", "c2", "D1", "c3", "D3", "c4", "D1", "c5" }));
    }

    [Test]
    public async Task Ascending_StillGathersEachValue()
    {
        var report = Report(new GroupDefinition { Level = 0, FieldName = "Dept", TableName = "T" });
        var data = Data(("", "D2", "c1"), ("", "D2", "c2"), ("", "D1", "c3"), ("", "D3", "c4"), ("", "D1", "c5"));

        Assert.That(await Printed(report, data),
            Is.EqualTo(new[] { "D1", "c3", "c5", "D2", "c1", "c2", "D3", "c4" }));
    }

    [Test]
    public async Task UnderASortedGroup_RunsAreTakenInTheSortedOrder()
    {
        // Sorted by region, the north's records come together and its two D1 records meet; the
        // south's D1 is another instance, under its own region.
        var report = Report(
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" },
            new GroupDefinition { Level = 1, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder });
        var data = Data(("N", "D2", "c1"), ("S", "D1", "c2"), ("N", "D1", "c3"), ("S", "D3", "c4"), ("N", "D1", "c5"));

        Assert.That(await Printed(report, data),
            Is.EqualTo(new[] { "N", "D2", "c1", "D1", "c3", "c5", "S", "D1", "c2", "D3", "c4" }));
    }

    [Test]
    public async Task InsideAGroupInOriginalOrder_NothingSorts()
    {
        // The group inside and the record sort field come after the original order in
        // Crystal's list of sort keys, so they decide nothing: the D1 records under N are
        // two instances, and the codes print as they came.
        var report = Report(
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder },
            new GroupDefinition { Level = 1, FieldName = "Dept", TableName = "T" });
        report.SortFields.Add(new SortField { FieldName = "Code", TableName = "T", Direction = SortDirection.Descending });
        var data = Data(("N", "D1", "c1"), ("N", "D2", "c2"), ("N", "D1", "c3"), ("S", "D1", "c4"), ("S", "D1", "c5"));

        Assert.That(await Printed(report, data),
            Is.EqualTo(new[] { "N", "D1", "c1", "D2", "c2", "D1", "c3", "S", "D1", "c4", "c5" }));
    }

    [Test]
    public void TheGroupBreaksAndSortsOnItsRun_BeforeItsValue()
    {
        // Without the run column the run is empty everywhere and the value decides, as before.
        string rdl = new RdlConverter().Convert(Report(
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" },
            new GroupDefinition { Level = 1, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder }));

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        List<string> Keys(string group, string element) => doc.Descendants(ns + "TableGroup")
            .Single(g => (string?)g.Element(ns + "Grouping")?.Attribute("Name") == group)
            .Descendants(ns + element).Select(e => e.Value).ToList();

        Assert.That(Keys("Group2", "GroupExpression"), Is.EqualTo(new[] { "=Fields!CrystalGroupRun1.Value", "=Fields!Dept.Value" }));
        Assert.That(Keys("Group2", "SortExpression"), Is.EqualTo(new[] { "=Fields!CrystalGroupRun1.Value", "=Fields!Dept.Value" }));
        Assert.That(Keys("Group1", "GroupExpression"), Is.EqualTo(new[] { "=Fields!Region.Value" }));
        Assert.That(doc.Descendants(ns + "Field").Select(f => (string?)f.Attribute("Name")), Has.Member("CrystalGroupRun1"));
    }

    [Test]
    public void OnTwoTables_TheGroupKeepsBreakingOnItsValue()
    {
        // Records from two tables come in the order Crystal joins them, which the data given
        // the renderer need not have.
        var report = Report(new GroupDefinition { Level = 0, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder });
        report.DataSources.Add(new Model.DataSource { Name = "Db", Tables = [new TableDefinition { Name = "T", Alias = "T" }, new TableDefinition { Name = "U", Alias = "U" }] });

        string rdl = new RdlConverter().Convert(report);
        Assert.That(rdl, Does.Not.Contain("CrystalGroupRun"));
        var data = Data(("", "D2", "c1"), ("", "D1", "c2"));
        Assert.That(TableJoiner.AddGroupRuns(report, data), Is.SameAs(data));
    }

    [Test]
    public void AddGroupRuns_NumbersRunsInCrystalsOrder_AndLeavesTheCallersTable()
    {
        var report = Report(
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" },
            new GroupDefinition { Level = 1, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder });
        var data = Data(("N", "D2", "c1"), ("S", "D1", "c2"), ("N", "D1", "c3"), ("S", "D3", "c4"), ("N", "D1", "c5"));

        var runs = TableJoiner.AddGroupRuns(report, data);

        Assert.That(data.Columns.Contains(RdlConverter.GroupRunField(1)), Is.False);
        Assert.That(runs.Rows.Cast<DataRow>().Select(r => (int)r[RdlConverter.GroupRunField(1)]),
            Is.EqualTo(new[] { 1, 3, 2, 4, 2 }));
    }

    [Test]
    public void AddGroupRuns_WithAGroupOnAFormula_AddsNoRun()
    {
        // The order a formula group sorts in cannot be read from the data; the group in
        // original order then breaks on its value, as it would with no run.
        var report = Report(
            new GroupDefinition { Level = 0, FieldName = "@Zone" },
            new GroupDefinition { Level = 1, FieldName = "Dept", TableName = "T", SortOrder = GroupSortOrder.OriginalOrder });
        var data = Data(("N", "D2", "c1"), ("S", "D1", "c2"));

        Assert.That(TableJoiner.AddGroupRuns(report, data), Is.SameAs(data));
    }
}
