using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Runtime;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// How runtime overrides land on the parsed model, and how a key that names nothing is
/// reported rather than failing the render: the rules a host's request contract relies on.
/// </summary>
[TestFixture]
public class RenderPrepOverrideTests
{
    private static ReportDefinition Build() => new()
    {
        ReportTitle = "Overrides",
        Fields = [
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" },
            new DatabaseField { Name = "Name", ColumnName = "Name", TableName = "Customers", DataType = "String" },
            new FormulaField { Name = "pos", FormulaText = "1" }
        ],
        Sections = [
            new Section { Type = SectionType.Details, HeightTwips = 600, Objects = [
                new TextObject { Name = "Title", Text = "hi", Bounds = new(0, 0, 1440, 240) },
                new FieldObject { Name = "Amount1", FieldName = "Amount", Bounds = new(1440, 0, 1440, 240) },
                new SubreportObject { Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 240, 2880, 240),
                    Report = new ReportDefinition { ReportTitle = "Child",
                        Fields = [new ParameterField { Name = "CustId", DataType = "String" }],
                        Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }] } }
            ] }
        ]
    };

    private static ReportObject Obj(ReportDefinition r, string name) =>
        r.Sections.SelectMany(s => s.Objects).First(o => o.Name == name);

    [Test]
    public void CanGrow_SetsTheObjectsFormat_MatchingTheNameCaseInsensitively()
    {
        var report = Build();
        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { CanGrow = { ["amount1"] = true } });

        Assert.That(Obj(report, "Amount1").Format.CanGrow, Is.True);
        Assert.That(Obj(report, "Title").Format.CanGrow, Is.False);
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void AKeyThatNamesNothing_IsReportedOnce_AndNothingElseIsTouched()
    {
        var report = Build();
        var overrides = new RuntimeOverrides
        {
            Suppress = { ["NoSuchObject"] = true },
            CanGrow = { ["NoSuchObject"] = true },
            Resize = { ["NoSuchObject"] = 100 },
            ObjectText = { ["NoSuchText"] = "x" },
            FormulaFieldText = { ["NoSuchFormula"] = "2" },
            MoveObjectPosition = { new MoveObjectOverride { ObjectName = "NoSuchObject", Axis = MoveAxis.Top, Amount = 1 } },
            SubreportParameters = { ["NoSuchSub"] = new() { ["CustId"] = "C1" } },
            SortByFieldName = "Orders.NoSuchColumn"
        };

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, overrides);

        Assert.That(warnings, Has.Count.EqualTo(8));
        Assert.That(warnings.Count(w => w.Contains("NoSuchObject")), Is.EqualTo(4));
        Assert.That(warnings.Any(w => w.Contains("NoSuchText")));
        Assert.That(warnings.Any(w => w.Contains("NoSuchFormula")));
        Assert.That(warnings.Any(w => w.Contains("NoSuchSub")));
        Assert.That(warnings.Any(w => w.Contains("NoSuchColumn")));
        Assert.That(report.SortFields, Is.Empty);
        Assert.That(Obj(report, "Amount1").SuppressOverride, Is.Null);
    }

    [Test]
    public void ATopMove_StaysInsideTheSection_AndALeftMoveIsNotClamped()
    {
        var report = Build();
        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { MoveObjectPosition = {
            new MoveObjectOverride { ObjectName = "Amount1", Axis = MoveAxis.Top, Amount = 1000 },
            new MoveObjectOverride { ObjectName = "Title", Axis = MoveAxis.Top, Amount = -500, Relative = true },
            new MoveObjectOverride { ObjectName = "Title", Axis = MoveAxis.Left, Amount = -50 } } });

        // Section 600 tall, object 240 tall: the furthest down it can go is 360.
        Assert.That(Obj(report, "Amount1").Bounds.Top, Is.EqualTo(360));
        Assert.That(Obj(report, "Title").Bounds.Top, Is.EqualTo(0));
        Assert.That(Obj(report, "Title").Bounds.Left, Is.EqualTo(-50));
    }

    [Test]
    public void SortBy_ReplacesTheFirstRecordSort_ResolvingTableDotField()
    {
        var report = Build();
        report.SortFields.Add(new SortField { FieldName = "Name", TableName = "Customers" });
        report.SortFields.Add(new SortField { FieldName = "Amount", TableName = "Orders", Direction = SortDirection.Descending });

        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "orders.amount" });
        Assert.That(report.SortFields.Select(s => (s.TableName, s.FieldName, s.Direction)), Is.EqualTo(new[]
        {
            ("Orders", "Amount", SortDirection.Ascending),
            ("Orders", "Amount", SortDirection.Descending)
        }), "only the first is replaced, keeping its direction");

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Customers.Amount" });
        Assert.That(warnings, Has.Count.EqualTo(1), "the column exists, but not on that table");
        Assert.That(report.SortFields[0].FieldName, Is.EqualTo("Amount"));
    }

    [Test]
    public void SortBy_WithNoSortAndNoGroup_ChangesNothingAndSaysSo()
    {
        // The Crystal runtime raises an error here; its callers log it and carry on.
        var report = Build();

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Orders.Amount" });

        Assert.That(report.SortFields, Is.Empty);
        Assert.That(warnings, Is.EqualTo(new[] { "SortByFieldName: the report defines no sort fields, so there is none to replace" }));
    }

    [Test]
    public void SortBy_OnAGroupedReport_RegroupsOnTheField_ButNotOnADate()
    {
        // Crystal's sort list starts with the first group's own sort, so that is what changes.
        var report = Build();
        report.Groups.Add(new GroupDefinition { Level = 0, FieldName = "Name" });
        report.Fields.Add(new DatabaseField { Name = "Placed", ColumnName = "Placed", TableName = "Orders", DataType = "DateTime" });

        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Orders.Amount" });
        Assert.That(report.Groups[0].FieldName, Is.EqualTo("Amount"));
        Assert.That(report.SortFields, Is.Empty);

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Orders.Placed" });
        Assert.That(report.Groups[0].FieldName, Is.EqualTo("Amount"));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void SortBy_CanNameAColumnTheReportDoesNotPlace()
    {
        // As Crystal resolves Database.Tables[t].Fields[f]: any column of the table will do.
        var report = Build();
        report.SortFields.Add(new SortField { FieldName = "Name", TableName = "Customers" });
        report.DataSources.Add(new DataSource
        {
            Name = "DataSource1",
            Tables = { new TableDefinition { Name = "Customers", Alias = "Customers", Columns = { new ColumnDefinition { Name = "City", DataType = "String" } } } }
        });

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Customers.City" });

        Assert.That(warnings, Is.Empty);
        Assert.That(report.SortFields[0].FieldName, Is.EqualTo("City"));
        Assert.That(report.Fields.OfType<DatabaseField>().Any(f => f.ColumnName == "City" && f.TableName == "Customers"), Is.True,
            "the column becomes a field, so the RDL has something to sort on");
    }

    [Test]
    public void FormulaText_MatchesWithOrWithoutTheAt()
    {
        var report = Build();
        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { FormulaFieldText = { ["@POS"] = "2" } });

        Assert.That(report.Fields.OfType<FormulaField>().Single().FormulaText, Is.EqualTo("2"));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void SubreportParameters_LandOnTheSubreportObject_ByDeclaredName()
    {
        var report = Build();
        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SubreportParameters = {
            ["sub1"] = new() { ["custid"] = "C1", ["NoSuchParam"] = 2 } } });

        var sub = (SubreportObject)Obj(report, "Sub1");
        Assert.That(sub.ParameterValueOverrides, Is.EqualTo(new Dictionary<string, object?> { ["CustId"] = "C1" }));
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("NoSuchParam"));
    }

    [TestCase(12240, 15840, 11906, 16838, TestName = "PrinterPaper_PutsAPortraitTemplateOnThePrintersPaper")]
    [TestCase(15840, 12240, 16838, 11906, TestName = "PrinterPaper_KeepsALandscapeTemplateLandscape")]
    public void PrinterPaper_ReplacesThePageOfATemplateOnItsPrintersPaper(int width, int height, int wantWidth, int wantHeight)
    {
        var report = Build();
        report.Page = new PageLayout { WidthTwips = width, HeightTwips = height, LeftMarginTwips = 300, PaperFromPrinter = true };

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { PrinterPaper = PaperSize.A4 });

        Assert.That((report.Page.WidthTwips, report.Page.HeightTwips), Is.EqualTo((wantWidth, wantHeight)));
        Assert.That(report.Page.LeftMarginTwips, Is.EqualTo(300), "margins are kept");
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void PrinterPaper_LeavesATemplateThatNamesItsOwnPaper()
    {
        var report = Build();
        report.Page = new PageLayout { WidthTwips = 12240, HeightTwips = 20160 };

        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { PrinterPaper = PaperSize.A4 });

        Assert.That((report.Page.WidthTwips, report.Page.HeightTwips), Is.EqualTo((12240, 20160)));
    }
}
