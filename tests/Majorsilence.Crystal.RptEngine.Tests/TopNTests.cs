using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A group's top or bottom N (the Top5USA family): only the N instances ranked first by the
/// summary print, in rank order, while a summary over the whole dataset still counts the rest,
/// as Crystal's grand total does.
/// </summary>
[TestFixture]
public class TopNTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Parse(string name)
    {
        string path = Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        return RptParser.Parse(path).Report!;
    }

    // Seven USA customers whose order totals are 700, 600, ..., 100, and one in Canada that
    // the report's record selection leaves out.
    private static Dictionary<string, DataTable> Tables()
    {
        var customer = new DataTable();
        customer.Columns.Add("Customer ID", typeof(int));
        customer.Columns.Add("Customer Name", typeof(string));
        customer.Columns.Add("Country", typeof(string));
        customer.Columns.Add("Region", typeof(string));
        var orders = new DataTable();
        orders.Columns.Add("Order ID", typeof(int));
        orders.Columns.Add("Customer ID", typeof(int));
        orders.Columns.Add("Order Amount", typeof(double));
        string[] names = ["ZZ-G", "ZZ-A", "ZZ-E", "ZZ-C", "ZZ-F", "ZZ-B", "ZZ-D"];
        int[] totals = [100, 700, 300, 500, 200, 600, 400];
        for (int i = 0; i < names.Length; i++)
        {
            customer.Rows.Add(i + 1, names[i], "USA", "CA");
            // Two orders each, so the rank is by the group's sum and not by any one row.
            orders.Rows.Add(100 + i * 2, i + 1, totals[i] * 0.25);
            orders.Rows.Add(101 + i * 2, i + 1, totals[i] * 0.75);
        }
        customer.Rows.Add(99, "ZZ-CANADA", "Canada", "BC");
        orders.Rows.Add(999, 99, 5000.0);
        return new Dictionary<string, DataTable> { ["Customer"] = customer, ["Orders"] = orders };
    }

    private static async Task<List<string>> Printed(ReportDefinition report)
    {
        string dir = Path.Combine(Path.GetTempPath(), "topn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { TableData = Tables() }, dir, []);
            using var pages = await engineReport.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static List<string> Customers(List<string> printed) =>
        printed.Where(t => t.StartsWith("ZZ-", StringComparison.Ordinal)).Distinct().ToList();

    [Test]
    public void TheTopNIsReadFromTheFile()
    {
        var group = Parse("benbrahim777__Top5USA.rpt").Groups.Single();
        Assert.That(group.TopN, Is.Not.Null);
        Assert.That((group.TopN!.Count, group.TopN.Bottom, group.TopN.Function, group.TopN.TableName, group.TopN.FieldName, group.TopN.DiscardOthers),
            Is.EqualTo((5, false, Model.Fields.AggregateFunction.Sum, "Orders", "Order Amount", true)));
        Assert.That(Parse("benbrahim777__Bottom5USA.rpt").Groups.Single().TopN!.Bottom, Is.True);
        Assert.That(Parse("benbrahim777__Top25USA.rpt").Groups.Single().TopN!.Count, Is.EqualTo(25));
        Assert.That(Parse("benbrahim777__CustomerOrders-ByCountry.rpt").Groups.All(g => g.TopN is null), Is.True);
    }

    [Test]
    public async Task Top5_PrintsTheFiveLargest_LargestFirst()
    {
        var printed = await Printed(Parse("benbrahim777__Top5USA.rpt"));

        Assert.That(Customers(printed), Is.EqualTo(new[] { "ZZ-A", "ZZ-B", "ZZ-C", "ZZ-D", "ZZ-E" }));
        // Each group's share is of every USA order, the two left out included: ZZ-A's 700 of 2,800.
        Assert.That(printed, Has.Member("25.00%"), string.Join(" | ", printed));
    }

    [Test]
    public async Task Bottom5_PrintsTheFiveSmallest_SmallestFirst()
    {
        var printed = await Printed(Parse("benbrahim777__Bottom5USA.rpt"));

        Assert.That(Customers(printed), Is.EqualTo(new[] { "ZZ-G", "ZZ-F", "ZZ-E", "ZZ-D", "ZZ-C" }));
    }

    [Test]
    public void WithoutARankColumn_EveryGroupPasses()
    {
        // A renderer that adds no rank: the filter lets an empty rank through.
        string rdl = new Majorsilence.Crystal.Converter.RdlConverter().Convert(Parse("benbrahim777__Top5USA.rpt"));
        Assert.That(rdl, Does.Contain("<FilterExpression>=IIf(IsNothing(Fields!CrystalTopNRank0.Value), 0, Fields!CrystalTopNRank0.Value)</FilterExpression>"));
        Assert.That(rdl, Does.Contain("<FilterValue>=5</FilterValue>"));
        Assert.That(rdl, Does.Contain("<SortExpression>=Fields!CrystalTopNRank0.Value</SortExpression>"));
    }
}
