using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Lines and boxes drawn as Crystal draws them, a single Details band that columns cannot
/// hold written free-form, and a running total in its own number format. The box and line
/// values are the Crystal runtime's own (BoxObject / LineObject properties for each file).
/// </summary>
[TestFixture]
public class ShapeAndBandTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

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

    private static IEnumerable<ReportObject> Objects(ReportDefinition report) => report.Sections.SelectMany(s => s.Objects);

    [Test]
    public void ABox_KeepsItsFill_LineColourAndWidth()
    {
        // The runtime: FillColor #E0E0E0, LineColor #808080, LineThickness 30, SingleLine.
        var box = Objects(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))).OfType<BoxObject>().Single();

        Assert.That(box.Format.BackColor, Is.EqualTo("#E0E0E0"));
        Assert.That(box.Format.BorderColor, Is.EqualTo("#808080"));
        Assert.That(box.Format.BorderWidthTwips, Is.EqualTo(30));
        Assert.That(new[] { box.Format.BorderLeft, box.Format.BorderRight, box.Format.BorderTop, box.Format.BorderBottom },
            Is.All.EqualTo((byte)1));
    }

    [Test]
    public void ABox_IsDrawnFilled_InItsOwnColourAndWidth()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))));
        var style = doc.Descendants(Rdl + "Rectangle").Select(r => r.Element(Rdl + "Style"))
            .Single(s => (string?)s?.Element(Rdl + "BackgroundColor") == "#E0E0E0")!;

        Assert.That((string?)style.Element(Rdl + "BorderColor")?.Element(Rdl + "Default"), Is.EqualTo("#808080"));
        Assert.That((string?)style.Element(Rdl + "BorderWidth")?.Element(Rdl + "Default"), Is.EqualTo("1.5pt"));
    }

    [Test]
    public void ALine_IsDrawnFlat_AlongItsLongSide()
    {
        // Crystal draws lines only across or down the page; a line whose bounds are 2.95pt
        // tall was drawn as a diagonal.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__ChinaOrders-RunningTotals.rpt"))));
        var lines = doc.Descendants(Rdl + "Line").ToList();

        Assert.That(lines, Is.Not.Empty);
        Assert.That(lines.All(l => (string?)l.Element(Rdl + "Height") == "0pt" || (string?)l.Element(Rdl + "Width") == "0pt"), Is.True);
    }

    [Test]
    public void ASingleDetailsBandColumnsCannotHold_IsWrittenFreeForm_WithItsLabels()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))));
        var details = doc.Descendants(Rdl + "Details").Single();
        var values = details.Descendants(Rdl + "Value").Select(v => v.Value).ToList();

        // The card's labels are text objects, which the one-row column layout dropped.
        Assert.That(values, Does.Contain("COUNTRY"));
        Assert.That(details.Descendants(Rdl + "BackgroundColor").Any(c => c.Value == "#E0E0E0"), Is.True, "the card's box");
    }

    // The runtime's SQLExpressionFieldDefinition for each: name, value type and text.
    [TestCase("rpt-corpus/benbrahim777__USA-Orders-RWB.rpt", "%Amount", "Currency", "{Orders.Order Amount}")]
    [TestCase("rpt-corpus-external/meridian_probes__24_sql_expression_view.rpt", "%NetDays", "Float64",
        "({invoice.due_date} - {invoice.invoice_date})")]
    public void ASqlExpression_IsReadAsTheFormulaItAmountsTo(string file, string name, string type, string formula)
    {
        var report = Report(Path.GetFullPath($"../../../../{file}", AppContext.BaseDirectory));
        var field = report.Fields.OfType<Majorsilence.Crystal.Model.Fields.FormulaField>().Single(f => f.Name == name);

        Assert.That(field.ResultType, Is.EqualTo(type));
        Assert.That(field.FormulaText, Is.EqualTo(formula));
    }

    [Test]
    public void AGroupFooter_PrintsWhatIsPlacedInIt_NotAnInventedTotal()
    {
        // USA-Orders-RWB's state footer holds the group name and %Amount, nothing summing
        // Order Amount; a Sum was written under that column anyway.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__USA-Orders-RWB.rpt"))));
        var footer = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!;
        var values = footer.Descendants(Rdl + "Value").Select(v => v.Value).ToList();

        Assert.That(values, Does.Contain("=Fields!_Amount.Value"));
        Assert.That(values.Any(v => v.Contains("Sum(")), Is.False);
    }

    [Test]
    public void NewPageBeforeAGroup_BreaksBetweenGroups_NotBeforeTheFirst()
    {
        // Crystal prints the first state under the report's headers; a break at the start of
        // every group put it on page 2.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__USA-Orders-Pct-colored.rpt"))));
        var grouping = doc.Descendants(Rdl + "TableGroup").First().Element(Rdl + "Grouping")!;

        Assert.That(grouping.Element(Rdl + "PageBreakAtStart"), Is.Null);
        Assert.That((string?)grouping.Element(Rdl + "PageBreakAtEnd"), Is.EqualTo("true"));
    }

    [Test]
    public void ABandsColourFormula_ColoursItsRows()
    {
        // ServiceContract's item bands: "IF Recordnumber MOD 2 <> 0 THEN crSilver ELSE
        // crNocolor", over a fixed silver that painted every row.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("boyum__ServiceContract.rpt"))));
        var colours = doc.Descendants(Rdl + "Details").Descendants(Rdl + "BackgroundColor").Select(c => c.Value).ToList();

        Assert.That(colours, Is.Not.Empty);
        Assert.That(colours, Has.All.Contains("Silver").And.All.Contains("Transparent").And.All.Contains("Mod"));
    }

    [Test]
    public void DataDateAndDataTime_PrintTheirValues_NotTheirNames()
    {
        var rdl = new RdlConverter().Convert(Report(Corpus("boyum__ServiceContract.rpt")));

        Assert.That(rdl, Does.Not.Contain("[Data Date]"));
        Assert.That(rdl, Does.Not.Contain("[Data Time]"));
    }

    [Test]
    public void ARunningTotal_IsFormattedAsTheNumberItSums()
    {
        // ChinaOrders-RunningTotals' Order Total sums Order Amount, a currency column, and
        // Crystal prints it as $40.50 and $12,424.80.
        var total = Objects(Report(Corpus("benbrahim777__ChinaOrders-RunningTotals.rpt"))).OfType<FieldObject>()
            .Single(f => f.FieldName.TrimStart('#').Equals("Order Total", StringComparison.OrdinalIgnoreCase));

        Assert.That(total.Format.FormatString, Does.Contain("#,##0.00"));
    }
}
