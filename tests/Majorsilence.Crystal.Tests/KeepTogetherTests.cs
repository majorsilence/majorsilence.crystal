using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
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

    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static ReportDefinition Child() => new()
    {
        ReportTitle = "Child",
        Fields = [new DatabaseField { Name = "Item", ColumnName = "Item", TableName = "Orders", DataType = "String" }],
        Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
            new FieldObject { Name = "ItemField", FieldName = "Item", Bounds = new(0, 0, 2880, 240) }] }]
    };

    private static ReportDefinition Ledger(Section footer) => new()
    {
        ReportTitle = "Ledger",
        Fields = [new DatabaseField { Name = "Line", ColumnName = "Line", TableName = "T", DataType = "String" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "LineField", FieldName = "T.Line", Bounds = new(0, 0, 2880, 240) }] },
            footer,
        ]
    };

    // The band Rectangle that holds the text object reading the given text.
    private static XElement Band(ReportDefinition report, string text) =>
        XDocument.Parse(new RdlConverter().Convert(report)).Descendants(Rdl + "Textbox")
            .Single(t => (string?)t.Element(Rdl + "Value") == text)
            .Ancestors(Rdl + "Rectangle").First();

    private static string? KeepTogether(XElement band) => (string?)band.Element(Rdl + "KeepTogether");

    // A report footer that grows with its subreport moves whole to the next page when it does
    // not fit, as Crystal's Keep Together moves it, so its band is marked for the engine.
    [Test]
    public void AGrowingSectionThatKeepsTogether_MarksItsBand()
    {
        var footer = new Section { Type = SectionType.ReportFooter, HeightTwips = 600, Objects = [
            new TextObject { Name = "Caption", Text = "Jobs", Bounds = new(0, 0, 1440, 240) },
            new SubreportObject { Name = "Jobs", SubreportName = "Jobs", Bounds = new(0, 240, 4320, 300), Report = Child() }] };

        Assert.That(KeepTogether(Band(Ledger(footer), "Jobs")), Is.EqualTo("true"));
    }

    [Test]
    public void AGrowingFieldMarksItsBand_TooButNotWithKeepTogetherOff()
    {
        Section Footer(bool keep) => new() { Type = SectionType.ReportFooter, HeightTwips = 600, KeepTogether = keep, Objects = [
            new TextObject { Name = "Caption", Text = "Notes", Bounds = new(0, 0, 1440, 240) },
            new FieldObject { Name = "NoteField", FieldName = "T.Line", Bounds = new(1440, 0, 2880, 240),
                Format = new ObjectFormat { CanGrow = true } }] };

        Assert.Multiple(() =>
        {
            Assert.That(KeepTogether(Band(Ledger(Footer(true)), "Notes")), Is.EqualTo("true"));
            Assert.That(KeepTogether(Band(Ledger(Footer(false)), "Notes")), Is.Null);
        });
    }

    // A band of fixed height already moves whole, so it is left as it was.
    [Test]
    public void ABandThatCannotGrow_IsNotMarked()
    {
        var footer = new Section { Type = SectionType.ReportFooter, HeightTwips = 600, Objects = [
            new TextObject { Name = "Caption", Text = "End", Bounds = new(0, 0, 1440, 240) },
            new FieldObject { Name = "LastLine", FieldName = "T.Line", Bounds = new(1440, 0, 2880, 240) }] };

        Assert.That(KeepTogether(Band(Ledger(footer), "End")), Is.Null);
    }
}
