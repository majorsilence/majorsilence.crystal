using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A report's own record sorts, read from the file (#31), order the rows it prints whatever
/// order the pushed data arrives in.
/// </summary>
[TestFixture]
public class SortOrderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition CountryRegionSort()
    {
        string path = Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Country-Region-Sort.rpt", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        return RptParser.Parse(path).Report!;
    }

    // Deliberately out of order on every sort key.
    private static DataTable Unsorted()
    {
        var dt = new DataTable();
        dt.Columns.Add("Customer ID", typeof(int));
        dt.Columns.Add("Customer Name", typeof(string));
        dt.Columns.Add("Region", typeof(string));
        dt.Columns.Add("Country", typeof(string));
        dt.Rows.Add(1, "ZZ-USA-OHIO-B", "Ohio", "USA");
        dt.Rows.Add(2, "ZZ-CANADA-BC", "BC", "Canada");
        dt.Rows.Add(3, "ZZ-USA-IOWA", "Iowa", "USA");
        dt.Rows.Add(4, "ZZ-USA-OHIO-A", "Ohio", "USA");
        dt.Rows.Add(5, "ZZ-ARGENTINA", "Chaco", "Argentina");
        return dt;
    }

    private static async Task<List<string>> PrintedNames(ReportDefinition report)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sortorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { Data = Unsorted() }, dir, []);
            using var pages = await engineReport.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Select(t => t.Text).Where(t => t.StartsWith("ZZ-", StringComparison.Ordinal)).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RowsPrintInTheReportsOwnSortOrder()
    {
        var names = await PrintedNames(CountryRegionSort());

        // Country, then Region, then Customer Name, all ascending.
        Assert.That(names, Is.EqualTo(new[] { "ZZ-ARGENTINA", "ZZ-CANADA-BC", "ZZ-USA-IOWA", "ZZ-USA-OHIO-A", "ZZ-USA-OHIO-B" }));
    }

    [Test]
    public async Task ADescendingSort_PrintsInReverse()
    {
        var report = CountryRegionSort();
        report.SortFields[0].Direction = SortDirection.Descending;

        var names = await PrintedNames(report);

        Assert.That(names, Is.EqualTo(new[] { "ZZ-USA-IOWA", "ZZ-USA-OHIO-A", "ZZ-USA-OHIO-B", "ZZ-CANADA-BC", "ZZ-ARGENTINA" }));
    }
}
