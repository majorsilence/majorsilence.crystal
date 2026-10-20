using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A group's options as the file holds them. Tag-229, after the group's field name: the date
/// interval at +3 and the sort direction at +4; tag-136, the group's area pair, opens with
/// "repeat group header on each page". The expected values are the Crystal runtime's own
/// (its group options and group sort direction for each report), and each byte was confirmed
/// by changing that one setting through the runtime's object model and comparing the copies.
/// </summary>
[TestFixture]
public class GroupOptionTests
{
    private static string Corpus(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Report(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!;
    }

    [TestCase("parking__orders.rpt", GroupCondition.Monthly)]
    [TestCase("parking__orders_bar_percentage.rpt", GroupCondition.Monthly)]
    [TestCase("parking__orders_weekly.rpt", GroupCondition.Weekly)]
    [TestCase("parking__orders_pie.rpt", GroupCondition.Weekly)]
    [TestCase("meridian_freight__11_monthly_volume_trend.rpt", GroupCondition.Monthly)]
    public void AGroupOnADate_BreaksOnItsInterval(string file, GroupCondition expected)
    {
        var group = Report(External(file)).Groups[0];

        Assert.That(group.Condition, Is.EqualTo(expected));
        Assert.That(group.SortOrder, Is.EqualTo(GroupSortOrder.Ascending));
    }

    [Test]
    public void AGroupOnAFormulaThatIsNotADate_BreaksOnEachValue()
    {
        // The interval byte is 0 here, which on a date would mean "for each day".
        var group = Report(External("meridian_freight__10_transit_time_analysis.rpt")).Groups[0];

        Assert.That(group.FieldName, Is.EqualTo("@TransitDays"));
        Assert.That(group.Condition, Is.EqualTo(GroupCondition.EachValue));
    }

    [Test]
    public void ADescendingGroup_IsReadAsDescending_NotAsRepeatingItsHeader()
    {
        // The bytes after the interval were once read as "repeat group header"; they are the
        // sort direction, and this is the corpus's one descending group.
        var group = Report(External("worrall__MostRecentStructuringOfCanadianCities.rpt")).Groups[0];

        Assert.That(group.SortOrder, Is.EqualTo(GroupSortOrder.Descending));
        Assert.That(group.RepeatGroupHeader, Is.False);
    }

    [Test]
    public void RepeatGroupHeader_IsReadFromTheGroupsArea()
    {
        var report = Report(Corpus("souvikduttachoudhury__CustomFunctions.rpt"));

        Assert.That(report.Groups, Is.Not.Empty);
        Assert.That(report.Groups.All(g => g.RepeatGroupHeader), Is.True);
        Assert.That(report.Sections.Where(s => s.Type == SectionType.GroupHeader).All(s => s.RepeatGroupHeader), Is.True);
    }

    [Test]
    public void RepeatGroupHeader_IsOffWhereTheAreaSaysSo()
    {
        var report = Report(External("parking__orders.rpt"));

        Assert.That(report.Groups.Any(g => g.RepeatGroupHeader), Is.False);
    }

    [Test]
    public void AMonthlyGroup_GroupsAndSortsOnTheMonth_AndIsNamedByIt()
    {
        string rdl = new RdlConverter().Convert(Report(External("parking__orders.rpt")));

        // The chart over the group breaks on its month too, labelled with the group's name.
        Assert.That(rdl, Does.Contain("Format(DateSerial(Year(Fields!created_at.Value), Month(Fields!created_at.Value), 1), \"yyyy-MM\")"));
        Assert.That(rdl, Does.Contain("<SortExpression>=DateSerial(Year(Fields!created_at.Value), Month(Fields!created_at.Value), 1)</SortExpression>"));
    }
}
