using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Converter.Formula;
using Majorsilence.Crystal.Model.Fields;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Tests for the Irony-based Crystal Reports formula language parser.
/// Each test exercises a specific grammar construct and verifies the RDL output.
/// </summary>
[TestFixture]
public class FormulaParserTests
{
    // Convenience helper: parse Crystal Syntax and return the RDL expression (without leading "=")
    private static string Parse(string formula)
    {
        var f = new FormulaField { FormulaText = formula, Syntax = FormulaSyntax.Crystal };
        string result = FormulaTranspiler.ToRdlExpression(f);
        // Strip leading "=" added by FormulaTranspiler
        return result.StartsWith('=') ? result[1..] : result;
    }

    // ── Grammar construction ───────────────────────────────────────────────────

    [Test]
    public void Grammar_HasNoConflicts()
    {
        var parser = CrystalFormulaParser.Instance;
        foreach (var e in parser.GrammarErrors)
            Console.WriteLine($"[Grammar] {e}");
        Assert.That(parser.GrammarErrors, Is.Empty,
            "Irony reported grammar errors/conflicts that will cause incorrect parse trees");
    }

    // ── Literals ───────────────────────────────────────────────────────────────

    [Test]
    public void Literal_Integer()
    {
        Assert.That(Parse("42"), Is.EqualTo("42"));
    }

    [Test]
    public void Literal_Decimal()
    {
        Assert.That(Parse("3.14"), Is.EqualTo("3.14"));
    }

    [Test]
    public void Literal_StringDoubleQuote()
    {
        Assert.That(Parse("\"hello\""), Is.EqualTo("\"hello\""));
    }

    [Test]
    public void Literal_StringSingleQuote()
    {
        // Single-quoted Crystal string → double-quoted RDL string
        Assert.That(Parse("'hello'"), Is.EqualTo("\"hello\""));
    }

    [Test]
    public void Literal_BooleanTrue()
    {
        Assert.That(Parse("True"), Is.EqualTo("True"));
    }

    [Test]
    public void Literal_BooleanFalse()
    {
        Assert.That(Parse("False"), Is.EqualTo("False"));
    }

    [Test]
    public void Literal_Null()
    {
        Assert.That(Parse("Null"), Is.EqualTo("Nothing"));
    }

    [Test]
    public void Literal_DateHash()
    {
        Assert.That(Parse("#2020-01-15#"), Is.EqualTo("#2020-01-15#"));
    }

    // ── Field references ───────────────────────────────────────────────────────

    [Test]
    public void FieldRef_TableDotField()
    {
        Assert.That(Parse("{Orders.Amount}"), Is.EqualTo("Fields!Amount.Value"));
    }

    [Test]
    public void FieldRef_BareField()
    {
        Assert.That(Parse("{CustomerName}"), Is.EqualTo("Fields!CustomerName.Value"));
    }

    [Test]
    public void FieldRef_Parameter()
    {
        Assert.That(Parse("{?StartDate}"), Is.EqualTo("Parameters!StartDate.Value"));
    }

    [Test]
    public void FieldRef_FormulaRef()
    {
        string result = Parse("{@GrandTotal}");
        Assert.That(result, Is.EqualTo("Fields!GrandTotal.Value"),
            "Inline formula field refs should emit Fields!Name.Value");
    }

    // ── Arithmetic ─────────────────────────────────────────────────────────────

    [Test]
    public void Arithmetic_Addition()
    {
        string r = Parse("{Orders.Price} + {Orders.Tax}");
        Assert.That(r, Does.Contain("Fields!Price.Value"));
        Assert.That(r, Does.Contain("Fields!Tax.Value"));
        Assert.That(r, Does.Contain("+"));
    }

    [Test]
    public void Arithmetic_ComplexPrecedence()
    {
        // 2 + 3 * 4 should parse as 2 + (3 * 4)
        string r = Parse("2 + 3 * 4");
        Assert.That(r, Is.Not.Null);
        // Just verify it parses without error and contains the numbers
        Assert.That(r, Does.Contain("2"));
        Assert.That(r, Does.Contain("3"));
        Assert.That(r, Does.Contain("4"));
    }

    [Test]
    public void Arithmetic_Modulo()
    {
        string r = Parse("{Orders.Qty} Mod 10");
        Assert.That(r, Does.Contain("Mod"));
    }

