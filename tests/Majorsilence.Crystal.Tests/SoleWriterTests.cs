using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A formula that assigns a carried (Shared or Global) variable on one path only reads, on
/// the other, what it left itself at an earlier evaluation. When no other formula in the
/// report or its subreports writes the variable, that is the type's default: exactly so when
/// the conditions are on parameters alone. Written by another formula as well, the other
/// path holds whatever was last left in it, which nothing here can know: a string prints
/// empty there and its own value on the paths that set it, and a number stays unknown.
/// </summary>
[TestFixture]
public class SoleWriterTests
{
    private const string Label = """
        Shared Label as string
        If {?Style} = "0" Then Label = "short" end if
        If {?Style} = "1" Then Label = "long" end if
        formula = Label
        """;

    private static ReportDefinition Report(params FormulaField[] formulas) => new()
    {
        ReportTitle = "Sole writer",
        Fields = [new DatabaseField { Name = "Name", ColumnName = "Name", TableName = "T", DataType = "String" },
                  .. formulas],
        Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
            new FieldObject { Name = "N", FieldName = "Name", Bounds = new(0, 0, 1440, 240) }] }]
    };

    private static string FieldValue(string rdl, string name)
    {
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "Field").Single(f => (string?)f.Attribute("Name") == name).Element(ns + "Value")!.Value;
    }

    [Test]
    public void AVariableOnlyThisFormulaWrites_StartsAtItsDefault()
    {
        string rdl = new RdlConverter().Convert(Report(new FormulaField { Name = "ShowLabel", FormulaText = Label, ResultType = "String" }));

        Assert.That(FieldValue(rdl, "ShowLabel"), Is.EqualTo(
            "=IIf((RTrim(Parameters!Style.Value) = \"1\"), \"long\", IIf((RTrim(Parameters!Style.Value) = \"0\"), \"short\", \"\"))"));
    }

    [Test]
    public void AStringAnotherFormulaAlsoWrites_PrintsWhatThisOneSets()
    {
        string rdl = new RdlConverter().Convert(Report(
            new FormulaField { Name = "ShowLabel", FormulaText = Label, ResultType = "String" },
            new FormulaField { Name = "SetLabel", FormulaText = "Shared Label as string\nLabel = \"set\"\nformula = Label", ResultType = "String" }));

        Assert.That(FieldValue(rdl, "ShowLabel"), Is.EqualTo(
            "=IIf((RTrim(Parameters!Style.Value) = \"1\"), \"long\", IIf((RTrim(Parameters!Style.Value) = \"0\"), \"short\", \"\"))"));
    }

    private const string Amount = """
        Shared Amount as number
        If {?Style} = "0" Then Amount = 5 end if
        formula = Amount
        """;

    private const string SetAmount = "Shared Amount as number\nAmount = 7\nformula = Amount";

    [Test]
    public void ANumberAnotherFormulaAlsoWrites_StaysUnknown()
    {
        string rdl = new RdlConverter().Convert(Report(
            new FormulaField { Name = "ShowAmount", FormulaText = Amount, ResultType = "Float64" },
            new FormulaField { Name = "SetAmount", FormulaText = SetAmount, ResultType = "Float64" }));

        Assert.That(FieldValue(rdl, "ShowAmount"), Does.Not.Contain("IIf"));
    }

    // A subreport's formula shares a Shared variable with its parent: a parent formula that
    // writes it makes the subreport's no longer the only writer.
    [Test]
    public void AParentFormulaWritingTheVariable_CountsForTheSubreport()
    {
        var sub = Report(new FormulaField { Name = "ShowLabel", FormulaText = Amount, ResultType = "Float64" });
        var parent = Report(new FormulaField { Name = "SetLabel", FormulaText = SetAmount, ResultType = "Float64" });
        parent.Sections.Add(new Section { Type = SectionType.ReportFooter, HeightTwips = 600, Objects = [
            new SubreportObject { Name = "Sub", SubreportName = "Sub", Bounds = new(0, 0, 4000, 600), Report = sub }] });

        string alone = new RdlConverter().Convert(sub);
        string inTree = new RdlConverter { Root = parent }.Convert(sub);

        Assert.Multiple(() =>
        {
            Assert.That(FieldValue(alone, "ShowLabel"), Is.Not.EqualTo("=\"\""));
            Assert.That(FieldValue(inTree, "ShowLabel"), Is.EqualTo("=\"\""));
        });
    }

    // Reading the variable before any assignment, as a running total does, still gives up:
    // there the earlier value is the point.
    [Test]
    public void ARunningTotal_StaysUnknownEvenAlone()
    {
        string rdl = new RdlConverter().Convert(Report(new FormulaField { Name = "Total",
            FormulaText = "Shared total as number\ntotal = total + 1\nformula = total", ResultType = "Float64" }));

        Assert.That(FieldValue(rdl, "Total"), Is.EqualTo("=\"\"").Or.EqualTo("=0"));
    }
}
