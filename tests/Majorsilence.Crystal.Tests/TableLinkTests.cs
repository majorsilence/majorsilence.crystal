using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The tables and links a report's QESession stream records, as the parser reads them, and
/// what the converter does with them. Every expectation here is what the Crystal runtime's
/// own object model reports for the same file (tools/Majorsilence.Crystal.ReferenceRenderer
/// --links); BACKLOG has the corpus-wide counts.
/// </summary>
[TestFixture]
public class TableLinkTests
{
    private static string Corpus(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Parse(string name)
    {
        string path = Corpus(name);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True, string.Join("; ", result.Errors));
        return result.Report!;
    }

    private static string Describe(TableLink l) =>
        $"{l.SourceTable}.{l.SourceColumn} -> {l.TargetTable}.{l.TargetColumn} {l.JoinType}";

    [Test]
    public void TwoTableReport_HasItsOneLinkAndBothTables()
    {
        var report = Parse("benbrahim777__BeforeTV.rpt");

        Assert.That(report.TableLinks.Select(Describe), Is.EqualTo(new[]
        {
            "Orders.Customer ID -> Customer.Customer ID Inner"
        }));
        Assert.That(report.TableLinks[0].Operator, Is.EqualTo(LinkOperator.Equal));

        var tables = report.DataSources.Single().Tables;
        Assert.That(tables.Select(t => (t.Name, t.Alias, t.Columns.Count)), Is.EqualTo(new[]
        {
            ("Customer", "Customer", 18),
            ("Orders", "Orders", 12)
        }));
        Assert.That(tables[1].Columns.Select(c => c.Name), Does.Contain("Order Amount"));
        Assert.That(tables[1].Columns.First(c => c.Name == "Order Date").DataType, Is.EqualTo("DateTime"));
        Assert.That(tables[1].Columns.First(c => c.Name == "Order Amount").DataType, Is.EqualTo("Currency"));
    }

    [Test]
    public void FiveTableReport_LinksComeInTheRuntimesOrder()
    {
        // The stream does not store the links in this order; the runtime lists them by id.
        var report = Parse("benbrahim777__BigCells.rpt");

        Assert.That(report.TableLinks.Select(Describe), Is.EqualTo(new[]
        {
            "Orders_Detail.Order ID -> Orders.Order ID Inner",
            "Orders_Detail.Product ID -> Product.Product ID Inner",
            "Product.Product Type ID -> Product_Type.Product Type ID Inner",
            "Orders.Customer ID -> Customer.Customer ID Inner"
        }));
        Assert.That(report.DataSources.Single().Tables.Select(t => t.Alias),
            Is.EqualTo(new[] { "Customer", "Orders", "Orders_Detail", "Product", "Product_Type" }));
        Assert.That(report.DataSources.Single().Tables.First(t => t.Alias == "Product_Type").Name,
            Is.EqualTo("Product Type"), "the alias replaces the space; the table keeps its name");
    }

    [Test]
    public void LeftOuterJoin_IsReadAsSuch()
    {
        var report = Parse("souvikduttachoudhury__CustomFunctions.rpt");

        Assert.That(report.TableLinks.Select(Describe), Is.EqualTo(new[]
        {
            "CUSTOMER.CUSTOMER_ID -> ORDERS.CUSTOMER_ID LeftOuter"
        }));
    }

    [Test]
    public void CommandTables_AreKnownByTheirAliases()
    {
        // SAP Business One templates read "Command" SQL tables, which only their aliases tell apart.
        var report = Parse("boyum__Picklist.rpt");

        Assert.That(report.DataSources.Single().Tables.Select(t => (t.Name, t.Alias)), Is.EqualTo(new[]
        {
            ("Command", "Header"),
            ("Command", "Lines")
        }));
        Assert.That(report.TableLinks.Select(Describe), Is.EqualTo(new[]
        {
            "Lines.AbsEntry -> Header.PickListNumber Inner"
        }));
    }

    [Test]
    public void SingleTableReport_HasNoLinks()
    {
        var report = Parse("benbrahim777__CustomerList.rpt");

        Assert.That(report.TableLinks, Is.Empty);
        Assert.That(report.DataSources.Single().Tables.Select(t => t.Alias), Is.EqualTo(new[] { "Customer" }));
    }

    [Test]
    public void DatabaseFields_TakeTheirTableFromTheGraph()
    {
        var report = Parse("benbrahim777__BeforeTV.rpt");

        var byColumn = report.Fields.OfType<DatabaseField>().ToDictionary(f => f.ColumnName, f => f.TableName);
        Assert.That(byColumn["Customer Name"], Is.EqualTo("Customer"));
        Assert.That(byColumn["Order Amount"], Is.EqualTo("Orders"));
        Assert.That(byColumn["Order Date"], Is.EqualTo("Orders"));
    }

    // --- The graph reader on a hand-built payload: the record layouts, without a corpus file. ---

