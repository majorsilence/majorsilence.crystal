using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Where a report leaves "Convert Database NULL Values to Default" off, a formula that reads
/// a null field is null and prints nothing, as the Crystal runtime prints it; one that tests
/// the field with IsNull first still prints. Before, the null was read as "" or 0 and the
/// formula printed what it made of the rest.
/// </summary>
[TestFixture]
public class NullFieldFormulaTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Report(bool convertNulls) => new()
    {
        ReportTitle = "Names",
        ConvertDatabaseNulls = convertNulls,
        ConvertOtherNulls = convertNulls,
        Fields = [
            new DatabaseField { Name = "First", ColumnName = "First", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Last", ColumnName = "Last", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" },
            new FormulaField { Name = "FullName", FormulaText = "\"Name: \" + {T.First} + \" \" + {T.Last}", ResultType = "String" },
            new FormulaField { Name = "Surname", FormulaText = "if IsNull({T.Last}) then \"no surname\" else \"Surname: \" + {T.Last}", ResultType = "String" },
            new FormulaField { Name = "Greeting", FormulaText = "\"Dear \" + {@FullName}", ResultType = "String" },
            new FormulaField { Name = "Plus", FormulaText = "{T.Amount} + 1", ResultType = "Float64" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 960, Objects = [
                new FieldObject { Name = "FullNameField", FieldName = "@FullName", Bounds = new(0, 0, 4320, 240) },
                new FieldObject { Name = "SurnameField", FieldName = "@Surname", Bounds = new(4320, 0, 4320, 240) },
                new FieldObject { Name = "GreetingField", FieldName = "@Greeting", Bounds = new(0, 240, 4320, 240) },
                new FieldObject { Name = "PlusField", FieldName = "@Plus", Bounds = new(0, 480, 1440, 240) }] }]
    };

    private static async Task<List<string>> Printed(bool convertNulls)
    {
        var t = new DataTable();
        t.Columns.Add("First", typeof(string));
        t.Columns.Add("Last", typeof(string));
        t.Columns.Add("Amount", typeof(double));
        t.Rows.Add("Ann", "Lee", 41.0);
        t.Rows.Add("Bob", DBNull.Value, DBNull.Value);

        string dir = Path.Combine(Path.GetTempPath(), "nullformula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(convertNulls), new RuntimeOverrides { Data = t }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(x => x.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AFormulaReadingANullField_PrintsNothing()
    {
        var printed = await Printed(convertNulls: false);
        string all = string.Join(" | ", printed);
        Assert.Multiple(() =>
        {
            Assert.That(printed, Is.SupersetOf(new[] { "Name: Ann Lee", "Surname: Lee", "Dear Name: Ann Lee", "42" }), all);
            // Bob's surname is null: the formulas reading it, directly or through another
            // formula, print nothing; the one testing it with IsNull prints its own text.
            Assert.That(printed, Does.Contain("no surname"), all);
            Assert.That(printed.Where(p => p.StartsWith("Name: Bob") || p.StartsWith("Dear Name: Bob")), Is.Empty, all);
            Assert.That(printed.Count(p => p == "1"), Is.Zero, all);
        });
    }

    [Test]
    public async Task WhereTheReportConvertsNulls_TheFormulaReadsThemAsEmpty()
    {
        var printed = await Printed(convertNulls: true);
        Assert.That(printed.Where(p => p.StartsWith("Dear Name: Bob")), Is.Not.Empty, string.Join(" | ", printed));
    }
}
