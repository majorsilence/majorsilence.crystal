using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.OleStorage;
using Majorsilence.Reporting.Rdl;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Charts: what the parser reads of a chart's style, text, legend, gridlines, text sizes and
/// colours, and how the converter draws them. The expected values are the Crystal runtime's
/// own (ISCRChartObject.ChartStyle and its text options), and the sizes and colours those
/// of Crystal's PDF export of the same reports.
/// </summary>
[TestFixture]
public class ChartTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static string Corpus(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static List<ChartObject> Charts(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!.Sections.SelectMany(s => s.Objects).OfType<ChartObject>().ToList();
    }

    private static ChartObject Chart(string path, string name) => Charts(path).Single(c => c.Name == name);

    // ---------------------------------------------------------------------------------
    // Parser: style and subtype (tag-289 bytes 0 and 1)
    // ---------------------------------------------------------------------------------

    // The runtime's ChartStyle.Type and subtype for each: Bar/1 (stacked) and Pie/31,
    // Bar/0 (side by side), Bar/2 (percent), Area, Doughnut, Line.
    [TestCase("benbrahim777__Top5USA-piechart.rpt", false, "Chart1", ChartKind.Column, ChartStacking.Stacked)]
    [TestCase("benbrahim777__Top5USA-piechart.rpt", false, "Graph1", ChartKind.Pie, ChartStacking.None)]
    [TestCase("benbrahim777__Canada-CrossTab.rpt", false, "Graph1", ChartKind.Column, ChartStacking.None)]
    [TestCase("parking__orders_bar_percentage.rpt", true, "Graph1", ChartKind.Column, ChartStacking.PercentStacked)]
    [TestCase("parking__orders_area.rpt", true, "Graph1", ChartKind.Area, ChartStacking.None)]
    [TestCase("parking__orders_doughnut.rpt", true, "Graph1", ChartKind.Doughnut, ChartStacking.None)]
    public void ChartStyle_IsTheRuntimes(string file, bool external, string name, ChartKind kind, ChartStacking stacking)
    {
        var chart = Chart(external ? External(file) : Corpus(file), name);
        Assert.That(chart.Kind, Is.EqualTo(kind));
        Assert.That(chart.Stacking, Is.EqualTo(stacking));
    }

    // CrChartStyleTypeEnum and its subtypes, as tag-289 bytes 0 and 1 carry them. The
    // line, 3D riser and faked-3D bar values are each the runtime's for a corpus chart.
    [TestCase(1, 10, ChartKind.Line, ChartStacking.None)]
    [TestCase(5, 50, ChartKind.Column, ChartStacking.None)]
    [TestCase(0, 4, ChartKind.Column, ChartStacking.Stacked)]
    [TestCase(4, 40, ChartKind.Doughnut, ChartStacking.None)]
    [TestCase(13, 128, ChartKind.Column, ChartStacking.None)]
    public void ChartStyle_Mapping(int styleType, int subtype, ChartKind kind, ChartStacking stacking)
    {
        Assert.That(ChartDefinitionDecoder.MapStyle(styleType, subtype), Is.EqualTo((kind, stacking)));
    }

    // ---------------------------------------------------------------------------------
    // Parser: text, legend and gridlines
    // ---------------------------------------------------------------------------------

    [Test]
    public void CrossTabChart_HasItsTitleAxisTitlesLegendAndGridlines()
    {
        var chart = Chart(Corpus("benbrahim777__Canada-CrossTab.rpt"), "Graph1");
        Assert.Multiple(() =>
        {
            Assert.That(chart.Title, Is.EqualTo("Sales by Product Type and Province"));
            Assert.That(chart.CategoryAxisTitle, Is.EqualTo("Product Type Name"));
            Assert.That(chart.ValueAxisTitle, Is.EqualTo("Sum of Order Amount"));
            Assert.That(chart.ShowLegend, Is.True);
            Assert.That(chart.LegendPosition, Is.EqualTo(ChartLegendPosition.Right));
            // The runtime: AxisYGridLine major, GroupAxisXGridLine none.
            Assert.That(chart.ValueAxisGridLines, Is.True);
            Assert.That(chart.CategoryAxisGridLines, Is.False);
        });
    }

    [Test]
    public void ChartWithItsLegendTurnedOff_HasNone()
    {
        Assert.That(Chart(Corpus("benbrahim777__Top5USA-piechart.rpt"), "Chart1").ShowLegend, Is.False);
        Assert.That(Chart(External("parking__orders_legend_off.rpt"), "Graph1").ShowLegend, Is.False);
    }

    [Test]
    public void LegendPosition_IsRead()
    {
        Assert.That(Chart(External("parking__orders_legend_left.rpt"), "Graph1").LegendPosition,
            Is.EqualTo(ChartLegendPosition.Left));
        Assert.That(Chart(External("parking__orders_legend_right.rpt"), "Graph1").LegendPosition,
            Is.EqualTo(ChartLegendPosition.Right));
    }

    [Test]
    public void GroupAxisGridlines_AreRead()
    {
        // The runtime: GroupAxisXGridLine both, AxisYGridLine both.
        var chart = Chart(External("parking__orders_legend_left.rpt"), "Graph1");
        Assert.That(chart.CategoryAxisGridLines, Is.True);
        Assert.That(chart.ValueAxisGridLines, Is.True);
    }

    // A chart over the report's groups with its axis titles turned off carries no
    // group-by string of its own; it charts the report's group, which is what the
    // runtime's ConditionFields reports ({region.name}). It used to be dropped.
    [Test]
    public void GroupChartWithoutAxisTitles_ChartsTheReportsGroup()
    {
        var chart = Chart(External("meridian_sales__03_sales_by_region_rep.rpt"), "RegionRevenueChart");
        Assert.That(chart.CategoryFields, Is.EqualTo(new[] { "name" }));
        Assert.That(chart.SeriesField, Is.EqualTo("line_amount"));
        Assert.That(chart.CategoryAxisTitle, Is.Empty);
    }

    // ---------------------------------------------------------------------------------
    // Parser: text faces, weights and sizes
    // ---------------------------------------------------------------------------------

    // Crystal's own PDF export of Canada-CrossTab draws the title in Arial Bold at 13.4pt,
    // the axis titles in Arial Bold at 7.4pt and the labels and legend in Arial at 6.8pt.
    [Test]
    public void CrossTabChart_TextIsCrystalsSizeAndWeight()
    {
        var chart = Chart(Corpus("benbrahim777__Canada-CrossTab.rpt"), "Graph1");
        Assert.Multiple(() =>
        {
            Assert.That(chart.TitleFont.Name, Is.EqualTo("Arial"));
            Assert.That(chart.TitleFont.SizePt, Is.EqualTo(13.5).Within(0.2));
            Assert.That(chart.TitleFont.Bold, Is.True);
            Assert.That(chart.CategoryAxisTitleFont.SizePt, Is.EqualTo(7.5).Within(0.2));
            Assert.That(chart.CategoryAxisTitleFont.Bold, Is.True);
            Assert.That(chart.ValueAxisTitleFont.Bold, Is.True);
            Assert.That(chart.CategoryLabelFont.SizePt, Is.EqualTo(6.8).Within(0.2));
            Assert.That(chart.CategoryLabelFont.Bold, Is.False);
            Assert.That(chart.ValueLabelFont.SizePt, Is.EqualTo(6.8).Within(0.2));
            Assert.That(chart.LegendFont.SizePt, Is.EqualTo(6.8).Within(0.2));
            Assert.That(chart.LegendFont.Bold, Is.False);
        });
    }

    // The older template's chart, as Crystal draws it in Top5-Canadian-Customers: a bold
    // 16.3pt title and plain axis titles and labels at 11.6pt, on a chart 252pt tall.
    [Test]
    public void OlderTemplateChart_TextScalesWithItsHeight()
    {
        var chart = Chart(Corpus("benbrahim777__Top5-Canadian-Customers.rpt"), "Chart1");
        Assert.Multiple(() =>
        {
            Assert.That(chart.TitleFont.SizePt, Is.EqualTo(16.5).Within(0.5));
            Assert.That(chart.TitleFont.Bold, Is.True);
            Assert.That(chart.CategoryAxisTitleFont.Bold, Is.False);
            Assert.That(chart.ValueAxisTitleFont.Bold, Is.False);
            Assert.That(chart.CategoryLabelFont.SizePt, Is.EqualTo(11.5).Within(0.3));
        });
    }

    // ---------------------------------------------------------------------------------
    // Parser: template streams and colours
    // ---------------------------------------------------------------------------------

    [Test]
    public void ChartIndex_CountsChartObjectsInOrder()
    {
        var charts = Charts(Corpus("benbrahim777__Top5USA-piechart.rpt"));
        Assert.That(charts.Single(c => c.Name == "Chart1").ChartIndex, Is.EqualTo(0));
        Assert.That(charts.Single(c => c.Name == "Graph1").ChartIndex, Is.EqualTo(1));
    }

    // Each storage has one template stream per tag-180 chart object; that is what pairs
    // them by order. A file where the two counts differ would pair charts with the wrong
    // templates.
    [Test]
    public void EveryChartObject_HasATemplateStream()
    {
        var dir = Path.GetDirectoryName(Corpus("x"))!;
        Assume.That(Directory.Exists(dir), Is.True, "run scripts/download-test-rpts.sh");
        int checkedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.rpt"))
        {
            var result = RptParser.Parse(path);
            if (!result.Success) continue;
            int objects = result.RawChunks.Count(r => r.Tag == 180);
            using var ole = OleReader.Open(path);
            int streams = ole.ListStreamNames().Count(n => n.StartsWith("CHART ", StringComparison.Ordinal));
            Assert.That(streams, Is.EqualTo(objects), Path.GetFileName(path));
            if (objects > 0) checkedFiles++;
        }
        Assert.That(checkedFiles, Is.GreaterThan(0));
    }

    // The fills Crystal's PDF export draws for Canada-CrossTab's eight bars, in order.
    [Test]
    public void CurrentTemplate_PaletteIsTheColoursCrystalDraws()
    {
        var chart = Chart(Corpus("benbrahim777__Canada-CrossTab.rpt"), "Graph1");
        Assert.That(chart.ColorByCategory, Is.True);
        Assert.That(chart.Palette.Take(8), Is.EqualTo(new[]
        {
            "#3A6598", "#EFA252", "#009470", "#DD581F", "#A22D62", "#FECE60", "#27758B", "#DA7062"
        }));
    }

    // The older template draws every bar in one shaded blue (Top5-Canadian-Customers'
    // Chart1), while its colour list starts with maroon: the list is not what it draws.
    [Test]
    public void OlderTemplate_PaletteIsNotUsed()
    {
        var chart = Chart(Corpus("benbrahim777__Top5USA-piechart.rpt"), "Chart1");
        Assert.That(chart.ColorByCategory, Is.False);
        Assert.That(chart.Palette, Is.Empty);
    }

    // Setting a chart's title to 12pt through the runtime's object model stored 1820 in the
    // template on a 216pt-tall chart: the size is a fraction of the chart's height.
    [Test]
    public void TemplateTextHeight_IsAFractionOfTheChartsHeight()
    {
        var chart = new ChartObject { Bounds = new(0, 0, 7200, 4320) };
        var template = new ChartTemplateReader.Template(
            new Dictionary<int, int> { [ChartTemplateReader.TitleText] = 1820 }, [], PaletteDescribesFills: false);
        ChartTemplateReader.ApplyTemplate(chart, template);
        Assert.That(chart.TitleFont.SizePt, Is.EqualTo(12.0).Within(0.05));
    }

    // ---------------------------------------------------------------------------------
    // Converter
    // ---------------------------------------------------------------------------------

    private static XDocument Convert(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        return XDocument.Parse(new RdlConverter().Convert(RptParser.Parse(path).Report!));
    }

    private static XElement RdlChart(XDocument doc, string name) =>
        doc.Descendants(Rdl + "Chart").Single(c => (string?)c.Attribute("Name") == name);

    // The report-footer pie was dropped outright: a report footer with a details table is
    // joined into the table's footer as text only, and only subreports were routed past it.
    [Test]
    public void ReportFooterChart_IsEmitted()
    {
        var doc = Convert(Corpus("benbrahim777__Top5USA-piechart.rpt"));
        var pie = RdlChart(doc, "Graph1");
        Assert.That(pie.Element(Rdl + "Type")?.Value, Is.EqualTo("Pie"));
        Assert.That(pie.Descendants(Rdl + "Caption").First().Value, Is.EqualTo("Top 5 Customers Percentage of Total Orders"));
    }

    // Two charts over the same field must not declare the same grouping name: the engine
    // rejects the report ("Duplicate Grouping name").
    [Test]
    public void TwoChartsOverOneField_HaveDistinctGroupingNames()
    {
        var doc = Convert(Corpus("benbrahim777__Top5USA-piechart.rpt"));
        var names = doc.Descendants(Rdl + "Grouping").Select(g => (string?)g.Attribute("Name")).ToList();
        Assert.That(names, Is.Unique);
    }

    // The engine draws a pie per category with the series as slices; Crystal's one pie of
    // groups is therefore the group field as the series, with no categories.
    [Test]
    public void Pie_SlicesAreTheSeries()
    {
        var pie = RdlChart(Convert(Corpus("benbrahim777__Top5USA-piechart.rpt")), "Graph1");
        Assert.That(pie.Element(Rdl + "CategoryGroupings"), Is.Null);
        Assert.That(pie.Descendants(Rdl + "DynamicSeries").Single()
            .Descendants(Rdl + "GroupExpression").Single().Value, Is.EqualTo("=Fields!Customer_Name.Value"));
        Assert.That(pie.Element(Rdl + "Legend")?.Element(Rdl + "Visible")?.Value, Is.EqualTo("true"));
        Assert.That(pie.Element(Rdl + "CategoryAxis"), Is.Null, "a pie has no axes");
    }

    // The engine draws a doughnut's hole in the plot area's style and skips it without one,
    // so the slices met in the middle. A pie has no hole and no plot area.
    [Test]
    public void Doughnut_HasAPlotAreaForItsHole()
    {
        var ring = RdlChart(Convert(External("parking__orders_doughnut.rpt")), "Graph1");
        Assert.That(ring.Element(Rdl + "PlotArea")?.Element(Rdl + "Style")?.Element(Rdl + "BackgroundColor")?.Value,
            Is.EqualTo("White"));

        var pie = RdlChart(Convert(Corpus("benbrahim777__Top5USA-piechart.rpt")), "Graph1");
        Assert.That(pie.Element(Rdl + "PlotArea"), Is.Null);
    }

    // A chart over a top-N group charts the report's N groups in rank order.
    [Test]
    public void ChartOverTopNGroup_IsFilteredAndSortedByRank()
    {
        var pie = RdlChart(Convert(Corpus("benbrahim777__Top5USA-piechart.rpt")), "Graph1");
        var filter = pie.Element(Rdl + "Filters")?.Element(Rdl + "Filter");
        Assert.That(filter, Is.Not.Null);
        Assert.That(filter!.Element(Rdl + "FilterExpression")!.Value, Does.Contain("CrystalTopNRank"));
        Assert.That(filter.Descendants(Rdl + "FilterValue").Single().Value, Is.EqualTo("=5"));
        Assert.That(pie.Descendants(Rdl + "SortExpression").Single().Value, Does.Match(@"^=Fields!CrystalTopNRank\d+\.Value$"));
    }

    // Crystal colours each bar of Canada-CrossTab's one-series chart differently and lists
    // the bars in its legend. The engine colours and lists series, so the category is the
    // series too, stacked so each bar keeps its full width.
    [Test]
    public void ColourByCategoryBars_AreSeriesAndStacked()
    {
        var chart = RdlChart(Convert(Corpus("benbrahim777__Canada-CrossTab.rpt")), "Graph1");
        Assert.Multiple(() =>
        {
            Assert.That(chart.Element(Rdl + "Type")?.Value, Is.EqualTo("Column"));
            Assert.That(chart.Element(Rdl + "Subtype")?.Value, Is.EqualTo("Stacked"));
            Assert.That(chart.Descendants(Rdl + "DynamicCategories").Single()
                .Descendants(Rdl + "GroupExpression").Single().Value, Is.EqualTo("=Fields!Product_Type_Name.Value"));
            Assert.That(chart.Descendants(Rdl + "DynamicSeries").Single()
                .Descendants(Rdl + "GroupExpression").Single().Value, Is.EqualTo("=Fields!Product_Type_Name.Value"));
            Assert.That(chart.Element(Rdl + "Legend")?.Element(Rdl + "Position")?.Value, Is.EqualTo("RightCenter"));
        });
    }

    [Test]
    public void AxisChart_HasAxesWithCrystalsTitlesAndText()
    {
        var chart = RdlChart(Convert(Corpus("benbrahim777__Canada-CrossTab.rpt")), "Graph1");
        var title = chart.Element(Rdl + "Title")!;
        var category = chart.Element(Rdl + "CategoryAxis")!.Element(Rdl + "Axis")!;
        var value = chart.Element(Rdl + "ValueAxis")!.Element(Rdl + "Axis")!;
        Assert.Multiple(() =>
        {
            Assert.That(title.Element(Rdl + "Style")!.Element(Rdl + "FontSize")!.Value, Is.EqualTo("13.5pt"));
            Assert.That(title.Element(Rdl + "Style")!.Element(Rdl + "FontWeight")!.Value, Is.EqualTo("Bold"));
            Assert.That(category.Element(Rdl + "Title")!.Element(Rdl + "Caption")!.Value, Is.EqualTo("Product Type Name"));
            Assert.That(value.Element(Rdl + "Title")!.Element(Rdl + "Caption")!.Value, Is.EqualTo("Sum of Order Amount"));
            Assert.That(value.Element(Rdl + "Title")!.Element(Rdl + "Style")!.Element(Rdl + "WritingMode")!.Value, Is.EqualTo("tb-rl"));
            Assert.That(value.Element(Rdl + "MajorGridLines")!.Element(Rdl + "ShowGridLines")!.Value, Is.EqualTo("true"));
            Assert.That(category.Element(Rdl + "MajorGridLines"), Is.Null);
            Assert.That(category.Element(Rdl + "Style")!.Element(Rdl + "FontSize")!.Value, Is.EqualTo("6.8pt"));
        });
    }

    // A one-series chart in the older template stays one series: Crystal draws all its bars
    // in one colour and no legend.
    [Test]
    public void OlderTemplateChart_StaysOneSeries()
    {
        var chart = RdlChart(Convert(Corpus("benbrahim777__Top5USA-piechart.rpt")), "Chart1");
        Assert.That(chart.Element(Rdl + "SeriesGroupings"), Is.Null);
        Assert.That(chart.Element(Rdl + "Legend"), Is.Null);
    }

    // Everything above is written in elements the engine knows: it loads both reports with
    // nothing it reports at warning level or above.
    [TestCase("benbrahim777__Top5USA-piechart.rpt")]
    [TestCase("benbrahim777__Canada-CrossTab.rpt")]
    public async Task ConvertedCharts_LoadInTheEngine(string file)
    {
        var doc = Convert(Corpus(file));
        var parser = new RDLParser(doc.ToString()) { SkipDatabaseSchemaValidation = true };
        Report report = await parser.Parse();
        var errors = (report.ErrorItems?.Cast<string>() ?? [])
            .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m, @"^[^:\r\n]{1,40}:\s*[0-3]\s*-\s"))
            .ToList();
        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }

    [Test]
    public void SyntheticChart_WithEveryPartSet_LoadsInTheEngine()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Charts",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 4400,
                    Objects =
                    [
                        new ChartObject
                        {
                            Name = "Bars", Bounds = new(0, 0, 7200, 4320), Title = "By region",
                            Kind = ChartKind.Column, Stacking = ChartStacking.None,
                            CategoryFields = ["Region"], SeriesField = "Amount",
                            CategoryAxisTitle = "Region", ValueAxisTitle = "Sum of Amount",
                            ShowLegend = true, LegendPosition = ChartLegendPosition.Bottom,
                            ValueAxisGridLines = true, CategoryAxisGridLines = true,
                            TitleFont = new ChartFont { Name = "Arial", SizePt = 13.5, Bold = true, Italic = true },
                            Palette = ["#3A6598"], ColorByCategory = true
                        },
                        new ChartObject
                        {
                            Name = "Ring", Bounds = new(7200, 0, 4320, 4320), Title = "Share",
                            Kind = ChartKind.Doughnut, CategoryFields = ["Region"], SeriesField = "Amount",
                            ShowLegend = true
                        }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);
        var parser = new RDLParser(rdl) { SkipDatabaseSchemaValidation = true };
        Report engineReport = parser.Parse().GetAwaiter().GetResult();
        var errors = (engineReport.ErrorItems?.Cast<string>() ?? [])
            .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(m, @"^[^:\r\n]{1,40}:\s*[0-3]\s*-\s"))
            .ToList();
        Assert.That(errors, Is.Empty, string.Join(" | ", errors));
    }
}
