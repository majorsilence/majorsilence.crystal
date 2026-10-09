using System.Data;
using System.Text;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.RptEngine;
using Majorsilence.Crystal.Runtime;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A multi-table report rendered from one table per Crystal table, joined here with the
/// links the file records (roadmap 3.2, #36): the join itself on small hand-made tables,
/// and a real two-table report rendering the same from per-table data as from the table a
/// caller joined already.
/// </summary>
[TestFixture]
public class TableDataTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static string CorpusPath(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static DataTable Table(string[] columns, params object[][] rows)
    {
        var dt = new DataTable();
        foreach (var c in columns) dt.Columns.Add(c, typeof(object));
        foreach (var r in rows) dt.Rows.Add(r);
        return dt;
    }

    private static ReportDefinition TwoTables(TableJoinType join, params TableLink[] extraLinks)
    {
        var report = new ReportDefinition
        {
            DataSources =
            {
                new DataSource
                {
                    Name = "DataSource1",
                    Tables =
                    {
                        new TableDefinition { Name = "Customer", Alias = "Customer" },
                        new TableDefinition { Name = "Orders", Alias = "Orders" }
                    }
                }
            },
            TableLinks =
            {
                new TableLink { SourceTable = "Orders", SourceColumn = "Customer ID", TargetTable = "Customer", TargetColumn = "Customer ID", JoinType = join }
            },
            Fields =
            {
                new DatabaseField { Name = "Customer Name", ColumnName = "Customer Name", TableName = "Customer" },
                new DatabaseField { Name = "Order Amount", ColumnName = "Order Amount", TableName = "Orders" }
            }
        };
        report.TableLinks.AddRange(extraLinks);
        return report;
    }

    private static Dictionary<string, DataTable> CustomersAndOrders() => new()
    {
        ["Customer"] = Table(["Customer ID", "Customer Name"], [1, "Alpha"], [2, "Beta"], [3, "Gamma"]),
        ["Orders"] = Table(["Order ID", "Customer ID", "Order Amount"], [10, 1, 100m], [11, 1, 150m], [12, 2, 200m], [13, 9, 999m])
    };

    private static string[] Rows(DataTable t, params string[] columns) =>
        t.Rows.Cast<DataRow>().Select(r => string.Join("|", columns.Select(c => r[c] is DBNull ? "" : r[c].ToString()))).ToArray();

    [Test]
    public void InnerJoin_KeepsTheMatchedRowsOnly_UnderQualifiedNames()
    {
        var warnings = new List<string>();
        var joined = TableJoiner.Join(TwoTables(TableJoinType.Inner), CustomersAndOrders(), warnings);

        Assert.That(warnings, Is.Empty);
        Assert.That(Rows(joined, "Customer.Customer Name", "Orders.Order Amount"),
            Is.EquivalentTo(new[] { "Alpha|100", "Alpha|150", "Beta|200" }));
        Assert.That(joined.Columns.Contains("Order Amount"), Is.True, "a column only one table has is also reachable bare");
        Assert.That(joined.Columns.Contains("Customer ID"), Is.False, "a column both tables have is not");
    }

    [Test]
    public void LeftOuterJoin_KeepsEverySourceRow()
    {
        // The link runs from Orders to Customer, so every order stays, matched or not.
        var joined = TableJoiner.Join(TwoTables(TableJoinType.LeftOuter), CustomersAndOrders(), []);

        Assert.That(Rows(joined, "Orders.Order ID", "Customer.Customer Name"),
            Is.EquivalentTo(new[] { "10|Alpha", "11|Alpha", "12|Beta", "13|" }));
    }

    [Test]
    public void RightOuterJoin_KeepsEveryTargetRow()
    {
        var joined = TableJoiner.Join(TwoTables(TableJoinType.RightOuter), CustomersAndOrders(), []);

        Assert.That(Rows(joined, "Customer.Customer Name", "Orders.Order ID"),
            Is.EquivalentTo(new[] { "Alpha|10", "Alpha|11", "Beta|12", "Gamma|" }));
    }

    [Test]
    public void SecondLinkBetweenJoinedTables_FiltersTheRows()
    {
        var report = TwoTables(TableJoinType.Inner,
            new TableLink { SourceTable = "Orders", SourceColumn = "Region", TargetTable = "Customer", TargetColumn = "Region", JoinType = TableJoinType.Inner });
        var tables = new Dictionary<string, DataTable>
        {
            ["Customer"] = Table(["Customer ID", "Customer Name", "Region"], [1, "Alpha", "East"]),
            ["Orders"] = Table(["Order ID", "Customer ID", "Order Amount", "Region"], [10, 1, 100m, "East"], [11, 1, 150m, "West"])
        };

        var joined = TableJoiner.Join(report, tables, []);

        Assert.That(Rows(joined, "Orders.Order ID"), Is.EqualTo(new[] { "10" }));
    }

    [Test]
    public void UnlinkedTable_IsCrossJoined_AndAMissingTableIsReported()
    {
        var report = TwoTables(TableJoinType.Inner);
        report.DataSources[0].Tables.Add(new TableDefinition { Name = "Company", Alias = "Company" });
        report.DataSources[0].Tables.Add(new TableDefinition { Name = "Unpushed", Alias = "Unpushed" });
        var tables = CustomersAndOrders();
        tables["Company"] = Table(["Company Name"], ["ACME"]);
        tables["Extra"] = Table(["x"], [1]);

        var warnings = new List<string>();
        var joined = TableJoiner.Join(report, tables, warnings);

        Assert.That(joined.Rows.Count, Is.EqualTo(3));
        Assert.That(joined.Rows.Cast<DataRow>().All(r => (string)r["Company.Company Name"] == "ACME"), Is.True);
        Assert.That(warnings, Is.EquivalentTo(new[]
        {
            "TableData: the report has no table named 'Extra'; it is not used.",
            "TableData: no data was pushed for table 'Unpushed'; it renders empty."
        }));
    }

    [Test]
    public void KeysMatchAcrossNumericWidthsAndStringCase()
    {
        var tables = new Dictionary<string, DataTable>
        {
            ["Customer"] = Table(["Customer ID", "Customer Name"], [1L, "Alpha"], ["abc", "Beta"]),
            ["Orders"] = Table(["Order ID", "Customer ID", "Order Amount"], [10, 1, 100m], [11, "ABC", 150m])
        };

        var joined = TableJoiner.Join(TwoTables(TableJoinType.Inner), tables, []);

        Assert.That(Rows(joined, "Orders.Order ID", "Customer.Customer Name"), Is.EquivalentTo(new[] { "10|Alpha", "11|Beta" }));
    }

    [Test]
    public void QualifyColumns_AddsTheQualifiedNamesToAPreJoinedTable_WithoutTouchingIt()
    {
        var report = TwoTables(TableJoinType.Inner);
        var flat = Table(["Customer Name", "Order Amount"], ["Alpha", 100m]);

        var qualified = TableJoiner.QualifyColumns(report, flat);

        Assert.That(qualified.Columns.Cast<DataColumn>().Select(c => c.ColumnName),
            Is.EqualTo(new[] { "Customer Name", "Order Amount", "Customer.Customer Name", "Orders.Order Amount" }));
        Assert.That(qualified.Rows[0]["Orders.Order Amount"], Is.EqualTo(100m));
        Assert.That(flat.Columns.Count, Is.EqualTo(2), "the caller's table is left as it was");
        Assert.That(TableJoiner.QualifyColumns(report, qualified), Is.SameAs(qualified), "nothing to add, nothing copied");
    }

    private static readonly string[] CustomerNames = ["ZZZ-ALPHA-ZZZ", "ZZZ-BETA-ZZZ"];

    private static Dictionary<string, DataTable> BeforeTvTables()
    {
        var customer = new DataTable();
        customer.Columns.Add("Customer ID", typeof(int));
        customer.Columns.Add("Customer Name", typeof(string));
        customer.Rows.Add(1, CustomerNames[0]);
        customer.Rows.Add(2, CustomerNames[1]);
        customer.Rows.Add(3, "ZZZ-NO-ORDERS-ZZZ");

        var orders = new DataTable();
        orders.Columns.Add("Order ID", typeof(int));
        orders.Columns.Add("Customer ID", typeof(int));
        orders.Columns.Add("Order Amount", typeof(decimal));
        orders.Columns.Add("Order Date", typeof(DateTime));
        orders.Rows.Add(10, 1, 1234.5m, new DateTime(2024, 3, 1));
        orders.Rows.Add(11, 1, 2345.5m, new DateTime(2024, 3, 2));
        orders.Rows.Add(12, 2, 3456.5m, new DateTime(2024, 3, 3));
        return new Dictionary<string, DataTable> { ["Customer"] = customer, ["Orders"] = orders };
    }

    private static DataTable BeforeTvPreJoined()
    {
        var flat = new DataTable();
        flat.Columns.Add("Customer Name", typeof(string));
        flat.Columns.Add("Order Amount", typeof(decimal));
        flat.Columns.Add("Order Date", typeof(DateTime));
        flat.Rows.Add(CustomerNames[0], 1234.5m, new DateTime(2024, 3, 1));
        flat.Rows.Add(CustomerNames[0], 2345.5m, new DateTime(2024, 3, 2));
        flat.Rows.Add(CustomerNames[1], 3456.5m, new DateTime(2024, 3, 3));
        return flat;
    }

    // The lines of a CSV export that carry pushed data; the rest is static text and dates.
    private static string[] DataLines(byte[] csv) =>
        Encoding.UTF8.GetString(csv).Split('\n').Where(l => l.Contains("ZZZ-")).Select(l => l.TrimEnd('\r')).ToArray();

    [Test]
    public async Task TwoTableReport_RendersTheSameFromPerTableDataAsFromAPreJoinedTable()
    {
        string path = CorpusPath("benbrahim777__BeforeTV.rpt");
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");

        ExportResult perTable, preJoined;
        using (var rpt = File.OpenRead(path))
            perTable = await new ReportEngine().ExportWithWarningsAsync(rpt,
                new RuntimeOverrides { TableData = BeforeTvTables() }, ExportFormat.Csv);
        using (var rpt = File.OpenRead(path))
            preJoined = await new ReportEngine().ExportWithWarningsAsync(rpt,
                new RuntimeOverrides { Data = BeforeTvPreJoined() }, ExportFormat.Csv);

        // The report also wants a parameter neither render supplies; that warning is not the point here.
        Assert.That(DataWarnings(perTable), Is.Empty);
        Assert.That(DataWarnings(preJoined), Is.Empty);
        var fromTables = DataLines(perTable.Bytes);
        Assert.That(fromTables, Has.Length.EqualTo(3), "one line per joined order; the customer with no orders has none");
        Assert.That(fromTables.Count(l => l.Contains(CustomerNames[0])), Is.EqualTo(2));
        Assert.That(string.Join("\n", fromTables), Does.Contain("1,234.5").Or.Contain("1234.5"));
        Assert.That(fromTables, Is.EqualTo(DataLines(preJoined.Bytes)));
    }

    [Test]
    public async Task TableDataGiven_DataIsIgnoredAndSaidSo()
    {
        string path = CorpusPath("benbrahim777__BeforeTV.rpt");
        Assume.That(File.Exists(path), Is.True);

        using var rpt = File.OpenRead(path);
        var result = await new ReportEngine().ExportWithWarningsAsync(rpt,
            new RuntimeOverrides { TableData = BeforeTvTables(), Data = BeforeTvPreJoined() }, ExportFormat.Csv);

        Assert.That(DataWarnings(result), Is.EqualTo(new[] { "Data: ignored, since TableData was given." }));
    }

    // The warnings about data; the report's unsupplied parameter is reported too, and is not what these tests are about.
    private static string[] DataWarnings(ExportResult result) =>
        result.Warnings.Where(w => !w.StartsWith("Parameters:", StringComparison.Ordinal)).ToArray();

    [Test]
    public void Analysis_ListsTheLinks()
    {
        string path = CorpusPath("benbrahim777__BeforeTV.rpt");
        Assume.That(File.Exists(path), Is.True);

        using var rpt = File.OpenRead(path);
        var analysis = new ReportEngine().Analyze(rpt);

        Assert.That(analysis.TableLinks.Select(l => $"{l.SourceTable}.{l.SourceColumn} -> {l.TargetTable}.{l.TargetColumn} {l.JoinType}"),
            Is.EqualTo(new[] { "Orders.Customer ID -> Customer.Customer ID Inner" }));
        Assert.That(analysis.DataTables.Select(t => t.TableName), Is.EqualTo(new[] { "Customer", "Orders" }));
        Assert.That(analysis.DataTables[1].ColumnNames, Has.Count.EqualTo(12));
    }
}
