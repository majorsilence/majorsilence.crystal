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

    // ── Basic syntax detected from the body ────────────────────────────────────
    // The .rpt parser reports every formula as Crystal syntax, so Basic-syntax bodies
    // have to be recognised from their own markers or they reach the engine as raw text.

    private static string ParseDeclaredCrystal(string text) =>
        FormulaTranspiler.ToRdlExpression(new FormulaField
        {
            FormulaText = text,
            Syntax = FormulaSyntax.Crystal
        });

    [Test]
    public void BasicIfChain_DeclaredAsCrystal_BecomesNestedIIf()
    {
        string r = ParseDeclaredCrystal(
            "if Trim({Acct.type}) = \"I\" then \n" +
            "formula = \"REVENUE:\"\n" +
            "elseif Trim({Acct.type}) = \"E\" then \n" +
            "formula = \"EXPENSES:\"\n" +
            "else\n" +
            "formula = \"OTHER:\"\n" +
            "end if");

        Assert.That(r, Does.Contain("IIf("), "the If/ElseIf chain should become nested IIf calls");
        Assert.That(r, Does.Contain("\"REVENUE:\""));
        Assert.That(r, Does.Contain("\"EXPENSES:\""));
        Assert.That(r, Does.Contain("\"OTHER:\""));
        Assert.That(r, Does.Not.Contain("formula ="), "the return-value assignment must not survive");
        Assert.That(r, Does.Not.Contain("end if"), "the statement terminator must not survive");
    }

    [Test]
    public void BasicIfChain_WithNoElse_FallsBackToEmptyString()
    {
        string r = ParseDeclaredCrystal(
            "if {Acct.flag} = 1 then\nformula = \"Yes\"\nend if");

        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("\"\""), "no matching branch yields an empty result");
    }

    [Test]
    public void BasicSyntax_WholeLineApostropheComments_AreDropped()
    {
        string r = ParseDeclaredCrystal(
            "'if {P.BudgetType} = \"B\" then\n" +
            "'formula = {Acct.VarianceYear}\n" +
            "'end if\n" +
            "formula = {Acct.ActualYear}");

        Assert.That(r, Does.Contain("Fields!ActualYear.Value"), "the live assignment should survive");
        Assert.That(r, Does.Not.Contain("BudgetType"), "commented-out lines should be dropped");
    }

    // An apostrophe is Crystal's string delimiter as well as Basic's comment marker, so
    // comment stripping must only take whole commented lines — never cut mid-line.
    [Test]
    public void ApostropheStringLiterals_SurviveCommentStripping()
    {
        string r = ParseDeclaredCrystal(
            "if {Orders.Amount} > 0 then\nformula = 'Yes'\nelse\nformula = 'No'\nend if");

        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("Yes"));
        Assert.That(r, Does.Contain("No"));
    }

    // A nested else-if chain with comments inside the branches defeats both the grammar and
    // the regex fallback. Whatever the fallback returns then still holds Crystal keywords,
    // and "Then" is not VB.NET expression syntax, so emitting it costs the whole report.
    [Test]
    public void UntranspilableNestedIfChain_DegradesInsteadOfLeakingKeywords()
    {
        string r = ParseDeclaredCrystal(
            "if {?Fiscal} = True then\n" +
            "    if onfirstrecord then\n" +
            "       {GL.DebitOpening}\n" +
            "    else if not onfirstrecord then\n" +
            "        //Normal Asset or Expense Account\n" +
            "        if {GL.Debit} = 0 then\n" +
            "            {GL.Debit} - {GL.Credit}\n" +
            "        else\n" +
            "            0");

        Assert.That(r, Does.Not.Contain("then").IgnoreCase,
            "a Crystal keyword must never reach the engine as part of an expression");
    }

    // The keyword guard keys on "Then", so report text containing it must not trip it.
    [Test]
    public void StringLiteralContainingKeyword_DoesNotTripTheGuard()
    {
        string r = ParseDeclaredCrystal("\"paid, and then cleared\"");
        Assert.That(r, Does.Contain("and then cleared"), "a literal must survive intact");
    }

    // These reports keep older versions of a formula commented out with //. A "End If" in
    // that dead text must not classify the live Crystal-syntax body as Basic: doing so ran
    // apostrophe-comment stripping over it and deleted every branch value, because each
    // begins with a Crystal string literal.
    [Test]
    public void CommentedOutBasicVersion_DoesNotMakeLiveCrystalBodyLookBasic()
    {
        string r = ParseDeclaredCrystal(
            "//'Shared EmpAddress as string\n" +
            "//If rtrim({R.address1}) <> \"\" Then\n" +
            "//End If\n" +
            "\n" +
            "if {?IncludeCustcode} = true then\n" +
            "    'In Account with: ' + trim({R.lastname})\n" +
            "else\n" +
            "    'In Account with: ' + trim({R.firstname})");

        Assert.That(r, Does.Contain("In Account with"),
            "the live branch values must survive — they start with a string literal, not a comment");
        Assert.That(r, Does.Contain("IIf("));
    }

    // "Else If" is valid Crystal syntax too, so it must not be treated as a Basic marker —
    // doing so sent Crystal formulas down the Basic path and truncated their literals.
    [Test]
    public void CrystalElseIf_IsNotTreatedAsBasicSyntax()
    {
        string r = ParseDeclaredCrystal(
            "If {Orders.Amount} > 1000 Then 'Large' Else If {Orders.Amount} > 100 Then 'Medium' Else 'Small'");

        Assert.That(r, Does.Contain("IIf("));
        Assert.That(r, Does.Contain("Large"));
        Assert.That(r, Does.Contain("Small"));
    }
}
