using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A details field set to Suppress If Duplicated prints only on a record whose value differs
/// from the record printed before it, in the order the records print.
/// </summary>
[TestFixture]
public class SuppressIfDuplicatedRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Listing(bool grouped = false) => new()
    {
        Fields =
        [
            new DatabaseField { Name = "Lines.Customer", ColumnName = "Customer", TableName = "Lines", DataType = "String" },
            new DatabaseField { Name = "Lines.Amount", ColumnName = "Amount", TableName = "Lines", DataType = "Float64" },
            .. grouped
                ? new[] { new DatabaseField { Name = "Lines.Region", ColumnName = "Region", TableName = "Lines", DataType = "String" } }
                : [],
        ],
        Groups = grouped ? [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "Lines" }] : [],
        SortFields = [new SortField { FieldName = "Amount", TableName = "Lines" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects =
            [
                new FieldObject { Name = "Customer1", FieldName = "Customer", Bounds = new(0, 0, 2880, 240),
                    Format = new ObjectFormat { SuppressIfDuplicated = true } },
                new FieldObject { Name = "Amount1", FieldName = "Amount", Bounds = new(4320, 0, 1440, 240) },
            ] },
        ]
    };

    // Out of the printed order, so the record read before is not the record printed before.
    private static DataTable Lines()
    {
        var dt = new DataTable();
        dt.Columns.Add("Customer", typeof(string));
        dt.Columns.Add("Amount", typeof(double));
        dt.Rows.Add("ZZ-B", 3.0);
        dt.Rows.Add("ZZ-A", 1.0);
        dt.Rows.Add("ZZ-A", 2.0);
        dt.Rows.Add("ZZ-B", 4.0);
        return dt;
    }

    [Test]
    public async Task ARepeatedValue_PrintsOnceForEachRun()
    {
        string dir = Path.Combine(Path.GetTempPath(), "suppressdup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(Listing(), new RuntimeOverrides { Data = Lines() }, dir, []);
            using var pages = await engineReport.BuildPages();
            var names = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Select(t => t.Text).Where(t => t.StartsWith("ZZ-", StringComparison.Ordinal)).ToList();

            Assert.That(names, Is.EqualTo(new[] { "ZZ-A", "ZZ-B" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The first record of a group prints its value though the last record of the group
    // before has the same one: Crystal starts the comparison again with each group.
    [Test]
    public async Task ARepeatedValue_PrintsAgainAtTheStartOfEachGroup()
    {
        var report = Listing(grouped: true);
        var dt = new DataTable();
        dt.Columns.Add("Customer", typeof(string));
        dt.Columns.Add("Amount", typeof(double));
        dt.Columns.Add("Region", typeof(string));
        dt.Rows.Add("ZZ-A", 1.0, "North");
        dt.Rows.Add("ZZ-A", 2.0, "North");
        dt.Rows.Add("ZZ-A", 3.0, "South");
        dt.Rows.Add("ZZ-B", 4.0, "South");

        string dir = Path.Combine(Path.GetTempPath(), "suppressdup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { Data = dt }, dir, []);
            using var pages = await engineReport.BuildPages();
            var names = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Select(t => t.Text).Where(t => t.StartsWith("ZZ-", StringComparison.Ordinal)).ToList();

            Assert.That(names, Is.EqualTo(new[] { "ZZ-A", "ZZ-A", "ZZ-B" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
