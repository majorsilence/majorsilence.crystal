using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Whether a formula certainly returns a number. Most of these are refusals: the cost of
/// calling a string formula numeric is a number format applied to text, so anything the
/// checker does not fully understand must come out "not known".
/// </summary>
[TestFixture]
public class FormulaResultTypeTests
{
    private static readonly HashSet<string> NumericColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Product.Price (SRP)", "Orders.Order Amount",
    };

    private static readonly Dictionary<string, string> Formulas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discount"] = "{Product.Price (SRP)} * 0.9",
        ["Label"] = "'Price: ' & {Product.Price (SRP)}",
        ["Loop"] = "{@Loop} + 1",
    };

    private static bool IsNumeric(string text)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Formula(string name) =>
            Formulas.TryGetValue(name, out var body) && visiting.Add(name)
            && FormulaResultType.IsNumeric(body, NumericColumns.Contains, Formula);
        return FormulaResultType.IsNumeric(text, NumericColumns.Contains, Formula);
    }

    [TestCase("CCur(CDbl({Product.Price (SRP)}) * 0.90)", TestName = "AConversion_IsNumeric_WhateverItConverts")]
    [TestCase("{Product.Price (SRP)} * 0.75", TestName = "ArithmeticOnANumericColumn_IsNumeric")]
    [TestCase("({Product.Price (SRP)} - 1) / 4", TestName = "ParenthesesAndMixedOperators_AreNumeric")]
    [TestCase("-{Product.Price (SRP)}", TestName = "UnaryMinus_IsNumeric")]
    [TestCase("{Product.Price (SRP)} + {Orders.Order Amount}", TestName = "Plus_BetweenNumbers_IsNumeric")]
    [TestCase("Round({Product.Price (SRP)}, 2)", TestName = "ANumericFunction_IsNumeric")]
    [TestCase("ToNumber('12') * 3", TestName = "ANumericFunction_IgnoresAStringArgument")]
    [TestCase("Sum ({Orders.Order Amount})", TestName = "ASummary_IsNumeric")]
    [TestCase("{@Discount} * 2", TestName = "ANumericFormulaReference_IsNumeric")]
    [TestCase("42", TestName = "ANumberLiteral_IsNumeric")]
    [TestCase("{Product.Price (SRP)} mod 3", TestName = "Mod_IsAnArithmeticOperator")]
    public void Numeric(string text) => Assert.That(IsNumeric(text), Is.True);

    [TestCase("{Product.Product Name}", TestName = "ANonNumericColumn_IsNotNumeric")]
    [TestCase("{Unknown.Field} * 2", TestName = "AnUnknownColumn_IsNotNumeric")]
    [TestCase("'abc'", TestName = "AStringLiteral_IsNotNumeric")]
    [TestCase("{Product.Price (SRP)} & ' each'", TestName = "Concatenation_IsNotNumeric")]
    [TestCase("{Product.Price (SRP)} + 'x'", TestName = "Plus_WithAString_IsNotNumeric")]
    [TestCase("If {Product.Price (SRP)} > 10 Then 1 Else 0", TestName = "AConditional_IsNotKnown")]
    [TestCase("{Product.Price (SRP)} > 10", TestName = "AComparison_IsNotNumeric")]
    [TestCase("Maximum({Orders.Order Amount})", TestName = "AFunctionThatReturnsItsArgumentsType_IsNotKnown")]
    [TestCase("IIf(True, 1, 'a')", TestName = "IIf_IsNotKnown")]
    [TestCase("{Product.Price (SRP)} // a comment", TestName = "AComment_IsNotKnown")]
    [TestCase("{@Label}", TestName = "AStringFormulaReference_IsNotNumeric")]
    [TestCase("{@Loop}", TestName = "ASelfReferencingFormula_ProvesNothing")]
    [TestCase("{@Missing} * 2", TestName = "AMissingFormulaReference_IsNotNumeric")]
    [TestCase("", TestName = "AnEmptyFormula_IsNotNumeric")]
    [TestCase("Round({Product.Price (SRP)}, 2", TestName = "UnbalancedParentheses_AreNotKnown")]
    [TestCase("{Product.Price (SRP)} *", TestName = "ADanglingOperator_IsNotKnown")]
    [TestCase("{Product.Price (SRP)}[1]", TestName = "ASubscript_IsNotKnown")]
    [TestCase("formula = {Product.Price (SRP)} * 2", TestName = "BasicSyntaxAssignment_IsNotKnown")]
    public void NotNumeric(string text) => Assert.That(IsNumeric(text), Is.False);

    // ------------------------------------------------------------------ corpus
    //
    // The formats these reports' formula objects get, each checked against what the real
    // engine prints for that object.

    private static string CorpusFile(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}.rpt", AppContext.BaseDirectory);

    private static string? FormatOf(string report, string field)
    {
        string path = CorpusFile(report);
        Assume.That(File.Exists(path), Is.True, $"corpus file not downloaded: {path}");
        var parsed = RptParser.Parse(path);
        return parsed.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.FieldObject>()
            .First(f => f.FieldName == field)
            .Format?.FormatString;
    }

    // Crystal prints "$13.05 " and "$8.98 " for CCur(CDbl({Product.Price (SRP)}) * 0.90),
    // the trailing space being the one every own-format currency value carries.
    [Test]
    public void ANumericFormula_GetsItsObjectsNumericFormat() =>
        Assert.That(FormatOf("benbrahim777__TenPct-DiscountDays", "@TenPct"),
            Is.EqualTo("\"$\"#,##0.00\" \""));

    // Its record stores "kr. " and says not to show it; Crystal prints "341.326,67".
    [Test]
    public void ASymbolTheRecordHides_IsNotShown() =>
        Assert.That(FormatOf("boyum__SalesOpportunity", "@AccountSize"), Is.EqualTo("0.00"));

    // A switch returning captions carries a numeric record too, and must not be given it.
    [Test]
    public void AStringFormula_GetsNoNumericFormat() =>
        Assert.That(FormatOf("boyum__SalesOpportunity", "@Title_AccountSize"), Is.Null);
}
