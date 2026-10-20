using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;
using Majorsilence.Reporting.Rdl;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// A chart inside a subreport. The engine converts a subreport with a second, row-sentinel
/// dataset, and a data region may leave its dataset unnamed only while there is one: an
/// unnamed chart there failed to compile ("Chart1 must specify a DataSetName"), the
/// subreport was left unbuilt, and rendering the parent threw from Subreport.RunPage.
/// USAvsFrance, USAvsFranceOnDemand and Top5USAunderlay each have such a subreport.
/// </summary>
[TestFixture]
public class ChartSubreportTests
{
    [OneTimeSetUp]
    public void Init() => ReportEngine.Init();

    private static ReportDefinition ChartReport() => new()
    {
        ReportTitle = "Sales",
        Fields =
        [
            new DatabaseField { Name = "Customer", ColumnName = "Customer", TableName = "Orders", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }
        ],
        Sections =
        [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 4400, Objects =
            [
                new ChartObject
                {
                    Name = "Chart1", Bounds = new(0, 0, 7200, 4320), Title = "Sum of Amount / Customer",
                    Kind = ChartKind.Column, Stacking = ChartStacking.Stacked,
                    CategoryFields = ["Customer"], SeriesField = "Amount"
                }
            ] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects =
            [
                new FieldObject { Name = "CustomerField", FieldName = "Customer", Bounds = new(0, 0, 2880, 240) }
            ] }
        ]
    };

    private static DataTable Orders()
    {
        var table = new DataTable();
        table.Columns.Add("Customer", typeof(string));
        table.Columns.Add("Amount", typeof(double));
        table.Rows.Add("Alpha", 10.0);
        table.Rows.Add("Beta", 20.0);
        return table;
    }

    [Test]
    public async Task SubreportWithAChart_RendersWithoutError()
    {
        var main = new ReportDefinition
        {
            ReportTitle = "Main",
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 600, Objects =
                    [new TextObject { Name = "Title", Text = "Main title", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.ReportFooter, HeightTwips = 6000, Objects =
                [
                    new SubreportObject
                    {
                        Name = "Sales", SubreportName = "Sales", Bounds = new(0, 0, 7600, 5600), Report = ChartReport()
                    }
                ] }
            ]
        };
        var overrides = new RuntimeOverrides { SubreportData = { ["Sales"] = Orders() } };

        string dir = Path.Combine(Path.GetTempPath(), "chartsubreport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var warnings = new List<string>();
            using var engineReport = await ReportEngine.LoadAsync(main, overrides, dir, warnings);
            using var pages = await engineReport.BuildPages();
            Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(8),
                string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
            var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).ToList();
            Assert.That(printed.OfType<PageText>().Select(t => t.Text), Does.Contain("Main title"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The corpus reports the crash was found in, rendered from the file, with no data.
    [TestCase("benbrahim777__USAvsFrance.rpt")]
    [TestCase("benbrahim777__USAvsFranceOnDemand.rpt")]
    [TestCase("benbrahim777__Top5USAunderlay.rpt")]
    public async Task CorpusReportWithAChartInASubreport_Renders(string file)
    {
        string path = Path.GetFullPath($"../../../../rpt-corpus/{file}", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, $"{file} not found — run scripts/download-test-rpts.sh");
        using var rpt = File.OpenRead(path);
        byte[] pdf = await new ReportEngine().ExportAsync(rpt, new RuntimeOverrides(), ExportFormat.Pdf);
        Assert.That(pdf, Is.Not.Empty);
    }
}
