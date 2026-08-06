using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Reporting.Rdl;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Smoke tests that converted RDL actually loads in the Majorsilence.Reporting
/// engine — the conversion target — with no Error-severity items. This catches
/// schema/expression problems that XML well-formedness checks cannot.
/// </summary>
[TestFixture]
public class EngineCompatibilityTests
{
    private static string CorpusPath(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    // Representative feature coverage: plain fields + embedded image, cross-tab
    // (Matrix), grouped report with summaries, and subreports with companions.
    [TestCase("benbrahim777__CustomerList.rpt")]
    [TestCase("benbrahim777__Canada-CrossTab.rpt")]
    [TestCase("benbrahim777__SalesByCustomer-Grouped.rpt")]
    [TestCase("boyum__Payments.rpt")]
    public async Task ConvertedRdl_LoadsInMajorsilenceReportingEngine(string corpusFile)
    {
        string rptPath = CorpusPath(corpusFile);
        Assume.That(File.Exists(rptPath), Is.True,
            $"{corpusFile} not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(rptPath);
        Assert.That(result.Success, Is.True);

        // Convert parent + subreport companions into an isolated folder so the
        // engine can resolve <Subreport><ReportName> references.
        string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "engine-smoke", Path.GetFileNameWithoutExtension(corpusFile));
        Directory.CreateDirectory(dir);
        string stem = Path.GetFileNameWithoutExtension(corpusFile);
        string mainPath = Path.Combine(dir, stem + ".rdl");
        File.WriteAllText(mainPath, new RdlConverter().Convert(result.Report!, $"{stem}_"));
        WriteCompanions(result.Report!, mainPath);

        foreach (var rdlPath in Directory.EnumerateFiles(dir, "*.rdl"))
        {
            var parser = new RDLParser(File.ReadAllText(rdlPath)) { Folder = dir };
            Report report = await parser.Parse();

            var errors = (report.ErrorItems?.Cast<string>() ?? [])
                .Where(e => e.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                            e.StartsWith("Fatal", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.That(errors, Is.Empty,
                $"engine reported errors for {Path.GetFileName(rdlPath)}: {string.Join(" | ", errors)}");
        }
    }

    // No corpus file exercises a multi-axis / multi-cell cross-tab (the public
    // corpus's cross-tabs are all 1 row field x 1 column field x 1 cell), so this
    // synthetic report is the only schema-level check that the engine accepts
    // the nested ColumnGrouping/RowGrouping + StaticColumns shape.
    [Test]
    public async Task MultiAxisMultiCellMatrix_LoadsInMajorsilenceReportingEngine()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pivot",
            Fields = [
                new DatabaseField { Name = "Country", ColumnName = "Country", DataType = "String" },
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Year", ColumnName = "Year", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new DatabaseField { Name = "Units", ColumnName = "Units", DataType = "Int32" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new CrossTabObject
                    {
                        Name = "CrossTab1",
                        Bounds = new(0, 0, 5760, 1440),
                        RowGroupFields = ["Country", "Region"],
                        ColumnGroupFields = ["Year"],
                        Cells = [
                            new CrossTabCell("Amount", AggregateFunction.Sum),
                            new CrossTabCell("Units", AggregateFunction.Count)
                        ]
                    }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);
        var parser = new RDLParser(rdl);
        Report engineReport = await parser.Parse();

        var errors = (engineReport.ErrorItems?.Cast<string>() ?? [])
            .Where(e => e.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                        e.StartsWith("Fatal", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.That(errors, Is.Empty, $"engine reported errors: {string.Join(" | ", errors)}");
    }

    private static void WriteCompanions(ReportDefinition report, string mainRdlPath)
    {
        string dir = Path.GetDirectoryName(mainRdlPath)!;
        string stem = Path.GetFileNameWithoutExtension(mainRdlPath);
        foreach (var sub in report.Sections.SelectMany(s => s.Objects).OfType<SubreportObject>()
                     .Where(s => s.Report is not null))
        {
            string name = RdlConverter.SubreportRdlName($"{stem}_", sub.SubreportName);
            string path = Path.Combine(dir, name + ".rdl");
            File.WriteAllText(path, new RdlConverter().Convert(sub.Report!, $"{name}_"));
            WriteCompanions(sub.Report!, path);
        }
    }
}
