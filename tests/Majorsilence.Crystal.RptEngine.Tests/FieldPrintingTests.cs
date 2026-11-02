using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// Two things Crystal's runtime does with a field's text, rendered: it prints a string's last
/// line break as no line at all, and it draws no border around a field whose text is empty.
/// </summary>
[TestFixture]
public class FieldPrintingTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition Listed(string field, ObjectFormat format) => new()
    {
        ReportTitle = "Fields",
        Fields = [
            new DatabaseField { Name = "Name", ColumnName = "Name", TableName = "T", DataType = "String" },
            new FormulaField { Name = "Plain", FormulaText = "{T.Name}", ResultType = "String" },
            new FormulaField { Name = "Ended", FormulaText = "{T.Name} + chr(10)", ResultType = "String" }],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "Shown", FieldName = field, Bounds = new(0, 0, 2880, 240), Format = format }] }]
    };

    private static async Task<List<(string Text, float Y, BorderStyleEnum Top)>> Printed(ReportDefinition report, params string[] names)
    {
        var data = new DataTable();
        data.Columns.Add("Name");
        foreach (var name in names)
            data.Rows.Add(name);
        string dir = Path.Combine(Path.GetTempPath(), "fieldprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await engineReport.BuildPages();
            return pages.Cast<Page>().First().Cast<PageItem>().OfType<PageText>().OrderBy(t => t.Y)
                .Select(t => (t.Text, t.Y, t.SI.BStyleTop)).ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The Crystal runtime exports "a" + chr(10) as one line and "a" + chr(10) + chr(10) as two,
    // the second empty. This engine starts a line after every line break, so a growing field
    // ending in one was a line taller than Crystal's, and pushed what follows down.
    [Test]
    public async Task AStringsLastLineBreak_PrintsNoLine()
    {
        var grows = new ObjectFormat { CanGrow = true };
        var plain = await Printed(Listed("@Plain", grows), "one", "two");
        var ended = await Printed(Listed("@Ended", grows), "one", "two");

        Assert.That(ended[1].Y - ended[0].Y, Is.EqualTo(plain[1].Y - plain[0].Y).Within(0.01));
    }

    // The runtime exports a field with a top border, its formula giving "", with no rule, and
    // with the rule for " " or "x". A bill's comment line, ruled above for when there is a
    // comment, drew its rule across every bill without one.
    [TestCase("x", BorderStyleEnum.Solid)]
    [TestCase("", BorderStyleEnum.None)]
    [TestCase(" ", BorderStyleEnum.Solid)]
    public async Task AnEmptyField_DrawsNoBorder(string value, BorderStyleEnum drawn)
    {
        var report = Listed("Name", new ObjectFormat { BorderTop = 1 });
        report.Sections[0] = new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = report.Sections[0].Objects };
        report.Sections.Add(new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = true });
        var printed = await Printed(report, value);

        Assert.That(printed.Single(t => t.Text == value).Top, Is.EqualTo(drawn));
    }
}
