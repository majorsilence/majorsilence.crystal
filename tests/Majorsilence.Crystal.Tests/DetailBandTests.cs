using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Reporting.Rdl;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A report with several Details sections prints one line per band under every record,
/// each object where its band draws it (#7). The table gets one Details row per band over a
/// column grid made of every band's Lefts, an object's cell spanning to where the next
/// object in its own band starts.
///
/// Also the formula behind each band's suppression, which this depends on: every section's
/// suppress formula is called Section_Visibility, and a section names its own by position.
/// </summary>
[TestFixture]
public class DetailBandTests
{
    private static string CorpusPath(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static readonly List<ReportField> Fields =
    [
        new DatabaseField { Name = "T.Code", TableName = "T", ColumnName = "Code", DataType = "String" },
        new DatabaseField { Name = "T.Name", TableName = "T", ColumnName = "Name", DataType = "String" },
        new DatabaseField { Name = "T.Note", TableName = "T", ColumnName = "Note", DataType = "String" },
        new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
    ];

    private static FieldObject Field(string name, int left, int width, int top = 20) =>
        new() { FieldName = name, Bounds = new(left, top, width, 240) };

    // Two bands sharing their first Left, the second's Note running across two of the
    // first band's columns.
    private static ReportDefinition TwoBands(Section? third = null)
    {
        var sections = new List<Section>
        {
            new() { Type = SectionType.Details, HeightTwips = 300, Objects =
                [Field("Code", 1000, 1200), Field("Name", 2500, 2000), Field("Amount", 5000, 1000)] },
            new() { Type = SectionType.Details, HeightTwips = 330, Objects =
                [Field("Code", 1000, 1200, top: 40), Field("Note", 2000, 3800)] },
        };
        if (third is not null) sections.Add(third);
        return new ReportDefinition { ReportTitle = "Bands", Fields = Fields, Sections = sections };
    }

    private static (XDocument Doc, XNamespace Ns) Convert(ReportDefinition report)
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        return (doc, doc.Root!.Name.Namespace);
    }

    private static List<XElement> DetailRows(XDocument doc, XNamespace ns) =>
        doc.Descendants(ns + "Details").Single().Element(ns + "TableRows")!.Elements(ns + "TableRow").ToList();

    private static int ColSpan(XElement cell, XNamespace ns) =>
        int.Parse(cell.Element(ns + "ColSpan")?.Value ?? "1");

    private static string? CellValue(XElement cell, XNamespace ns) =>
        cell.Descendants(ns + "Textbox").FirstOrDefault()?.Element(ns + "Value")?.Value;

    [Test]
    public void SeveralDetailsSections_AreOneRowEach_OverTheUnionOfTheirLefts()
    {
        var (doc, ns) = Convert(TwoBands());

        var widths = doc.Descendants(ns + "TableColumn").Select(c => c.Element(ns + "Width")!.Value).ToList();
        var rows = DetailRows(doc, ns);
        var first = rows[0].Descendants(ns + "TableCell").ToList();
        var second = rows[1].Descendants(ns + "TableCell").ToList();

        Assert.Multiple(() =>
        {
            // Lefts 1000, 2000, 2500, 5000: four columns, each as wide as the gap to the
            // next, the last to the furthest right edge (Amount's, at 6000).
            Assert.That(widths, Is.EqualTo(new[] { "50pt", "25pt", "125pt", "50pt" }));
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(r => r.Element(ns + "Height")!.Value), Is.EqualTo(new[] { "15pt", "16.5pt" }));

            // Band one: Code spans to Name's start, past the column that only Note begins.
            Assert.That(first.Select(c => ColSpan(c, ns)), Is.EqualTo(new[] { 2, 1, 1 }));
            Assert.That(first.Select(c => CellValue(c, ns)),
                Is.EqualTo(new[] { "=Fields!Code.Value", "=Fields!Name.Value", "=Fields!Amount.Value" }));

            // Band two: Note from its own column to the end.
            Assert.That(second.Select(c => ColSpan(c, ns)), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(second.Select(c => CellValue(c, ns)),
                Is.EqualTo(new[] { "=Fields!Code.Value", "=Fields!Note.Value" }));
        });
    }

