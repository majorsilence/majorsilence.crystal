using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A cross-tab's grid as the file holds it - its settings, cell boxes and formats - and the
/// Matrix written from it. The parking__crosstab_* reports in the third-party corpus are one
/// base report and one copy per Customize Style setting, so each setting is checked against
/// the copy that changes it and nothing else.
/// </summary>
[TestFixture]
public class CrossTabTests
{
    private static string Public(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Report(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found - run scripts/download-test-rpts.sh");
        return RptParser.Parse(path).Report!;
    }

    private static CrossTabObject CrossTab(string path) =>
        Report(path).Sections.SelectMany(s => s.Objects).OfType<CrossTabObject>().Single();

    private static XElement Matrix(ReportDefinition report, out XNamespace ns)
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "Matrix").Single();
    }

    // Crystal's export of Canada-CrossTab draws the column headers 21.05pt tall, the row
    // headers 83.9pt wide, the data columns 66.4pt apart and the rows 21.5pt, the values 5pt
    // inside the grid lines, with the grid on and both grand totals printed after the data.
    [Test]
    public void CanadaCrossTab_GridIsRead()
    {
        var ct = CrossTab(Public("benbrahim777__Canada-CrossTab.rpt"));
        var data = ct.CellLayouts.Single(c => c.RowLevel == 1 && c.ColumnLevel == 1);
        var rowTotal = ct.CellLayouts.Single(c => c.RowLevel == 0 && c.ColumnLevel == 1);
        var columnTotal = ct.CellLayouts.Single(c => c.RowLevel == 1 && c.ColumnLevel == 0);

        Assert.Multiple(() =>
        {
            Assert.That(ct.ShowGrid, Is.True);
            Assert.That(ct.SuppressRowGrandTotals, Is.False);
            Assert.That(ct.SuppressColumnGrandTotals, Is.False);
            Assert.That(ct.ColumnHeaderHeightTwips, Is.EqualTo(421));
            Assert.That(ct.RowHeaderWidthTwips, Is.EqualTo(1678));
            Assert.That(ct.CellLayouts, Has.Count.EqualTo(4));
            Assert.That(data.Box, Is.EqualTo(new ObjectBounds(1678, 5308, 1328, 430)));
            Assert.That((data.MarginLeftTwips, data.MarginTopTwips), Is.EqualTo((100, 100)));
            Assert.That(rowTotal.Box.Top, Is.GreaterThan(data.Box.Top), "the Total row prints below");
            Assert.That(columnTotal.Box.Left, Is.GreaterThan(data.Box.Left), "the Total column prints right");
            Assert.That((ct.Bounds.Width, ct.Bounds.Height), Is.EqualTo((4334, 1281)));
        });
    }

    // The export prints the values bold, in their currency format, flush right; the group
    // values and the totals' labels regular, flush left.
    [Test]
    public void CanadaCrossTab_CellFormatsAreRead()
    {
        var ct = CrossTab(Public("benbrahim777__Canada-CrossTab.rpt"));
        var value = ct.CellLayouts.Single(c => c.RowLevel == 1 && c.ColumnLevel == 1).SummaryFormats.Single();
        var rowHeader = ct.RowLevels.Single(l => l.Level == 1).HeaderFormat!;
        var columnHeader = ct.ColumnLevels.Single(l => l.Level == 1).HeaderFormat!;
        var total = ct.RowLevels.Single(l => l.Level == 0);

        Assert.Multiple(() =>
        {
            Assert.That((value.FontName, value.FontSize, value.Bold), Is.EqualTo(("Arial", (double?)10, true)));
            // No trailing space after the number, unlike a placed field with its own
            // currency format: the export ends each value at the cell margin.
            Assert.That(value.FormatString, Is.EqualTo("\"$\"#,##0.00"));
            Assert.That(value.HAlign, Is.EqualTo(HorizontalAlignment.Right));
            Assert.That((rowHeader.Bold, rowHeader.HAlign, rowHeader.FormatString), Is.EqualTo((false, HorizontalAlignment.Default, (string?)null)));
            // Neither header sets an alignment: Crystal's default, text to the left.
            Assert.That((columnHeader.Bold, columnHeader.HAlign), Is.EqualTo((false, HorizontalAlignment.Default)));
            Assert.That(total.TotalLabel, Is.EqualTo("Total"));
            Assert.That(total.TotalLabelFormat!.Bold, Is.False);
            Assert.That(total.TotalBackColor, Is.Null);
        });
    }

    // Each copy differs from the base in the one setting its name gives.
    [TestCase("parking__crosstab_base.rpt", true, false, false)]
    [TestCase("parking__crosstab_show_grid_off.rpt", false, false, false)]
    [TestCase("parking__crosstab_suppress_row_grandtotal_on.rpt", true, true, false)]
    [TestCase("parking__crosstab_suppress_col_grandtotal_on.rpt", true, false, true)]
    [TestCase("parking__crosstab_suppress_empty_rows_on.rpt", true, false, false)]
    [TestCase("parking__crosstab_repeat_row_labels_on.rpt", true, false, false)]
    public void ParkingCrossTab_StyleSettingsAreRead(string file, bool grid, bool suppressRowTotals, bool suppressColumnTotals)
    {
        var ct = CrossTab(External(file));
        Assert.That((ct.ShowGrid, ct.SuppressRowGrandTotals, ct.SuppressColumnGrandTotals),
            Is.EqualTo((grid, suppressRowTotals, suppressColumnTotals)));
    }

    // With the cell margins off every cell and both header bands lose the 200 twips of
    // margin, and the margins read zero.
    [Test]
    public void ParkingCrossTab_CellMarginsOff_ShrinksTheGrid()
    {
        var on = CrossTab(External("parking__crosstab_base.rpt"));
        var off = CrossTab(External("parking__crosstab_cell_margins_off.rpt"));
        var onData = on.CellLayouts.Single(c => c.RowLevel == 1 && c.ColumnLevel == 1);
        var offData = off.CellLayouts.Single(c => c.RowLevel == 1 && c.ColumnLevel == 1);

        Assert.Multiple(() =>
        {
            Assert.That((offData.MarginLeftTwips, offData.MarginTopTwips), Is.EqualTo((0, 0)));
            Assert.That(onData.Box.Width - offData.Box.Width, Is.EqualTo(200));
            Assert.That(onData.Box.Height - offData.Box.Height, Is.EqualTo(200));
            Assert.That(on.ColumnHeaderHeightTwips - off.ColumnHeaderHeightTwips, Is.EqualTo(200));
            Assert.That(on.RowHeaderWidthTwips - off.RowHeaderWidthTwips, Is.EqualTo(200));
        });
    }

    // The export fills the Total row yellow in one copy and the Total column in the other;
    // the colour is on that axis's level 0.
    [TestCase("parking__crosstab_row_grandtotal_bgcolor.rpt", "#FFFF00", null)]
    [TestCase("parking__crosstab_col_grandtotal_bgcolor.rpt", null, "#FFFF00")]
    [TestCase("parking__crosstab_base.rpt", null, null)]
    public void ParkingCrossTab_GrandTotalColoursAreRead(string file, string? rowColour, string? columnColour)
    {
        var ct = CrossTab(External(file));
        Assert.That((ct.RowLevels.Single(l => l.Level == 0).TotalBackColor, ct.ColumnLevels.Single(l => l.Level == 0).TotalBackColor),
            Is.EqualTo((rowColour, columnColour)));
    }

    // The Matrix takes the grid's sizes from the file, and the grand totals are a Subtotal
    // on each axis's outermost grouping.
    [Test]
    public void CanadaCrossTab_MatrixIsSizedAndTotalledAsTheGrid()
    {
        var matrix = Matrix(Report(Public("benbrahim777__Canada-CrossTab.rpt")), out var ns);
        var column = matrix.Descendants(ns + "ColumnGrouping").Single();
        var row = matrix.Descendants(ns + "RowGrouping").Single();
        var cellBox = matrix.Descendants(ns + "MatrixCell").Single().Descendants(ns + "Textbox").Single();
        var style = cellBox.Element(ns + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That(column.Element(ns + "Height")!.Value, Is.EqualTo("21.05pt"));
            Assert.That(row.Element(ns + "Width")!.Value, Is.EqualTo("83.9pt"));
            Assert.That(matrix.Descendants(ns + "MatrixColumn").Single().Element(ns + "Width")!.Value, Is.EqualTo("66.4pt"));
            Assert.That(matrix.Descendants(ns + "MatrixRow").Single().Element(ns + "Height")!.Value, Is.EqualTo("21.5pt"));
            Assert.That(column.Descendants(ns + "Subtotal").Single().Descendants(ns + "Value").Single().Value, Is.EqualTo("Total"));
            Assert.That(row.Descendants(ns + "Subtotal").Single().Descendants(ns + "Value").Single().Value, Is.EqualTo("Total"));
            Assert.That(matrix.Descendants(ns + "Position"), Is.Empty, "both totals print after the data");
            Assert.That(cellBox.Element(ns + "Value")!.Value,
                Is.EqualTo("=IIf(Count(Fields!Order_Amount.Value) > 0, Sum(Fields!Order_Amount.Value), 0)"));
            Assert.That(style.Element(ns + "FontWeight")?.Value, Is.EqualTo("Bold"));
            Assert.That(style.Element(ns + "Format")?.Value, Is.EqualTo("\"$\"#,##0.00"));
            Assert.That(style.Element(ns + "TextAlign")?.Value, Is.EqualTo("Right"));
            Assert.That(style.Element(ns + "PaddingLeft")?.Value, Is.EqualTo("5pt"));
            Assert.That(style.Element(ns + "PaddingRight")?.Value, Is.EqualTo("5pt"));
            Assert.That(style.Element(ns + "PaddingTop")?.Value, Is.EqualTo("5pt"));
            Assert.That(style.Element(ns + "BorderStyle")!.Elements().Select(e => e.Value), Is.All.EqualTo("Solid"));
            Assert.That(style.Element(ns + "BorderStyle")!.Elements().Count(), Is.EqualTo(4));
            Assert.That(style.Element(ns + "BorderWidth")!.Value, Is.EqualTo("0.25pt"));
            Assert.That(row.Descendants(ns + "Textbox").Last().Element(ns + "Style")!.Element(ns + "FontWeight"), Is.Null,
                "the row headers are regular");
        });
    }

    // Grand totals printed before the data are written Before; a suppressed one is not
    // written; with the grid off no cell has lines.
    [Test]
    public void ParkingCrossTab_TotalsFirst_SuppressedTotals_AndNoGrid()
    {
        var first = Matrix(Report(External("parking__crosstab_base.rpt")), out var ns);
        var noRowTotals = Matrix(Report(External("parking__crosstab_suppress_row_grandtotal_on.rpt")), out _);
        var noColumnTotals = Matrix(Report(External("parking__crosstab_suppress_col_grandtotal_on.rpt")), out _);
        var noGrid = Matrix(Report(External("parking__crosstab_show_grid_off.rpt")), out _);

        Assert.Multiple(() =>
        {
            Assert.That(first.Descendants(ns + "Subtotal").Select(s => s.Element(ns + "Position")?.Value),
                Is.EqualTo(new[] { "Before", "Before" }));
            Assert.That(noRowTotals.Descendants(ns + "ColumnGrouping").Descendants(ns + "Subtotal"), Is.Empty,
                "suppressing the row grand totals drops the Total column");
            Assert.That(noRowTotals.Descendants(ns + "RowGrouping").Descendants(ns + "Subtotal"), Is.Not.Empty);
            Assert.That(noColumnTotals.Descendants(ns + "RowGrouping").Descendants(ns + "Subtotal"), Is.Empty,
                "suppressing the column grand totals drops the Total row");
            Assert.That(noColumnTotals.Descendants(ns + "ColumnGrouping").Descendants(ns + "Subtotal"), Is.Not.Empty);
            Assert.That(noGrid.Descendants(ns + "BorderStyle"), Is.Empty);
            Assert.That(first.Descendants(ns + "BorderStyle"), Is.Not.Empty);
        });
    }

    // A grand-total colour is on the Total row's label, and on each value cell as an
    // expression that picks it only outside the row groups.
    [Test]
    public void ParkingCrossTab_RowGrandTotalColour_IsWritten()
    {
        var matrix = Matrix(Report(External("parking__crosstab_row_grandtotal_bgcolor.rpt")), out var ns);
        string label = matrix.Descendants(ns + "RowGrouping").Descendants(ns + "Subtotal").Descendants(ns + "BackgroundColor").Single().Value;
        string cell = matrix.Descendants(ns + "MatrixCell").Descendants(ns + "BackgroundColor").Single().Value;

        Assert.Multiple(() =>
        {
            Assert.That(label, Is.EqualTo("#FFFF00"));
            Assert.That(cell, Is.EqualTo("=IIf(InScope(\"MatrixRow_partner_id\"), \"Transparent\", \"#FFFF00\")"));
        });
    }

    // A cross-tab the file holds no grid for keeps the sizes this converter always wrote,
    // and has no lines.
    [Test]
    public void CrossTabWithoutLayout_KeepsTheDefaultSizes()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pivot",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Product", ColumnName = "Product", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections = [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [
                    new CrossTabObject
                    {
                        Name = "CrossTab1",
                        Bounds = new(0, 0, 5760, 1440),
                        RowGroupFields = ["Region"],
                        ColumnGroupFields = ["Product"],
                        Cells = [new CrossTabCell("Amount", AggregateFunction.Sum)]
                    }] }]
        };

        var matrix = Matrix(report, out var ns);
        Assert.Multiple(() =>
        {
            Assert.That(matrix.Descendants(ns + "ColumnGrouping").Single().Element(ns + "Height")!.Value, Is.EqualTo("14pt"));
            Assert.That(matrix.Descendants(ns + "RowGrouping").Single().Element(ns + "Width")!.Value, Is.EqualTo("72pt"));
            Assert.That(matrix.Descendants(ns + "MatrixColumn").Single().Element(ns + "Width")!.Value, Is.EqualTo("72pt"));
            Assert.That(matrix.Descendants(ns + "MatrixRow").Single().Element(ns + "Height")!.Value, Is.EqualTo("14pt"));
            Assert.That(matrix.Descendants(ns + "BorderStyle"), Is.Empty);
        });
    }

    // An axis on a formula and a summary of one are named by the formula alone, with no
    // table: ConsolidatedBalanceSheet's quarters and four totals, B1Budget's rows and
    // "@Difference". Each was dropped, and with the rows went the whole grid.
    [Test]
    public void FormulaAxesAndSummaries_AreRead()
    {
        var balance = Report(Public("souvikduttachoudhury__ConsolidatedBalanceSheet.rpt"))
            .Sections.SelectMany(s => s.Objects).OfType<CrossTabObject>().First();
        var budget = CrossTab(Public("boyum__B1Budget_Q.rpt"));
        var data = balance.CellLayouts.Single(c => c.RowLevel == 0 && c.ColumnLevel == 1);

        Assert.Multiple(() =>
        {
            Assert.That(balance.RowGroupFields, Is.Empty);
            Assert.That(balance.ColumnGroupFields, Is.EqualTo(new[] { "@Quarter" }));
            Assert.That(balance.Cells, Has.Count.EqualTo(13));
            Assert.That(balance.Cells[4], Is.EqualTo(new CrossTabCell("@total current assets", AggregateFunction.Sum)));
            Assert.That(data.SummaryFormats.Select(f => f.FormatString), Has.All.EqualTo("#,##0.00"),
                "the formula totals' amounts are formatted as the columns' are");
            Assert.That(budget.RowGroupFields, Is.EqualTo(new[] { "@DataEntryCodeAndName" }));
            Assert.That(budget.Cells.Select(c => c.FieldName), Does.Contain("@Difference"));
        });
    }

    // ConsolidatedBalanceSheet stacks thirteen summaries in a cell one column wide and 4926
    // twips tall, with no row groups: thirteen rows of one column under each quarter. Its
    // Total column is 270 twips wide and prints nothing, so it is not written.
    [Test]
    public void StackedSummaries_AreRowsOfTheirOwn()
    {
        var report = Report(Public("souvikduttachoudhury__ConsolidatedBalanceSheet.rpt"));
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var matrix = doc.Descendants(ns + "Matrix").First();
        var rowGrouping = matrix.Descendants(ns + "RowGrouping").Single();

        Assert.Multiple(() =>
        {
            Assert.That(rowGrouping.Descendants(ns + "StaticRow").Count(), Is.EqualTo(13));
            Assert.That(rowGrouping.Element(ns + "Width")!.Value, Is.EqualTo("21.25pt"), "the 425-twip row header");
            Assert.That(matrix.Descendants(ns + "MatrixRow").Count(), Is.EqualTo(13));
            Assert.That(matrix.Descendants(ns + "MatrixRow").First().Element(ns + "Height")!.Value, Is.EqualTo("18.9pt"));
            Assert.That(matrix.Descendants(ns + "MatrixColumn").Single().Element(ns + "Width")!.Value, Is.EqualTo("78.75pt"));
            Assert.That(matrix.Descendants(ns + "StaticColumns"), Is.Empty);
            Assert.That(matrix.Descendants(ns + "Subtotal"), Is.Empty);
        });
    }

    // A grouping name is the report's to use once. B1Budget_M groups its columns by its date
    // twice, and ConsolidatedBalanceSheet's two cross-tabs both by quarter.
    [TestCase("boyum__B1Budget_M.rpt")]
    [TestCase("souvikduttachoudhury__ConsolidatedBalanceSheet.rpt")]
    public void MatrixGroupingNames_AreUniqueInTheReport(string file)
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Public(file))));
        var ns = doc.Root!.Name.Namespace;
        var names = doc.Descendants(ns + "Matrix").Descendants(ns + "Grouping")
            .Select(g => (string)g.Attribute("Name")!).ToList();

        Assert.That(names, Has.Count.GreaterThan(1));
        Assert.That(names, Is.Unique);
    }
}