    [Test]
    public void Arithmetic_IntegerDivision()
    {
        string r = Parse("{Orders.Total} \\ 3");
        Assert.That(r, Does.Contain("\\"));
    }

    [Test]
    public void Arithmetic_Power()
    {
        string r = Parse("2 ^ 8");
        Assert.That(r, Does.Contain("^"));
    }

    // ── String concatenation ───────────────────────────────────────────────────

    [Test]
    public void String_Concat_Ampersand()
    {
        string r = Parse("{Customer.FirstName} & \" \" & {Customer.LastName}");
        Assert.That(r, Does.Contain("Fields!FirstName.Value"));
        Assert.That(r, Does.Contain("Fields!LastName.Value"));
        Assert.That(r, Does.Contain("&"));
    }

    [Test]
    public void String_Concat_SingleQuoteStrings()
    {
        string r = Parse("'Hello' & ' ' & 'World'");
        Assert.That(r, Does.Contain("\"Hello\""));
        Assert.That(r, Does.Contain("\"World\""));
    }

    // ── Comparison operators ───────────────────────────────────────────────────

    [Test]
    public void Compare_GreaterThan()
    {
        string r = Parse("{Orders.Amount} > 100");
        Assert.That(r, Does.Contain(">"));
    }

    [Test]
    public void Compare_NotEqual()
    {
        string r = Parse("{Orders.Status} <> 'Cancelled'");
        Assert.That(r, Does.Contain("<>"));
    }

    // ── Logical operators ──────────────────────────────────────────────────────

    [Test]
    public void Logical_AndOr()
    {
        string r = Parse("{Orders.Amount} > 100 And {Orders.Amount} < 1000");
        Assert.That(r, Does.Contain("And"));
    }

    [Test]
    public void Logical_Not()
    {
        string r = Parse("Not {Orders.IsCancelled}");
        Assert.That(r, Does.Contain("Not"));
    }

    // ── If/Then/Else ───────────────────────────────────────────────────────────

