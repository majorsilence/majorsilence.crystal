using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The report's null options and each formula's null treatment, and the formula values a
/// report that keeps database nulls is given: a formula that reads a null field is null.
/// </summary>
[TestFixture]
public class NullFieldTests
{
    private static string Corpus(string name)
    {
        string path = Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");
        return path;
    }

    // The Crystal runtime reports "Convert Database NULL Values to Default" on and "Convert
    // Other NULL Values to Default" off for this report, and its Remarks formula as using
    // default values for nulls where the others take nulls as exceptions.
    [Test]
    public void TheNullOptions_AreReadAsTheRuntimeReportsThem()
    {
        var picklist = RptParser.Parse(Corpus("boyum__Picklist.rpt")).Report!;
        var journal = RptParser.Parse(Corpus("boyum__JournalEntry.rpt")).Report!;
        FormulaField Formula(ReportDefinition r, string name) => r.Fields.OfType<FormulaField>().Single(f => f.Name == name);

        Assert.Multiple(() =>
        {
            Assert.That(picklist.ConvertDatabaseNulls, Is.True);
            Assert.That(picklist.ConvertOtherNulls, Is.False);
            Assert.That(Formula(picklist, "Remarks").DefaultValuesForNulls, Is.True);
            Assert.That(Formula(picklist, "PickDate").DefaultValuesForNulls, Is.False);
            Assert.That(journal.ConvertDatabaseNulls, Is.False);
            Assert.That(Formula(journal, "Title").DefaultValuesForNulls, Is.True);
            Assert.That(Formula(journal, "PostingDate").DefaultValuesForNulls, Is.False);
        });
    }