    [Test]
    public void ABandsObject_IsInsetToItsOwnBoxWithinItsSpan()
    {
        var (doc, ns) = Convert(TwoBands());

        // Note: 3800 twips wide in a span of 4000 (2000 to the grid's right edge at 6000),
        // 40 down a 330 band.
        var note = DetailRows(doc, ns)[1].Descendants(ns + "TableCell").Last();
        var style = note.Descendants(ns + "Textbox").Single().Element(ns + "Style")!;
        // Code in band one: 1200 wide in a span of 1500, 20 down a 300 band.
        var code = DetailRows(doc, ns)[0].Descendants(ns + "TableCell").First();
        var codeStyle = code.Descendants(ns + "Textbox").Single().Element(ns + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That(style.Element(ns + "PaddingRight")?.Value, Is.EqualTo("10pt"));
            Assert.That(style.Element(ns + "PaddingTop")?.Value, Is.EqualTo("1pt"));
            Assert.That(codeStyle.Element(ns + "PaddingRight")?.Value, Is.EqualTo("15pt"));
            Assert.That(codeStyle.Element(ns + "PaddingTop")?.Value, Is.EqualTo("1pt"));
            Assert.That(codeStyle.Element(ns + "PaddingBottom")?.Value, Is.EqualTo("2pt"));
        });
    }

    [Test]
    public void EachBand_IsSuppressedOnItsOwnTerms()
    {
        var report = TwoBands();
        report.Sections[1] = new Section
        {
            Type = SectionType.Details, HeightTwips = 330, Suppress = true,
            SuppressFormula = "{T.Note} = ''",
            Objects = [Field("Note", 2000, 3800)]
        };
        var (doc, ns) = Convert(report);
        var rows = DetailRows(doc, ns);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Element(ns + "Visibility"), Is.Null,
                "the first band has no suppression of its own and must print");
            Assert.That(rows[1].Element(ns + "Visibility")?.Element(ns + "Hidden")?.Value,
                Does.Contain("Fields!Note.Value"));
        });
    }

    // A band suppressed outright used to hide the whole flattened row, every band with it.
    [Test]
    public void ABandSuppressedOutright_IsLeftOut_AndHidesNothingElse()
    {
        var hidden = new Section
        {
            Type = SectionType.Details, HeightTwips = 300, Suppress = true,
            Objects = [Field("Code", 9000, 900)]
        };
        var (doc, ns) = Convert(TwoBands(hidden));
        var rows = DetailRows(doc, ns);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(r => r.Element(ns + "Visibility")), Has.All.Null);
            // Nor does its object add a column.
            Assert.That(doc.Descendants(ns + "TableColumn").Count(), Is.EqualTo(4));
        });
    }

    // A text, a line or two objects sharing a Left cannot be cells of the grid; such a band
    // is one cell across the table holding each object where it is drawn.
    [Test]
    public void ABandTheGridCannotHold_IsWrittenFreeForm()
    {
        var labelled = new Section
        {
            Type = SectionType.Details, HeightTwips = 300,
            Objects = [new TextObject { Text = "Total:", Bounds = new(1000, 0, 900, 240) }, Field("Amount", 5000, 1000, top: 0)]
        };
        var (doc, ns) = Convert(TwoBands(labelled));
        var rows = DetailRows(doc, ns);
        var cell = rows[2].Descendants(ns + "TableCell").Single();
        var items = cell.Element(ns + "ReportItems")!.Element(ns + "Rectangle")!
            .Element(ns + "ReportItems")!.Elements(ns + "Textbox").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(ColSpan(cell, ns), Is.EqualTo(4));
            Assert.That(items.Select(t => t.Element(ns + "Value")!.Value),
                Is.EqualTo(new[] { "Total:", "=Fields!Amount.Value" }));
            // Positioned against the table's left edge, which is the grid's first Left.
            Assert.That(items.Select(t => t.Element(ns + "Left")!.Value), Is.EqualTo(new[] { "0pt", "200pt" }));
        });
    }

    [Test]
    public void OneDetailsSection_KeepsItsSingleRow()
    {
        var report = TwoBands();
        report.Sections.RemoveAt(1);
        var (doc, ns) = Convert(report);

        Assert.That(DetailRows(doc, ns), Has.Count.EqualTo(1));
        Assert.That(DetailRows(doc, ns)[0].Descendants(ns + "TableCell").Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task SeveralDetailsSections_LoadInTheEngine()
    {
        var hidden = new Section { Type = SectionType.Details, HeightTwips = 300, Suppress = true };
        var labelled = new Section
        {
            Type = SectionType.Details, HeightTwips = 300,
            Objects = [new TextObject { Text = "Total:", Bounds = new(1000, 0, 900, 240) }]
        };
        var report = TwoBands(labelled);
        report.Sections.Add(hidden);
        report.Sections.Add(new Section { Type = SectionType.Details, HeightTwips = 200 });

        var parser = new RDLParser(new RdlConverter().Convert(report)) { SkipDatabaseSchemaValidation = true };
        Report engineReport = await parser.Parse();

        Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(4),
            string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
    }

    // boyum__ServiceContract: four Section_Visibility hooks - two page header label bands
    // and two Details bands - over two "{Lines.ItemCode}=''" formulas and two
    // "{Lines.ItmsGrpNam}=''". By name alone every band got the last one read.
    [Test]
    public void EachSectionsFormula_IsTheOneItsHookPointsAt()
    {
        string path = CorpusPath("boyum__ServiceContract.rpt");
        Assume.That(File.Exists(path), Is.True, "boyum__ServiceContract.rpt not found - run scripts/download-test-rpts.sh");

        var report = RptParser.Parse(path).Report!;
        var details = report.Sections.Where(s => s.Type == SectionType.Details).ToList();

        Assert.That(details.Select(s => s.SuppressFormula),
            Is.EqualTo(new[] { "{Lines.ItemCode}=''", "{Lines.ItmsGrpNam}=''" }));
    }

    [Test]
    public void ServiceContract_PrintsItsTwoDetailsBandsAsTwoRows()
    {
        string path = CorpusPath("boyum__ServiceContract.rpt");
        Assume.That(File.Exists(path), Is.True, "boyum__ServiceContract.rpt not found - run scripts/download-test-rpts.sh");

        var (doc, ns) = Convert(RptParser.Parse(path).Report!);
        var rows = DetailRows(doc, ns);
        var itemGroupRow = rows[1].Descendants(ns + "TableCell").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            // Seven columns between them: the item band's seven Lefts, of which the
            // item-group band's four are a subset.
            Assert.That(doc.Descendants(ns + "Details").Single().Parent!.Element(ns + "TableColumns")!
                .Elements(ns + "TableColumn").Count(), Is.EqualTo(7));
            // Item group, start date, then end date across the serial-number column, and
            // the termination date to the end.
            Assert.That(itemGroupRow.Select(c => ColSpan(c, ns)), Is.EqualTo(new[] { 1, 1, 2, 3 }));
        });
    }
}
