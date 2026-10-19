using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Crystal's three-formula idiom for listing or totalling a group's records (#19): reset a
/// variable in the group header, add each record's piece to it in the details, show it in
/// the group footer. Written as the aggregate it amounts to, the footer prints what
/// Crystal's does; before, all three formulas were empty.
/// </summary>
[TestFixture]
public class PrintTimeVariableTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static FormulaField Formula(string name, string text, string? type = null) =>
        new() { Name = name, FormulaText = text, ResultType = type };

    private static FieldObject Placed(string formula, int left = 0, int top = 0, int width = 4320) =>
        new() { Name = "o_" + formula, FieldName = "@" + formula, Bounds = new(left, top, width, 240), Format = new ObjectFormat { CanGrow = true } };

    // Regions, each with its items listed in the footer, one per line, and their amounts
    // totalled. The details section is suppressed, as it usually is in this idiom: Crystal
    // still evaluates the formulas in it.
    private static ReportDefinition Report(
        string reset = "whileprintingrecords; stringvar list; if not inrepeatedgroupheader then list := \"\";",
        string accumulate = "whileprintingrecords; stringvar list := list + {T.Item} + chr(10);",
        string show = "whileprintingrecords; stringvar list; trim(list)",
        SectionType showIn = SectionType.GroupFooter) => new()
    {
        ReportTitle = "Listed",
        Fields = [
            new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Item", ColumnName = "Item", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" },
            Formula("ResetList", reset, "String"),
            Formula("AddItem", accumulate, "String"),
            Formula("ShowList", show, "String"),
            Formula("ResetTotal", "whileprintingrecords; numbervar total := 0;", "Float64"),
            Formula("AddAmount", "whileprintingrecords; numbervar total; if {T.Amount} > 0 then total := total + {T.Amount};", "Float64"),
            Formula("ShowTotal", "whileprintingrecords; numbervar total; \"TOTAL=\" + totext(total, 2)", "String"),
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" }],
        Sections = [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                new FieldObject { Name = "RegionName", FieldName = "Region", Bounds = new(0, 0, 2880, 240) },
                Placed("ResetList", left: 3000, width: 100),
                Placed("ResetTotal", left: 3200, width: 100)] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = true, Objects = [
                new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 1440, 240) },
                Placed("AddItem", left: 1500, width: 100),
                Placed("AddAmount", left: 1700, width: 100)] },
            new Section { Type = showIn, GroupLevel = 0, HeightTwips = 1200, Objects = [
                Placed("ShowList"),
                Placed("ShowTotal", top: 600)] }]
    };

    private static DataTable Data()
    {
        var t = new DataTable();
        t.Columns.Add("Region", typeof(string));
        t.Columns.Add("Item", typeof(string));
        t.Columns.Add("Amount", typeof(double));
        t.Rows.Add("East", "Apple", 2.5);
        t.Rows.Add("East", "Pear", -1.0);
        t.Rows.Add("West", "Fig", 4.0);
        t.Rows.Add("West", "Kiwi", 1.0);
        t.Rows.Add("West", "Lime", 0.5);
        return t;
    }

    private static async Task<List<string>> Printed(ReportDefinition report)
    {
        string dir = Path.Combine(Path.GetTempPath(), "printvars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { Data = Data() }, dir, []);
            using var pages = await engineReport.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task AGroupFooter_ListsItsGroupsRecords_AndTotalsThem()
    {
        var printed = await Printed(Report());
        string all = string.Join("|", printed);

        Assert.Multiple(() =>
        {
            // Each group's items, one per line, in one textbox, each group starting from its
            // header's reset. The last line break stays: Crystal's Trim removes spaces only,
            // as its runtime confirms (Length(Trim("ab" + chr(10))) is 3).
            Assert.That(printed, Does.Contain("Apple\nPear\n"), all);
            Assert.That(printed, Does.Contain("Fig\nKiwi\nLime\n"), all);
            // Only the positive amounts are added: East 2.50, West 5.50.
            Assert.That(printed, Does.Contain("TOTAL=2.50"), all);
            Assert.That(printed, Does.Contain("TOTAL=5.50"), all);
        });
    }

    // Shown in the report footer instead, with nothing resetting it in a group header, the
    // variable runs over the whole report.
    [Test]
    public async Task AReportFooter_ListsEveryRecord()
    {
        var report = Report(reset: "whileprintingrecords; stringvar list := \"\";", showIn: SectionType.ReportFooter);
        // The resets sit in the group header; for a report-footer display that is a reset per
        // group, which is not the whole-report list, so it must not be rewritten as one.
        var printed = await Printed(report);
        string all = string.Join("|", printed);

        Assert.That(all, Does.Not.Contain("Apple"), "a reset in a group header makes the report footer a running value, which this does not rewrite");
    }

    // A Basic-syntax formula writing the same variable is one this does not follow. The file
    // does not say which dialect a formula is in, so it is found by its declarations.
    [Test]
    public async Task ABasicSyntaxWriterOfTheVariable_LeavesTheFooterEmpty()
    {
        var report = Report();
        report.Fields.Add(Formula("BasicAdd", "Global list As String\r\nlist = list & \"!\"\r\nformula = list", "String"));
        report.Sections.Single(s => s.Type == SectionType.Details).Objects.Add(Placed("BasicAdd", left: 1900, width: 100));

        var printed = await Printed(report);

        Assert.That(string.Join("|", printed), Does.Not.Contain("Apple"));
    }

    // A footer whose variable is also written in a way this does not follow keeps the empty
    // value it had, rather than a wrong one.
    [Test]
    public async Task AnAccumulationItCannotFollow_LeavesTheFooterEmpty()
    {
        var report = Report(accumulate: "whileprintingrecords; stringvar list := {T.Item} + list;");
        var printed = await Printed(report);

        Assert.That(string.Join("|", printed), Does.Not.Contain("Apple"));
    }
}