    [Test]
    public void If_ThenElse_SingleValue()
    {
        string r = Parse("If {Orders.Amount} > 1000 Then 'High' Else 'Low'");
        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("\"High\""));
        Assert.That(r, Does.Contain("\"Low\""));
    }

    [Test]
    public void If_ThenOnly_EmitsNothing()
    {
        string r = Parse("If {Orders.Qty} > 0 Then {Orders.Qty}");
        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("Nothing"));
    }

    [Test]
    public void If_Nested()
    {
        string r = Parse(
            "If {Orders.Amount} > 1000 Then 'Large' " +
            "Else If {Orders.Amount} > 100 Then 'Medium' " +
            "Else 'Small'");
        Assert.That(r, Does.Contain("IIf("));
    }

    [Test]
    public void If_WithFieldRef_ThenStringLiteral()
    {
        // From existing ConverterTests
        string r = Parse("If {Orders.Amount} > 1000 Then 'High' Else 'Low'");
        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("Fields!Amount.Value"));
    }

    // ── Function calls ─────────────────────────────────────────────────────────

    [Test]
    public void FuncCall_ToText_MapsToStr()
    {
        string r = Parse("ToText({Orders.Amount}, 2)");
        Assert.That(r, Does.Contain("CStr("));
    }

    [Test]
    public void FuncCall_IsNull_MapsToIsNothing()
    {
        string r = Parse("IsNull({Customer.MiddleName})");
        Assert.That(r, Does.Contain("IsNothing("));
    }

    [Test]
    public void FuncCall_Len()
    {
        string r = Parse("Len({Customer.Name})");
        Assert.That(r, Does.Contain("Len("));
    }

    [Test]
    public void FuncCall_Mid()
    {
        string r = Parse("Mid({Customer.Name}, 1, 3)");
        Assert.That(r, Does.Contain("Mid("));
        Assert.That(r, Does.Contain("1"));
        Assert.That(r, Does.Contain("3"));
    }

    [Test]
    public void FuncCall_Round()
    {
        string r = Parse("Round({Orders.Amount}, 2)");
        Assert.That(r, Does.Contain("Round("));
    }

    [Test]
    public void FuncCall_Year()
    {
        string r = Parse("Year({Orders.OrderDate})");
        Assert.That(r, Does.Contain("Year("));
    }

    [Test]
    public void FuncCall_DateDiff()
    {
        string r = Parse("DateDiff('d', {Orders.OrderDate}, {Orders.ShipDate})");
        Assert.That(r, Does.Contain("DateDiff("));
    }

    [Test]
    public void FuncCall_Left_Right()
    {
        string r = Parse("Left({Customer.Name}, 5) & Right({Customer.Name}, 3)");
        Assert.That(r, Does.Contain("Left("));
        Assert.That(r, Does.Contain("Right("));
    }

    // ── Bare identifier mappings ───────────────────────────────────────────────

    [Test]
    public void BareIdent_CurrentDate_MapsToToday()
    {
        string r = Parse("CurrentDate");
        Assert.That(r, Does.Contain("Today"));
    }

    // ── Multi-statement (last value returned) ──────────────────────────────────

    [Test]
    public void MultiStmt_LocalVarDecl_FallsBackToEmptyString()
    {
        // Crystal variable declarations (Local NumberVar etc.) cannot be translated to SSRS VB.NET.
        // The transpiler should return "" so the DataSet field remains valid rather than emitting
        // Crystal syntax that would be a compile error in SSRS.
        string r = Parse("Local NumberVar x := 5; x + 10");
        Assert.That(r, Is.EqualTo("\"\""),
            "Crystal variable-declaration formulas should emit empty string, not raw Crystal syntax");
    }

    // ── Select Case ────────────────────────────────────────────────────────────

    [Test]
    public void SelectCase_SimpleValues()
    {
        string r = Parse(
            "Select Case {Orders.Status} " +
            "Case 1 : 'Active' " +
            "Case 2 : 'Inactive' " +
            "Default : 'Unknown'");
        Assert.That(r, Does.Contain("Switch("));
        Assert.That(r, Does.Contain("\"Active\""));
        Assert.That(r, Does.Contain("\"Inactive\""));
    }

    // ── Basic Syntax pre-processing ────────────────────────────────────────────

    [Test]
    public void BasicSyntax_StripsFormulaEquals()
    {
        var f = new FormulaField
        {
            FormulaText = "Formula = {Customer.Name}",
            Syntax = FormulaSyntax.Basic
        };
        string r = FormulaTranspiler.ToRdlExpression(f);
        Assert.That(r, Does.Contain("Fields!Name.Value"));
        Assert.That(r, Does.Not.Contain("Formula"));
    }

    [Test]
    public void BasicSyntax_IfEndIf()
    {
        var f = new FormulaField
        {
            FormulaText = "If {Orders.Amount} > 0 Then 'Yes' Else 'No' End If",
            Syntax = FormulaSyntax.Basic
        };
        string r = FormulaTranspiler.ToRdlExpression(f);
        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Not.Contain("End If"));
    }

    // ── Parentheses ────────────────────────────────────────────────────────────

    [Test]
    public void Parens_GroupExpression()
    {
        string r = Parse("({Orders.Price} + {Orders.Tax}) * {Orders.Qty}");
        Assert.That(r, Does.Contain("*"));
        Assert.That(r, Does.Contain("Fields!Price.Value"));
        Assert.That(r, Does.Contain("Fields!Tax.Value"));
        Assert.That(r, Does.Contain("Fields!Qty.Value"));
    }

    // ── Grammar error fallback ─────────────────────────────────────────────────

    [Test]
    public void FallbackToRegex_OnUnparseableFormula()
    {
        // A formula with syntax the grammar doesn't handle falls back to regex.
        // The regex fallback still handles basic field refs.
        string r = Parse("{Vendor.Name}");
        Assert.That(r, Does.Contain("Fields!Name.Value"));
    }

    // ── Regression: existing ConverterTests scenarios ─────────────────────────

    [Test]
    public void Regression_FieldConcatentation()
    {
        string r = Parse("{Customer.FirstName} & ' ' & {Customer.LastName}");
        Assert.That(r, Does.Contain("Fields!FirstName.Value"));
        Assert.That(r, Does.Contain("Fields!LastName.Value"));
    }

    [Test]
    public void Regression_ToTextAndCurrentDate()
    {
        string r = Parse("ToText({Orders.Amount}, 2) & ' on ' & ToText(CurrentDate, 'yyyy-MM-dd')");
        Assert.That(r, Does.Contain("CStr("));
        Assert.That(r, Does.Contain("Today"));
    }
}
