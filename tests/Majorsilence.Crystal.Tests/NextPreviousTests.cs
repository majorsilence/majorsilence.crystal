using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Reporting.Rdl;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Next, Previous, OnFirstRecord and OnLastRecord in section formulas, as the Crystal runtime
/// evaluates them: Next and Previous are the records printed after and before the current one,
/// across groups, and null past either end; a group header's formulas see its first record and
/// a footer's its last; a formula that reads a null Next or Previous is null, which as a
/// suppress or new-page condition is false.
/// </summary>
[TestFixture]
public class NextPreviousTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static ReportDefinition Report(Section? details = null, Section? header = null, Section? footer = null) => new()
    {
        Fields = [new DatabaseField { Name = "Account", ColumnName = "Account", TableName = "T", DataType = "String" },
                  new DatabaseField { Name = "Source", ColumnName = "Source", TableName = "T", DataType = "String" }],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "T" }],
        Sections =
        [
            header ?? new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AccountField", FieldName = "T.Account", Bounds = new(0, 0, 2880, 240) }] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "SourceField", FieldName = "T.Source", Bounds = new(0, 0, 2880, 240) }] },
            details ?? new Section { Type = SectionType.Details, HeightTwips = 160 },
            footer ?? new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240, Objects = [
                new TextObject { Name = "Total", Text = "Total", Bounds = new(0, 0, 1440, 240) }] },
        ]
    };

    private static XDocument Convert(ReportDefinition report) => XDocument.Parse(new RdlConverter().Convert(report));

    private static XElement Grouping(XDocument doc) =>
        doc.Descendants(Rdl + "Grouping").First(g => (string?)g.Attribute("Name") == "Group1");

    private static string? CalculatedField(XDocument doc, string name) =>
        (string?)doc.Descendants(Rdl + "Field").FirstOrDefault(f => (string?)f.Attribute("Name") == name)?.Element(Rdl + "Value");

    private static async Task<List<string>> EngineErrors(ReportDefinition report)
    {
        var engineReport = await new RDLParser(new RdlConverter().Convert(report)) { SkipDatabaseSchemaValidation = true }.Parse();
        return (engineReport.ErrorItems?.Cast<string>() ?? [])
            .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m, @"^[^:\r\n]{1,40}:\s*[0-3]\s*-\s"))
            .ToList();
    }

    // "Suppress all but an account's last line": Next is the record the table prints next,
    // across groups, and on the last record, where it is null, the section prints.
    [Test]
    public void ASuppressFormulasNext_IsTheTablesNextRecord_AndFalseWhereThereIsNone()
    {
        var doc = Convert(Report(details: new Section { Type = SectionType.Details, HeightTwips = 160,
            SuppressFormula = "{T.Source} = Next ({T.Source})" }));
        var hidden = doc.Descendants(Rdl + "Details").Single().Descendants(Rdl + "TableRow").ElementAt(1)
            .Element(Rdl + "Visibility")!.Element(Rdl + "Hidden")!.Value;

        Assert.That(hidden, Is.EqualTo("=IIf(IsNothing(Next(Fields!Source.Value, \"Table1\")), False, "
            + "(Fields!Source.Value = Next(Fields!Source.Value, \"Table1\")))"));
    }

    // Crystal skips the right side of an Or once the left decides, so a Previous read there
    // does not make the formula null on the first record.
    [Test]
    public void ANextOrPreviousReadOnlyAfterAnOr_DoesNotGateTheFormula()
    {
        var doc = Convert(Report(header: new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
            SuppressFormula = "OnFirstRecord or {T.Account} = Previous ({T.Account})" }));
        string hidden = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "Hidden").First().Value;

        Assert.That(hidden, Is.EqualTo("=((RowNumber(Nothing) = 1) Or (Fields!Account.Value = Previous(Fields!Account.Value, \"Table1\")))"));
    }

    // A footer's "{x} <> Next({x})" breaks after every group but the last. Read on the group's
    // last record, it is the next group's break before, read on the record before that group's
    // first: the field there is Previous, its Next the field. The engine takes no aggregate in
    // a page break's condition, so a calculated field holds it.
    [Test]
    public async Task AFootersNewPageAfterReadingNext_IsTheNextGroupsBreakBefore()
    {
        var report = Report(footer: new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240,
            NewPageAfterFormula = "{T.Account} <> Next ({T.Account})" });
        var doc = Convert(report);
        var grouping = Grouping(doc);

        var errors = await EngineErrors(report);
        Assert.Multiple(() =>
        {
            Assert.That((string?)grouping.Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
            Assert.That(grouping.Element(Rdl + "PageBreakAtEnd"), Is.Null);
            Assert.That((string?)grouping.Element(Rdl + "PageBreakCondition"), Is.EqualTo("=Fields!CrystalNewPageGroup1.Value"));
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup1"), Is.EqualTo("=RowNumber(Nothing) > 1 And (IIf(IsNothing((Fields!Account.Value)), False, "
                + "(Previous(Fields!Account.Value, \"Table1\") <> (Fields!Account.Value))))"));
            Assert.That(errors, Is.Empty, string.Join(" | ", errors));
        });
    }

    // "Not OnLastRecord" on a footer: no page after the last group, where the report footer
    // follows on the same page.
    [Test]
    public async Task NotOnLastRecordOnAFooter_BreaksAfterEveryGroupButTheLast()
    {
        var report = Report(footer: new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240,
            NewPageAfterFormula = "not OnLastRecord" });
        var doc = Convert(report);

        var errors = await EngineErrors(report);
        Assert.Multiple(() =>
        {
            Assert.That((string?)Grouping(doc).Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup1"),
                Is.EqualTo("=RowNumber(Nothing) > 1 And (Not (((RowNumber(Nothing) - 1) = CountRows(Nothing))))"));
            Assert.That(errors, Is.Empty, string.Join(" | ", errors));
        });
    }

    // Within an outer group, the break after an inner group comes before the outer footer
    // and header that follow it, so it stays a break after, read on the group's last record.
    [Test]
    public void AnInnerGroupsFooterFormula_IsReadOnItsLastRecord()
    {
        var report = Report(footer: new Section { Type = SectionType.GroupFooter, GroupLevel = 1, HeightTwips = 240,
            NewPageAfterFormula = "{T.Source} <> Next ({T.Source})" });
        report.Groups.Add(new GroupDefinition { Level = 1, FieldName = "Source", TableName = "T" });
        var doc = Convert(report);
        var inner = doc.Descendants(Rdl + "Grouping").First(g => (string?)g.Attribute("Name") == "Group2");

        Assert.Multiple(() =>
        {
            Assert.That((string?)inner.Element(Rdl + "PageBreakAtEnd"), Is.EqualTo("true"));
            Assert.That((string?)inner.Element(Rdl + "PageBreakCondition"), Is.EqualTo("=Fields!CrystalNewPageGroup2.Value"));
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup2"), Is.EqualTo("=IIf(IsNothing(Next(Fields!Source.Value, \"Table1\")), False, "
                + "(Fields!Source.Value <> Next(Fields!Source.Value, \"Table1\")))"));
        });
    }

    // "Not OnFirstRecord" before a header is a break before every group but the first, and
    // stays a break before.
    [Test]
    public void NotOnFirstRecordBeforeAHeader_BreaksBeforeEveryGroupButTheFirst()
    {
        var report = Report(header: new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
            NewPageBeforeFormula = "not OnFirstRecord", Objects = [
                new FieldObject { Name = "AccountField", FieldName = "T.Account", Bounds = new(0, 0, 2880, 240) }] });
        var doc = Convert(report);
        var grouping = Grouping(doc);

        Assert.Multiple(() =>
        {
            Assert.That((string?)grouping.Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
            Assert.That(grouping.Element(Rdl + "PageBreakAtEnd"), Is.Null);
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup1"), Is.EqualTo("=Not ((RowNumber(Nothing) = 1))"));
        });
    }

    // New Page After on a header that prints nothing breaks before each group but the first,
    // read on the group's first record, so none follows the last group.
    [Test]
    public async Task NotOnLastRecordOnAnEmptyHeader_IsReadOnTheGroupsFirstRecord()
    {
        var report = Report(header: new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 0,
            Suppress = true, NewPageAfterFormula = "NOT OnLastRecord" });
        var doc = Convert(report);
        var grouping = Grouping(doc);

        var errors = await EngineErrors(report);
        Assert.Multiple(() =>
        {
            Assert.That((string?)grouping.Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
            Assert.That(grouping.Element(Rdl + "PageBreakAtEnd"), Is.Null);
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup1"),
                Is.EqualTo("=RowNumber(Nothing) > 1 And (Not ((RowNumber(Nothing) = CountRows(Nothing))))"));
            Assert.That(errors, Is.Empty, string.Join(" | ", errors));
        });
    }

    // A break with no record functions keeps its condition on the grouping, with no field.
    [Test]
    public void ABreakWithoutRecordFunctions_WritesNoField()
    {
        var doc = Convert(Report(footer: new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240,
            NewPageAfterFormula = "{T.Account} = 'X'" }));

        Assert.Multiple(() =>
        {
            Assert.That((string?)Grouping(doc).Element(Rdl + "PageBreakCondition"), Is.EqualTo("=(RTrim(Fields!Account.Value) = \"X\")"));
            Assert.That(CalculatedField(doc, "CrystalNewPageGroup1"), Is.Null);
        });
    }

    // A Next written in a string stays text.
    [Test]
    public void NextInsideAString_IsNotScoped()
    {
        Assert.That(RdlConverter.ScopeRecordFunctions("\"Next(\" & Next(Fields!A.Value) & Previous(Fields!B.Value, \"Group1\")", "Table1"),
            Is.EqualTo("\"Next(\" & Next(Fields!A.Value, \"Table1\") & Previous(Fields!B.Value, \"Group1\")"));
    }
}
