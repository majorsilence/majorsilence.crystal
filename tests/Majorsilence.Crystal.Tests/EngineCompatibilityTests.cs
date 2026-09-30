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
    // Percentage-of-total summary sharing a column with a plain Sum summary.
    [TestCase("souvikduttachoudhury__CustomerProfileReport.rpt")]
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

    // Chart is a brand-new report-item type (Round 4) with no prior engine
    // verification; schema derived from the engine's own Chart/ChartData/
    // DynamicCategories source, not from an example — verify it against the
    // real engine rather than trusting the derivation alone.
    [Test]
    public async Task Chart_LoadsInMajorsilenceReportingEngine()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pie",
            Fields = [
                new DatabaseField { Name = "CustomerName", ColumnName = "Customer Name", DataType = "String" },
                new DatabaseField { Name = "OrderAmount", ColumnName = "Order Amount", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new ChartObject
                    {
                        Name = "Chart1",
                        Bounds = new(0, 0, 5760, 1440),
                        Title = "Top 5 Customers",
                        Kind = ChartKind.Pie,
                        CategoryFields = ["Customer Name"],
                        SeriesField = "Order Amount",
                        SeriesFunction = AggregateFunction.Sum
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

    // What each formula prints in Crystal, measured in the Crystal runtime (both syntaxes
    // agree), with {T.A} set to the value given. The emitted text alone cannot show these:
    // the engine's own Round, \ and Mod each give a different answer for some of them.
    [TestCase("Round({T.A}, 2)", 0.125, "0.13", TestName = "Round_HalfGoesAwayFromZero")]
    [TestCase("Round({T.A}, 2)", -0.125, "-0.13", TestName = "Round_HalfGoesAwayFromZero_Negative")]
    [TestCase("Round({T.A}, 2)", 1.005, "1.01", TestName = "Round_AtTheDigitsAsWritten")]
    [TestCase("Round({T.A})", 2.5, "3", TestName = "Round_ToAWholeNumber")]
    [TestCase("Round({T.A}, -1)", 1225, "1230", TestName = "Round_NegativePlaces_RoundToTens")]
    [TestCase("{T.A} \\ 2", 7.5, "4", TestName = "IntegerDivide_RoundsItsOperands")]
    [TestCase("7 \\ {T.A}", 2.5, "2", TestName = "IntegerDivide_RoundsItsDivisorHalfAway")]
    [TestCase("-{T.A} \\ 2", 7, "-3", TestName = "IntegerDivide_TruncatesTheQuotient")]
    [TestCase("{T.A} Mod 2", 7.5, "0", TestName = "Mod_RoundsItsOperands")]
    [TestCase("-{T.A} Mod 3", 7, "-1", TestName = "Mod_TakesTheDividendsSign")]
    [TestCase("{T.A} \\ 2 * 2", 7, "1", TestName = "IntegerDivide_BindsLooserThanMultiply")]
    [TestCase("9 Mod 5 \\ {T.A}", 2, "1", TestName = "IntegerDivide_BindsTighterThanMod")]
    public async Task Formula_PrintsWhatCrystalPrints(string formula, double a, string expected)
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Formula",
            Fields = [
                new DatabaseField { Name = "A", ColumnName = "A", DataType = "Float64" },
                new FormulaField { Name = "f", FormulaText = formula }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "@f", Bounds = new(0, 0, 2880, 240) }] }
            ]
        };
        string rdl = new RdlConverter().Convert(report);

        var engineReport = await new RDLParser(rdl) { SkipDatabaseSchemaValidation = true }.Parse();
        Assert.That(engineReport.ErrorMaxSeverity, Is.LessThanOrEqualTo(4),
            string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
        var table = new System.Data.DataTable();
        table.Columns.Add("A", typeof(double));
        table.Rows.Add(a);
        string dataSet = System.Text.RegularExpressions.Regex.Match(rdl, "<DataSet Name=\"([^\"]+)\"").Groups[1].Value;
        await engineReport.DataSets[dataSet].SetData(table);
        await engineReport.RunGetData(null);
        using var pages = await engineReport.BuildPages();
        var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>()
            .Select(t => t.Text).ToList();

        Assert.That(printed, Is.EqualTo(new[] { expected }), $"emitted: {rdl[rdl.IndexOf("<Value>=")..].Split('\n')[0]}");
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
