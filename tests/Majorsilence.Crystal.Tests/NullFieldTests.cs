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
            Assert.That(values["Joined"], Does.StartWith("=Switch(Not (IsNothing(Fields!A.Value) Or IsNothing(Fields!B.Value)), "));
            Assert.That(values["Joined"], Does.EndWith(", False, Nothing)"));
            Assert.That(values["Branch"], Does.Not.Contain("Switch("));
            // Through a formula it reads, the fields that formula always reads.
            Assert.That(values["Nested"], Does.StartWith("=Switch(Not (IsNothing(Fields!A.Value) Or IsNothing(Fields!B.Value)), "));
            Assert.That(values["Defaults"], Does.Not.Contain("Switch("));
        });
    }

    [Test]
    public void WhereTheReportConvertsNulls_NoFormulaIsGuarded()
    {
        var values = FormulaValues(true, new FormulaField { Name = "Joined", FormulaText = "{T.A} + \" \" + {T.B}" });
        Assert.That(values["Joined"], Does.Not.Contain("IsNothing"));
    }
}
