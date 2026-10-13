using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The report's own record sorts, read from its tag-41 records (#31). Each expectation is
/// what the Crystal runtime's sort list holds for the same file
/// (tools/Majorsilence.Crystal.ReferenceRenderer --links); BACKLOG has the corpus counts.
/// </summary>
[TestFixture]
public class SortFieldTests
{
    private static ReportDefinition Parse(string name)
    {
        string path = Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!;
    }

    private static string[] Sorts(ReportDefinition r) =>
        r.SortFields.Select(s => $"{s.TableName}.{s.FieldName} {s.Direction}").ToArray();

    [Test]
    public void ThreeRecordSorts_InTheOrderTheyApply()
    {
        Assert.That(Sorts(Parse("benbrahim777__Country-Region-Sort.rpt")), Is.EqualTo(new[]
        {
            "Customer.Country Ascending",
            "Customer.Region Ascending",
            "Customer.Customer Name Ascending"
        }));
    }

    [Test]
    public void OneRecordSort()
    {
        Assert.That(Sorts(Parse("benbrahim777__InventoryStatus.rpt")), Is.EqualTo(new[] { "Product.Product Name Ascending" }));
    }

    [Test]
    public void AGroupsOwnSort_IsNotARecordSort()
    {
        var report = Parse("benbrahim777__SalesByCustomer-Grouped.rpt");
        Assert.That(report.Groups, Is.Not.Empty);
        Assert.That(report.SortFields, Is.Empty);
    }

    [Test]
    public void ATopNSummarySort_IsNotARecordSort()
    {
        // Top5USA sorts its groups by "Sum of Orders.Order Amount", top N; a tag-41 record of kind 2.
        Assert.That(Parse("benbrahim777__Top5USA.rpt").SortFields, Is.Empty);
    }

    [Test]
    public void AReportWithNoSort_HasNone()
    {
        Assert.That(Parse("benbrahim777__CustomerList.rpt").SortFields, Is.Empty);
    }

    [Test]
    public void TheConverterWritesTheSorts()
    {
        string rdl = new Majorsilence.Crystal.Converter.RdlConverter().Convert(Parse("benbrahim777__Country-Region-Sort.rpt"));
        int country = rdl.IndexOf("<SortExpression>=Fields!Country.Value</SortExpression>", StringComparison.Ordinal);
        int region = rdl.IndexOf("<SortExpression>=Fields!Region.Value</SortExpression>", StringComparison.Ordinal);
        int name = rdl.IndexOf("<SortExpression>=Fields!Customer_Name.Value</SortExpression>", StringComparison.Ordinal);
        Assert.That(country, Is.GreaterThan(0));
        Assert.That(region, Is.GreaterThan(country));
        Assert.That(name, Is.GreaterThan(region));
    }
}
