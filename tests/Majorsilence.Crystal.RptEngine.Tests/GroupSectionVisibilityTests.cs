using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A grouped report with nothing in Details: its group sections print once per group, as a
/// summary report's do, from table groups of their own. With no rows Crystal still makes one
/// pass over the group sections and the Details, so each prints once, its fields blank: a
/// collection letter laid out in group footers prints the letter. Grouped with a details
/// table, the same holds.
/// </summary>
[TestFixture]
public class GroupSectionVisibilityTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    // Grouped, with nothing in Details, so there is no details table and the group footer is
    // written free-form.
    private static ReportDefinition Report() => new()
    {
        ReportTitle = "Grouped",
        Fields = [new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "T", DataType = "String" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" }],
        Sections = [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 300, Objects = [
                new TextObject { Name = "Title", Text = "TITLE", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.GroupHeader, HeightTwips = 0, GroupLevel = 0 },
            new Section { Type = SectionType.Details, HeightTwips = 0 },
            new Section { Type = SectionType.GroupFooter, HeightTwips = 300, GroupLevel = 0, Objects = [
                new TextObject { Name = "FooterText", Text = "GROUP-FOOTER", Bounds = new(0, 0, 2880, 240) }] }]
    };

    // Grouped, with a field in Details, so there is a details table. The group header holds a
    // label and an embedded field; Details a field and a label of its own.
    private static ReportDefinition WithDetails() => new()
    {
        ReportTitle = "GroupedDetails",
        Fields = [
            new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "T", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T" }],
        Sections = [
            new Section { Type = SectionType.GroupHeader, HeightTwips = 300, GroupLevel = 0, Objects = [
                new TextObject { Name = "RegionLine", Text = "Region: {T.Region}", Bounds = new(0, 0, 4320, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects = [
                new TextObject { Name = "DetailLabel", Text = "LINE", Bounds = new(0, 0, 1440, 240) },
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, HeightTwips = 300, GroupLevel = 0, Objects = [
                new TextObject { Name = "FooterText", Text = "GROUP-FOOTER", Bounds = new(0, 0, 2880, 240) }] }]
    };

    private static async Task<List<string>> Printed(DataTable? data) => await Printed(Report(), data);

    private static async Task<List<string>> Printed(ReportDefinition definition, DataTable? data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "groupvis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(definition, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task WithRows_TheGroupSectionPrints()
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Rows.Add("West");

        var printed = await Printed(data);

        Assert.That(printed, Does.Contain("TITLE"));
        Assert.That(printed, Does.Contain("GROUP-FOOTER"));
    }

    [Test]
    public async Task WithSeveralGroups_TheGroupSectionPrintsOncePerGroup()
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        foreach (var region in new[] { "West", "East", "West", "North" })
            data.Rows.Add(region);

        var printed = await Printed(data);

        Assert.That(printed.Count(t => t == "GROUP-FOOTER"), Is.EqualTo(3));
        Assert.That(printed.Count(t => t == "TITLE"), Is.EqualTo(1));
    }

    [Test]
    public async Task WithNoRows_TheGroupSectionPrintsOnce()
    {
        var printed = await Printed(null);

        Assert.That(printed.Count(t => t == "TITLE"), Is.EqualTo(1));
        Assert.That(printed.Count(t => t == "GROUP-FOOTER"), Is.EqualTo(1));
    }

    private static DataTable Regions(params (string Region, double Amount)[] rows)
    {
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Amount", typeof(double));
        foreach (var (region, amount) in rows)
            data.Rows.Add(region, amount);
        return data;
    }

    [Test]
    public async Task WithADetailsTable_AndNoRows_EachSectionPrintsOnce_ItsFieldsBlank()
    {
        var printed = await Printed(WithDetails(), Regions());

        // The text around an embedded field with no value still prints.
        Assert.That(printed.Count(t => t.TrimEnd() == "Region:"), Is.EqualTo(1));
        Assert.That(printed.Count(t => t == "LINE"), Is.EqualTo(1));
        Assert.That(printed.Count(t => t == "GROUP-FOOTER"), Is.EqualTo(1));
    }

    // An ungrouped report makes the same pass: with no rows its Details prints once.
    private static ReportDefinition Ungrouped() => new()
    {
        ReportTitle = "Ungrouped",
        Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 300, Objects = [
                new TextObject { Name = "DetailLabel", Text = "LINE", Bounds = new(0, 0, 1440, 240) },
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) }] }]
    };

    [Test]
    public async Task Ungrouped_WithNoRows_TheDetailsPrintOnce_AndWithRows_OncePerRow()
    {
        var amounts = new DataTable();
        amounts.Columns.Add("Amount", typeof(double));
        var none = await Printed(Ungrouped(), amounts.Clone());
        amounts.Rows.Add(1.0);
        amounts.Rows.Add(2.0);
        var two = await Printed(Ungrouped(), amounts);

        Assert.That(none.Count(t => t == "LINE"), Is.EqualTo(1));
        Assert.That(two.Count(t => t == "LINE"), Is.EqualTo(2));
    }

    [Test]
    public async Task WithADetailsTable_AndRows_NothingPrintsTwice()
    {
        var printed = await Printed(WithDetails(), Regions(("West", 1), ("East", 2), ("West", 3)));

        Assert.That(printed.Count(t => t.StartsWith("Region:", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(printed.Count(t => t == "LINE"), Is.EqualTo(3));
        Assert.That(printed.Count(t => t == "GROUP-FOOTER"), Is.EqualTo(2));
    }
}
