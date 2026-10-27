using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A section's Keep Together and a group's Keep Group Together as the file holds them: the
/// Int16 at 13..14 of a section-level tag-254 record, and the second Int16 of the group's
/// area pair (tag-136). The expected values are the Crystal runtime's own
/// (SectionFormat.EnableKeepTogether and GroupAreaFormat.EnableKeepGroupTogether), which
/// both agree with for every section and group of all three corpora.
/// </summary>
[TestFixture]
public class KeepTogetherTests
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

    [Test]
    public void EverySectionOfAReportThatLeavesTheDefault_KeepsTogether()
    {
        var report = Report(Corpus("benbrahim777__ChinaOrders-Grouped.rpt"));

        Assert.That(report.Sections, Is.Not.Empty);
        Assert.That(report.Sections.All(s => s.KeepTogether), Is.True);
    }

    [Test]
    public void ASectionThatTurnsItOff_IsReadAsOff_AndOnlyThatSection()
    {
        // The runtime: the four report header sections keep together, the page header, the
        // details and the report footer do not, and of the two page footer sections only the
        // 510-twip one does.
        var report = Report(Corpus("souvikduttachoudhury__ConsolidatedBalanceSheet.rpt"));
        bool Keeps(SectionType type) => report.Sections.Where(s => s.Type == type).All(s => s.KeepTogether);
        bool NoneKeep(SectionType type) => report.Sections.Where(s => s.Type == type).All(s => !s.KeepTogether);

        Assert.That(report.Sections.Count(s => s.Type == SectionType.ReportHeader), Is.EqualTo(4));
        Assert.That(Keeps(SectionType.ReportHeader), Is.True);
        Assert.That(NoneKeep(SectionType.PageHeader), Is.True);
        Assert.That(NoneKeep(SectionType.Details), Is.True);
        Assert.That(NoneKeep(SectionType.ReportFooter), Is.True);
        var pageFooters = report.Sections.Where(s => s.Type == SectionType.PageFooter)
            .ToDictionary(s => s.HeightTwips, s => s.KeepTogether);
        Assert.That(pageFooters, Is.EqualTo(new Dictionary<int, bool> { [2850] = false, [510] = true }));
    }

    [Test]
    public void KeepGroupTogether_IsReadFromTheGroupsArea()
    {
        Assert.That(Report(Corpus("benbrahim777__Top5USA.rpt")).Groups.Single().KeepTogether, Is.True);
        Assert.That(Report(External("parking__orders.rpt")).Groups.Any(g => g.KeepTogether), Is.False);
    }
}