    private static byte[] Int(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Str(string s) =>
        [.. Int(s.Length + 1), .. System.Text.Encoding.Latin1.GetBytes(s), 0];

    // A record: control byte (0xF8 with a schema), tag, schema, big-endian length, then the
    // body masked with the tag. A child record goes into its parent's body unmasked, and is
    // masked again with the parent's tag along with the rest of the body.
    private static byte[] Rec(int tag, int schema, params byte[][] body)
    {
        byte[] data = body.SelectMany(b => b).ToArray();
        return [0xF8, (byte)tag, (byte)(schema >> 8), (byte)schema, .. Int(data.Length), .. data.Select(b => (byte)(b ^ tag))];
    }

    private static byte[] Field(int id, string name, int valueType, int length, int schema) =>
        Rec(4, schema, Int(id), Str(name), Int(0), [1], Int(valueType), Int(length), new byte[24]);

    private static byte[] Table(int id, string name, string alias, int fieldSchema, params (int Id, string Name, int Type)[] fields) =>
        Rec(3, 0x0905,
            [.. Int(id), .. Str(name), .. Int(0), 1, .. Str($"db.dbo.{name}"),
             .. Int(2), .. Str("db"), .. Str("dbo"), .. Int(1), .. Str(alias), .. Int(1), .. Int(fields.Length)],
            fields.SelectMany(f => Field(f.Id, f.Name, f.Type, 4, fieldSchema)).ToArray());

    private static byte[] Link(int id, int source, int target, int op, int join) =>
        Rec(10, 0x0901, Int(id), Int(source), Int(target), Int(op), Int(join), Int(1));

    [Test]
    public void GraphReader_ReadsTablesFieldsAndLinks_InEitherFieldSchema()
    {
        byte[] payload = Rec(1, 0x0902, Rec(2, 0x0902,
            Int(0), Str("crdb_odbc.dll"), Str("ODBC (RDO)"), Str("SomeDsn"),
            Table(10, "Orders", "Orders", 0x0905, (11, "Customer ID", 4), (12, "Region", 11)),
            Table(20, "Customer Master", "Customer", 0x0906, (21, "Customer ID", 4), (22, "Region", 11)),
            // Stored out of id order; the runtime lists links by id.
            Link(31, 12, 22, 4, 1),
            Link(30, 11, 21, 4, 2)));

        var graph = Majorsilence.Crystal.Parser.Sections.QeDependencyGraphReader.Read(payload);

        Assert.That(graph.DriverName, Is.EqualTo("crdb_odbc.dll"));
        Assert.That(graph.DatabaseType, Is.EqualTo("ODBC (RDO)"));
        Assert.That(graph.DatabaseName, Is.EqualTo("SomeDsn"));
        Assert.That(graph.Tables.Select(t => (t.Id, t.Name, t.Alias, t.QualifiedName)), Is.EqualTo(new[]
        {
            (10, "Orders", "Orders", "db.dbo.Orders"),
            (20, "Customer Master", "Customer", "db.dbo.Customer Master")
        }));
        Assert.That(graph.Tables[1].QualifierParts, Is.EqualTo(new[] { "db", "dbo" }));
        Assert.That(graph.Tables.SelectMany(t => t.Fields).Select(f => (f.Id, f.Name, f.ValueType)), Is.EqualTo(new[]
        {
            (11, "Customer ID", 4), (12, "Region", 11), (21, "Customer ID", 4), (22, "Region", 11)
        }), "fields read under schema 0x0905 and 0x0906 alike");
        Assert.That(graph.Links.Select(l => (l.Id, l.SourceFieldId, l.TargetFieldId, l.JoinTypeCode)), Is.EqualTo(new[]
        {
            (30, 11, 21, 2),
            (31, 12, 22, 1)
        }));
    }

    [Test]
    public void Converter_QualifiesDataFieldsByTable_AndJoinsTheQuery()
    {
        var report = Parse("benbrahim777__BeforeTV.rpt");

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<DataField>Customer.Customer Name</DataField>"));
        Assert.That(rdl, Does.Contain("<DataField>Orders.Order Amount</DataField>"));
        Assert.That(rdl, Does.Contain("Name=\"Customer_Name\""), "the RDL field name stays the bare column");
        Assert.That(rdl, Does.Contain(
            "FROM [Customer] INNER JOIN [Orders] ON [Orders].[Customer ID] = [Customer].[Customer ID]"));
    }

    [Test]
    public void Converter_WritesOuterJoinsAndCrossJoinsAsTheLinksSay()
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
                        new TableDefinition { Name = "A", Alias = "A", Columns = { new ColumnDefinition { Name = "id" } } },
                        new TableDefinition { Name = "B", Alias = "B", Columns = { new ColumnDefinition { Name = "a_id" } } },
                        new TableDefinition { Name = "Lookup", Alias = "C", Columns = { new ColumnDefinition { Name = "k" } } },
                    }
                }
            },
            TableLinks =
            {
                new TableLink { SourceTable = "A", SourceColumn = "id", TargetTable = "B", TargetColumn = "a_id", JoinType = TableJoinType.LeftOuter }
            }
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("FROM [A] LEFT OUTER JOIN [B] ON [A].[id] = [B].[a_id], [Lookup] [C]"));
    }
}
