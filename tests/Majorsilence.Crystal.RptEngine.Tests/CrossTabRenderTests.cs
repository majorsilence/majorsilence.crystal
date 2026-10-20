using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A cross-tab rendered end to end: the values Crystal prints in its cells and totals, on a
/// handful of hand-made rows where every one of them can be worked out by hand.
/// </summary>
[TestFixture]
public class CrossTabRenderTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static string CorpusPath(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    // Every column the report's data fixture has, so the DataSet finds each field it
    // declares; only the ones the cross-tab and the record selection read are filled.
    private static DataTable CanadaData(params (string Region, string ProductType, decimal Amount)[] rows)
    {
        string header = File.ReadLines(Path.GetFullPath(
            "../../../../reference-data/benbrahim777__Canada-CrossTab.csv", AppContext.BaseDirectory)).First();
        var data = new DataTable();
        foreach (string column in header.Split("\",\"").Select(c => c.Trim('"')))
            data.Columns.Add(column, column == "Order Amount" ? typeof(decimal) : typeof(string));
        foreach (var (region, type, amount) in rows)
        {
            var row = data.NewRow();
            row["Country"] = "Canada";   // the report selects Canada's customers only
            row["Region"] = region;
            row["Product Type Name"] = type;
            row["Order Amount"] = amount;
            data.Rows.Add(row);
        }
        return data;
    }

    private static async Task<List<PageText>> Printed(string rpt, DataTable data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crosstab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var definition = RptParser.Parse(rpt).Report!;
            using var report = await ReportEngine.LoadAsync(definition, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await report.BuildPages();
            return pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().ToList();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Canada-CrossTab sums Order Amount by Region down and Product Type Name across. In
    // Crystal's export every cell prints in the cell's currency format, a province with none
    // of a product type prints $0.00, and a Total row and a Total column close the grid.
    [Test]
    public async Task CanadaCrossTab_PrintsFormattedCellsZeroesAndTotals()
    {
        string rpt = CorpusPath("benbrahim777__Canada-CrossTab.rpt");
        Assume.That(File.Exists(rpt), Is.True, "needs the public corpus - run scripts/download-test-rpts.sh");

        var data = CanadaData(("BC", "Competition", 100.25m), ("BC", "Gloves", 50m), ("MB", "Gloves", 10.5m));

        var texts = (await Printed(rpt, data)).Select(t => t.Text).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(texts, Does.Contain("$100.25"), "BC / Competition");
            Assert.That(texts, Does.Contain("$0.00"), "MB / Competition - nothing summed prints zero");
            Assert.That(texts, Does.Contain("$60.50"), "the Total row under Gloves");
            Assert.That(texts, Does.Contain("$150.25"), "the Total column beside BC");
            Assert.That(texts, Does.Contain("$160.75"), "where the Total row meets the Total column");
            Assert.That(texts.Count(t => t == "Total"), Is.EqualTo(2), "one label for the row, one for the column");
        });
    }

    // A grand-total colour fills the whole Total row - its label and every value in it - and
    // nothing else, as parking__crosstab_row_grandtotal_bgcolor's export does. The engine
    // does not apply a Subtotal's own Style, so each cell has to find out for itself that it
    // is printing the total.
    [Test]
    public async Task RowGrandTotalColour_FillsTheTotalRowOnly()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pivot",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "T", DataType = "String" },
                new DatabaseField { Name = "Product", ColumnName = "Product", TableName = "T", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Int32" }],
            Sections = [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 2000, Objects = [
                    new CrossTabObject
                    {
                        Name = "CrossTab1",
                        Bounds = new(0, 0, 4000, 1300),
                        RowGroupFields = ["Region"],
                        ColumnGroupFields = ["Product"],
                        Cells = [new CrossTabCell("Amount", AggregateFunction.Sum)],
                        ShowGrid = true,
                        RowLevels = [new CrossTabAxisLevel { Level = 0, TotalLabel = "Total", TotalBackColor = "#FFFF00" }],
                    }] },
                new Section { Type = SectionType.Details, HeightTwips = 0 }]
        };
        var data = new DataTable();
        data.Columns.Add("Region", typeof(string));
        data.Columns.Add("Product", typeof(string));
        data.Columns.Add("Amount", typeof(int));
        data.Rows.Add("West", "A", 1);
        data.Rows.Add("West", "B", 2);
        data.Rows.Add("East", "A", 40);

        string dir = Path.Combine(Path.GetTempPath(), "crosstab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Read while the pages are alive: disposing them lets go of each item's style.
        var background = new List<(string Text, int Argb)>();
        try
        {
            using var engineReport = await ReportEngine.LoadAsync(report, new RuntimeOverrides { Data = data }, dir, []);
            using var pages = await engineReport.BuildPages();
            background.AddRange(pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
                .Select(t => (t.Text, t.SI?.BackgroundColor.ToArgb() ?? 0)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        int yellow = System.Drawing.Color.FromArgb(255, 255, 0).ToArgb();
        bool Yellow(string text) => background.First(t => t.Text == text).Argb == yellow;
        // Two labels read "Total": the Total column's, printed with the column headers
        // first, and the Total row's, printed last.
        var totals = background.Where(t => t.Text == "Total").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(totals, Has.Count.EqualTo(2));
            Assert.That(totals[^1].Argb, Is.EqualTo(yellow), "the Total row's label");
            Assert.That(totals[0].Argb, Is.Not.EqualTo(yellow), "the Total column's label");
            Assert.That(Yellow("41"), Is.True, "A's total, in the Total row");
            Assert.That(Yellow("43"), Is.True, "the grand total, where the Total row meets the Total column");
            Assert.That(Yellow("1"), Is.False, "a value inside the grid");
            Assert.That(Yellow("3"), Is.False, "West's total, in the Total column");
        });
    }

    // The grid's pitch is the file's: 66.4pt columns and 21.5pt rows, with the row headers
    // 83.9pt wide - what Crystal's export draws for this report.
    [Test]
    public async Task CanadaCrossTab_CellsSitOnCrystalsGrid()
    {
        string rpt = CorpusPath("benbrahim777__Canada-CrossTab.rpt");
        Assume.That(File.Exists(rpt), Is.True, "needs the public corpus - run scripts/download-test-rpts.sh");

        var data = CanadaData(("BC", "Competition", 1m), ("MB", "Gloves", 2m));

        var printed = await Printed(rpt, data);
        var competition = printed.First(t => t.Text == "Competition");
        var gloves = printed.First(t => t.Text == "Gloves");
        var bc = printed.First(t => t.Text == "BC");
        var mb = printed.First(t => t.Text == "MB");

        Assert.Multiple(() =>
        {
            Assert.That(gloves.X - competition.X, Is.EqualTo(66.4f).Within(0.05f));
            Assert.That(mb.Y - bc.Y, Is.EqualTo(21.5f).Within(0.05f));
            Assert.That(competition.X - bc.X, Is.EqualTo(83.9f).Within(0.05f));
        });
    }
}
