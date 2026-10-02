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
    [TestCase("If {T.A} = 1 Then True", 2, "False", TestName = "IfWithoutElse_BooleanIsFalse")]
    [TestCase("Not (If {T.A} = 1 Then True)", 2, "True", TestName = "IfWithoutElse_BooleanCanBeNegated")]
    [TestCase("If {T.A} = 1 Then 5", 2, "0", TestName = "IfWithoutElse_NumberIsZero")]
    [TestCase("(If {T.A} = 1 Then 5) + 1", 2, "1", TestName = "IfWithoutElse_NumberCanBeAddedTo")]
    [TestCase("\"[\" & (If {T.A} = 1 Then \"x\") & \"]\"", 2, "[]", TestName = "IfWithoutElse_StringIsEmpty")]
    [TestCase("Select {T.A} Case 1: 5", 2, "0", TestName = "SelectWithoutMatch_NumberIsZero")]
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

    // The two mechanisms RptEngine renders offline with, exercised together: the RDL written
    // without the connection, and the engine's skip flag. A dataset given no data renders
    // empty; nothing is opened, so no connection error of the exception kind is logged.
    [Test]
    public async Task OfflineRdl_RendersWithoutOpeningTheTemplatesConnection()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Offline",
            DataSources = [new Model.DataSource { Name = "Main", Kind = Model.DataSourceKind.Native,
                ServerName = "db.example.test", DatabaseName = "Sales" }],
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };
        string rdl = new RdlConverter { OmitConnections = true }.Convert(report);
        Assert.That(rdl, Does.Not.Contain("db.example.test"));

        var engineReport = await new RDLParser(rdl) { SkipDatabaseSchemaValidation = true }.Parse();
        await engineReport.RunGetData(null);
        using var pages = await engineReport.BuildPages();

        var items = engineReport.ErrorItems?.Cast<string>().ToList() ?? [];
        Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(8), string.Join(" | ", items));
        Assert.That(items.Where(e => e.Contains("DataSource '", StringComparison.Ordinal)), Is.Empty,
            "that message is logged only after a connection object was created and opening it threw");
        Assert.That(pages.Count, Is.GreaterThanOrEqualTo(1));
    }

    // A report that reads no table prints its Details section once, as Crystal does: a
    // text object alone, and placed parameters with their page-header labels above them.
    // It used to render a blank page, the Details objects going nowhere (#32).
    [Test]
    public async Task AReportThatReadsNoTable_PrintsItsDetailsOnce_WithItsPageHeaderAboveIt()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "No table",
            Fields = [
                new ParameterField { Name = "Region", DataType = "String" },
                new ParameterField { Name = "Active", DataType = "Boolean" }
            ],
            Sections = [
                new Section { Type = SectionType.PageHeader, HeightTwips = 600, Objects = [
                    new TextObject { Name = "RegionLabel", Text = "Region label", Bounds = new(120, 360, 2600, 240) }] },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 600 },
                new Section { Type = SectionType.Details, HeightTwips = 1900, Objects = [
                    new FieldObject { Name = "RegionValue", FieldName = "?Region", Bounds = new(120, 0, 2600, 240) },
                    new FieldObject { Name = "ActiveValue", FieldName = "?Active", Bounds = new(3750, 0, 2600, 240) },
                    new TextObject { Name = "Title", Text = "Test Report", Bounds = new(8400, 720, 2250, 240) }] }
            ]
        };
        string rdl = new RdlConverter().Convert(report);
        Assert.That(rdl, Does.Not.Contain("<Table "), "a dataset with no rows would print a table's details zero times");
        Assert.That(rdl, Does.Not.Contain("<PageHeader>"), "the page header is a body band here, not RDL's page header");

        var engineReport = await new RDLParser(rdl) { SkipDatabaseSchemaValidation = true }.Parse();
        Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(8), string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
        await engineReport.RunGetData(new System.Collections.Hashtable { ["Region"] = "West", ["Active"] = true });
        using var pages = await engineReport.BuildPages();
        var texts = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().ToList();
        var printed = texts.Select(t => t.Text).ToList();

        Assert.That(printed, Is.SupersetOf(new[] { "Region label", "West", "True", "Test Report" }));
        Assert.That(printed.Count(t => t == "Test Report"), Is.EqualTo(1), "once, not once per row");
        float labelY = texts.First(t => t.Text == "Region label").Y;
        float valueY = texts.First(t => t.Text == "West").Y;
        Assert.That(valueY, Is.GreaterThan(labelY), "the page header prints above the details, as on Crystal's page one");
    }

    // A formula placed in such a report is a calculated field of a dataset no item is bound
    // to, which cannot resolve in the body. It depends only on parameters, constants and
    // other formulas, so its expression is written in place, through any chain of formulas.
    [Test]
    public async Task AReportThatReadsNoTable_PrintsItsFormulas_ThroughAChainOfThem()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "No table, formulas",
            Fields = [
                new ParameterField { Name = "Region", DataType = "String" },
                new FormulaField { Name = "greeting", FormulaText = "\"Hello \" & {?Region}" },
                new FormulaField { Name = "shout", FormulaText = "{@greeting} & \"!\"" }
            ],
            Sections = [
                new Section { Type = SectionType.Details, HeightTwips = 600, Objects = [
                    new FieldObject { Name = "Shout", FieldName = "@shout", Bounds = new(120, 0, 4000, 240) }] }
            ]
        };
        string rdl = new RdlConverter().Convert(report);
        string body = rdl[rdl.IndexOf("<Body>", StringComparison.Ordinal)..];
        Assert.That(body, Does.Not.Contain("Fields!"), "no dataset field reference is left outside the DataSets block");

        var engineReport = await new RDLParser(rdl) { SkipDatabaseSchemaValidation = true }.Parse();
        Assert.That(engineReport.ErrorMaxSeverity, Is.LessThan(8), string.Join(" | ", engineReport.ErrorItems?.Cast<string>() ?? []));
        await engineReport.RunGetData(new System.Collections.Hashtable { ["Region"] = "West" });
        using var pages = await engineReport.BuildPages();
        var printed = pages.Cast<Page>().SelectMany(p => p.Cast<PageItem>()).OfType<PageText>().Select(t => t.Text).ToList();

        Assert.That(printed, Does.Contain("Hello West!"));
    }

    // With a table to read, Details prints once per record, and the details table is kept.
    [Test]
    public void AReportThatReadsATable_KeepsItsDetailsTable()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "With table",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Table "));
        Assert.That(rdl, Does.Not.Contain("Band_Details"));
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
