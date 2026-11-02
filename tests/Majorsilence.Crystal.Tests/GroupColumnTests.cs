using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A group on a column whose name another table's column shares groups on its own table's
/// column, as Crystal does; and the group header and report header print only what their
/// objects print.
/// </summary>
[TestFixture]
public class GroupColumnTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Report(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!;
    }

    // A customer table and an invoice table, both with a Code column: the page prints the
    // customer's, the report groups on the invoice's.
    private static ReportDefinition Invoices(bool convertNulls = true) => new()
    {
        ConvertDatabaseNulls = convertNulls,
        Fields =
        [
            new DatabaseField { Name = "Code", ColumnName = "Code", TableName = "Customers", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Invoices", DataType = "Float64" },
            new DatabaseField { Name = "Code", ColumnName = "Code", TableName = "Invoices", DataType = "String" },
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Code", TableName = "Invoices" }],
        Sections =
        [
            new Section { Type = SectionType.PageHeader, HeightTwips = 240, Objects = [
                new FieldObject { Name = "CustomerCode", FieldName = "Code", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                new TextObject { Name = "AmountLabel", Text = "Amount", Bounds = new(2880, 0, 1440, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240, Objects = [
                new FieldObject { Name = "Total", FieldName = "Amount", SummaryFunction = AggregateFunction.Sum,
                    Bounds = new(2880, 0, 1440, 240) }] },
        ]
    };

    private static XDocument Converted(ReportDefinition report) => XDocument.Parse(new RdlConverter().Convert(report));

    private static XElement? DataSetField(XDocument rdl, string name) =>
        rdl.Descendants(Rdl + "Field").FirstOrDefault(f => (string?)f.Attribute("Name") == name);

    private static string? GroupExpression(XDocument rdl) =>
        (string?)rdl.Descendants(Rdl + "Grouping").First(g => (string?)g.Attribute("Name") == "Group1")
            .Descendants(Rdl + "GroupExpression").First();

    [TestCase(true)]
    [TestCase(false)]
    public void AGroupOnTheSecondTablesColumn_GroupsOnThatColumn(bool convertNulls)
    {
        // The DataSet held one Code, the customer table's, and the group broke one invoice into
        // a group per customer row.
        var rdl = Converted(Invoices(convertNulls));

        Assert.Multiple(() =>
        {
            Assert.That(GroupExpression(rdl), Is.EqualTo("=Fields!Invoices_Code.Value"));
            Assert.That(rdl.Descendants(Rdl + "DataField").Select(d => d.Value),
                Is.SupersetOf(new[] { "Customers.Code", "Invoices.Code" }));
            Assert.That(DataSetField(rdl, "Invoices_Code"), Is.Not.Null);
            // The page still prints the customer table's code.
            Assert.That(rdl.Descendants(Rdl + "Value").Select(v => v.Value), Does.Contain("=Fields!Code.Value"));
        });
    }

    [Test]
    public void AGroupedColumn_IsWrittenAsItsTablesOtherColumnsAre()
    {
        // Where the report converts nulls the column is read under a stored name, and the field
        // the group reads is a calculated one; the engine sums a group's records from a header
        // only through such a field.
        var rdl = Converted(Invoices(convertNulls: true));

        Assert.Multiple(() =>
        {
            Assert.That(DataSetField(rdl, "Invoices_Code_Stored")?.Element(Rdl + "DataField")?.Value, Is.EqualTo("Invoices.Code"));
            Assert.That(DataSetField(rdl, "Invoices_Code")?.Element(Rdl + "Value")?.Value,
                Does.StartWith("=IIf(Fields!Invoices_Code_Stored.Value <> Nothing"));
        });
    }

    [Test]
    public void AGroupOnTheFirstTablesColumn_IsUnchanged()
    {
        var report = Invoices();
        report.Groups[0].TableName = "Customers";
        var rdl = Converted(report);

        Assert.Multiple(() =>
        {
            Assert.That(GroupExpression(rdl), Is.EqualTo("=Fields!Code.Value"));
            Assert.That(DataSetField(rdl, "Customers_Code"), Is.Null);
            Assert.That(DataSetField(rdl, "Invoices_Code"), Is.Null);
        });
    }

    [Test]
    public void AGroupOnAColumnOfTheSameNameInEachTable_ReadsEachTablesColumn()
    {
        // The freight manifest groups on region, country, province, city, facility, carrier and
        // service level, each by its name column; every one read the shipment mode's name.
        var report = Report(External("meridian_freight__07_operational_manifest.rpt"));
        var rdl = Converted(report);
        var dataFields = rdl.Descendants(Rdl + "DataField").Select(d => d.Value).ToList();

        Assert.Multiple(() =>
        {
            foreach (var g in report.Groups)
                Assert.That(report.Fields.OfType<DatabaseField>().Any(f => f.ColumnName == g.FieldName && f.TableName == g.TableName),
                    $"{g.TableName}.{g.FieldName}");
            Assert.That(dataFields, Is.SupersetOf(new[] { "region.name", "country.name", "province.name", "city.name" }));
            Assert.That(GroupExpression(rdl), Is.EqualTo("=Fields!region_name.Value"));
        });
    }

    [Test]
    public void AGroupHeaderWithOnlyLabelsAcross_PrintsNoGroupName()
    {
        // Labels above the details and no field object: Crystal prints no group name, and the
        // header's first cell printed the invoice code over the first label.
        var rdl = Converted(Invoices());
        var header = rdl.Descendants(Rdl + "TableGroup").First().Element(Rdl + "Header")!;

        Assert.That(header.Descendants(Rdl + "Value").Select(v => v.Value).Where(v => v.StartsWith("=Fields!")), Is.Empty);
    }

    [Test]
    public void ABlankSectionWhoseObjectsAreAllSuppressed_PrintsNothing()
    {
        ReportObject Hidden() => new FieldObject { Name = "Hidden", FieldName = "Code", SuppressOverride = true };

        Assert.Multiple(() =>
        {
            Assert.That(RptParser.BlankSectionPrintsNothing(true, false, [Hidden(), Hidden()]), Is.True);
            // Not set to suppress blank, or a suppress formula of its own: left as it was.
            Assert.That(RptParser.BlankSectionPrintsNothing(false, false, [Hidden()]), Is.False);
            Assert.That(RptParser.BlankSectionPrintsNothing(true, true, [Hidden()]), Is.False);
            // An object that prints, or one a formula decides.
            Assert.That(RptParser.BlankSectionPrintsNothing(true, false,
                [Hidden(), new TextObject { Name = "Shown", Text = "Shown" }]), Is.False);
            var byFormula = Hidden();
            byFormula.SuppressFormula = "{?Letterhead} = true";
            Assert.That(RptParser.BlankSectionPrintsNothing(true, false, [byFormula]), Is.False);
            // An empty section is left to the rest of the converter.
            Assert.That(RptParser.BlankSectionPrintsNothing(true, false, []), Is.False);
        });
    }
}
