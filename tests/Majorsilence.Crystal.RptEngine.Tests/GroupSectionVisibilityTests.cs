using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A grouped report with nothing in Details: its group sections print once per group, as a
/// summary report's do, from table groups of their own. With no rows Crystal prints no group
/// section at all. This mattered once a subreport with no rows began printing its headers
/// (#38): one placed in such a section printed on a report with no data.
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

    private static async Task<List<string>> Printed(DataTable? data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "groupvis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var report = await ReportEngine.LoadAsync(Report(), new RuntimeOverrides { Data = data }, dir, []);
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
    public async Task WithNoRows_OnlyTheReportHeaderPrints()
    {
        var printed = await Printed(null);

        Assert.That(printed, Does.Contain("TITLE"));
        Assert.That(printed, Does.Not.Contain("GROUP-FOOTER"));
    }
}
