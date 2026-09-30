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
    public void SortBy_ResolvesTableDotField_AddsWhenNoneParsed_ThenReplacesTheFirst()
    {
        var report = Build();

        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "orders.amount" });
        Assert.That(report.SortFields.Select(s => s.FieldName), Is.EqualTo(new[] { "Amount" }));

        RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "{Customers.Name}" });
        Assert.That(report.SortFields.Select(s => s.FieldName), Is.EqualTo(new[] { "Name" }));

        var warnings = RenderPrep.ApplyBakeTimeOverrides(report, new RuntimeOverrides { SortByFieldName = "Customers.Amount" });
        Assert.That(warnings, Has.Count.EqualTo(1), "the column exists, but not on that table");
        Assert.That(report.SortFields.Select(s => s.FieldName), Is.EqualTo(new[] { "Name" }));
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
}