    private static Dictionary<string, string> FormulaValues(bool convertNulls, params FormulaField[] formulas)
    {
        var report = new ReportDefinition
        {
            ConvertDatabaseNulls = convertNulls,
            ConvertOtherNulls = false,
            Fields = [
                new DatabaseField { Name = "A", ColumnName = "A", TableName = "T", DataType = "String" },
                new DatabaseField { Name = "B", ColumnName = "B", TableName = "T", DataType = "String" },
                new DatabaseField { Name = "C", ColumnName = "C", TableName = "T", DataType = "String" },
                .. formulas],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AField", FieldName = "A", Bounds = new(0, 0, 1440, 240) }] }]
        };
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "Field").Where(f => f.Element(ns + "Value") is not null)
            .ToDictionary(f => f.Attribute("Name")!.Value, f => f.Element(ns + "Value")!.Value);
    }

    [Test]
    public void AFormula_IsNullWhenAFieldItAlwaysReadsIsNull()
    {
        var values = FormulaValues(false,
            new FormulaField { Name = "Joined", FormulaText = "{T.A} + \" \" + {T.B}" },
            // A branch, the right side of Or, and the argument of IsNull are not always read.
            new FormulaField { Name = "Branch", FormulaText = "if IsNull({T.A}) or {T.B} = \"\" then \"none\" else {T.C}" },
            new FormulaField { Name = "Nested", FormulaText = "\"x\" + {@Joined}" },
            new FormulaField { Name = "Defaults", FormulaText = "{T.A} + {T.B}", DefaultValuesForNulls = true });

        Assert.Multiple(() =>
        {
            Assert.That(values["Joined"], Does.StartWith("=Switch(Not (Fields!A.Value = Nothing Or Fields!B.Value = Nothing), "));
            Assert.That(values["Joined"], Does.EndWith(", False, Nothing)"));
            Assert.That(values["Branch"], Does.Not.Contain("Switch("));
            // Through a formula it reads, the fields that formula always reads.
            Assert.That(values["Nested"], Does.StartWith("=Switch(Not (Fields!A.Value = Nothing Or Fields!B.Value = Nothing), "));
            Assert.That(values["Defaults"], Does.Not.Contain("Switch("));
        });
    }

    [Test]
    public void WhereTheReportConvertsNulls_NoFormulaIsGuarded()
    {
        var values = FormulaValues(true, new FormulaField { Name = "Joined", FormulaText = "{T.A} + \" \" + {T.B}" });
        Assert.That(values["Joined"], Does.Not.Contain("Switch("));
    }

    private static List<System.Xml.Linq.XElement> DataSetFields(bool convertNulls, params FormulaField[] formulas)
    {
        var report = new ReportDefinition
        {
            ConvertDatabaseNulls = convertNulls,
            Fields = [
                new DatabaseField { Name = "Name", ColumnName = "Name", TableName = "T", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" },
                new DatabaseField { Name = "Paid", ColumnName = "Paid", TableName = "T", DataType = "Boolean" },
                new DatabaseField { Name = "Due", ColumnName = "Due", TableName = "T", DataType = "DateTime" },
                .. formulas],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "NameField", FieldName = "Name", Bounds = new(0, 0, 1440, 240) }] }]
        };
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "DataSet").Single().Descendants(ns + "Field").ToList();
    }

    private static string? Child(System.Xml.Linq.XElement field, string name) =>
        field.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    // The Crystal runtime reads a null string, number and boolean as "", 0 and False where
    // "Convert Database NULL Values to Default" is on, for a null the table holds and for a
    // record with no match across an outer join alike. The column is read under another name
    // and the field the report uses gives the default; a date, whose default prints blank as a
    // null does, is read as it was.
    [Test]
    public void WhereTheReportConvertsNulls_AFieldReadsANullAsItsDefault()
    {
        var fields = DataSetFields(true);
        string? Value(string name) => Child(fields.Single(f => f.Attribute("Name")!.Value == name), "Value");
        string? DataField(string name) => Child(fields.Single(f => f.Attribute("Name")!.Value == name), "DataField");

        Assert.Multiple(() =>
        {
            Assert.That(Value("Name"), Is.EqualTo("=IIf(Fields!Name_Stored.Value <> Nothing, Fields!Name_Stored.Value, \"\")"));
            Assert.That(Value("Amount"), Is.EqualTo("=IIf(Fields!Amount_Stored.Value <> Nothing, Fields!Amount_Stored.Value, 0)"));
            Assert.That(Value("Paid"), Is.EqualTo("=IIf(Fields!Paid_Stored.Value <> Nothing, Fields!Paid_Stored.Value, False)"));
            Assert.That(DataField("Amount_Stored"), Is.EqualTo("T.Amount"));
            Assert.That(DataField("Due"), Is.EqualTo("T.Due"));
            Assert.That(fields.Select(f => f.Attribute("Name")!.Value), Has.None.EqualTo("Due_Stored"));
        });
    }

    [Test]
    public void WhereTheReportKeepsNulls_AFieldReadsTheColumn()
    {
        var fields = DataSetFields(false);
        Assert.Multiple(() =>
        {
            Assert.That(fields.Select(f => f.Attribute("Name")!.Value), Has.None.EndsWith("_Stored"));
            Assert.That(Child(fields.Single(f => f.Attribute("Name")!.Value == "Amount"), "DataField"), Is.EqualTo("T.Amount"));
        });
    }

    // IsNull of a database field is False where the report converts nulls, a date's too, as
    // the Crystal runtime gives it; of a parameter or formula it is the test it was.
    [Test]
    public void WhereTheReportConvertsNulls_IsNullOfADatabaseFieldIsFalse()
    {
        FormulaField[] formulas = [
            new() { Name = "NoDate", FormulaText = "if IsNull({T.Due}) then \"none\" else \"due\"" },
            new() { Name = "OfFormula", FormulaText = "IsNull({@NoDate})" }];
        string? Formula(bool convert, string name) =>
            Child(DataSetFields(convert, formulas).Single(f => f.Attribute("Name")!.Value == name), "Value");

        Assert.Multiple(() =>
        {
            Assert.That(Formula(true, "NoDate"), Is.EqualTo("=IIf(False, \"none\", \"due\")"));
            Assert.That(Formula(true, "OfFormula"), Is.EqualTo("=(Fields!NoDate.Value = Nothing)"));
            Assert.That(Formula(false, "NoDate"), Does.Contain("(Fields!Due.Value = Nothing)"));
        });
    }
}
