using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A formula written as statements over variables of its own prints what Crystal computes
/// (#19): an address assembled from optional lines, the commonest such formula. It printed
/// nothing before, because an RDL expression has no variables.
/// </summary>
[TestFixture]
public class StatementFormulaTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private const string Address = """
        Local StringVar address := {T.Line1};
        if {T.Line2} <> "" then address := address + ", " + {T.Line2};
        if {T.City} <> "" then
        (
            address := address + ", " + {T.City};
            if {T.Code} <> "" then address := address + " " + {T.Code};
        );
        address
        """;

    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Addresses",
        Fields = [
            new DatabaseField { Name = "Line1", ColumnName = "Line1", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Line2", ColumnName = "Line2", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "City", ColumnName = "City", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Code", ColumnName = "Code", TableName = "T", DataType = "String" },
            new FormulaField { Name = "Address", FormulaText = Address, ResultType = "String" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AddressField", FieldName = "@Address", Bounds = new(0, 0, 8640, 240) }] }]
    };

    [Test]
    public async Task AnAddressBuiltLineByLine_Prints()
    {
        var t = new DataTable();
        foreach (var c in new[] { "Line1", "Line2", "City", "Code" }) t.Columns.Add(c, typeof(string));
        t.Rows.Add("1 Main St", "", "Springfield", "A1B 2C3");
        t.Rows.Add("2 Side Rd", "Unit 4", "", "Z9Z 9Z9");
        t.Rows.Add("3 Hill Ave", "Floor 2", "Shelbyville", "");

        string dir = Path.Combine(Path.GetTempPath(), "stmtformula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = t }, dir, []);
            using var pages = await report.BuildPages();
            var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(x => x.Text).ToList();

            Assert.That(printed, Is.SupersetOf(new[]
            {
                "1 Main St, Springfield A1B 2C3",
                // No city, so the postal code inside that branch is not added either.
                "2 Side Rd, Unit 4",
                "3 Hill Ave, Floor 2, Shelbyville",
            }), string.Join(" | ", printed));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
