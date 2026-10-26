using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model.Fields;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A Basic-syntax formula written as statements over variables it writes before reading is
/// reduced to the expression it computes (#19), through the same reduction as a
/// Crystal-syntax one. One whose variable carries a value from an earlier evaluation keeps
/// the empty value it had.
/// </summary>
[TestFixture]
public class BasicStatementsTests
{
    private static string Rdl(string formula, string? type = "String") =>
        FormulaTranspiler.ToRdlExpression(new FormulaField { Name = "F", FormulaText = formula, ResultType = type });

    // The commonest shape: a Shared variable (so that a subreport can read it), assigned,
    // appended to under conditions, and returned.
    [Test]
    public void AnAddressBuiltLineByLine_IsOneExpression()
    {
        string rdl = Rdl("""
            Shared MailingAddress as string
            ' the address block
            MailingAddress = {T.Name} + CHR(10) + {T.Address1}
            If {T.Address2} <> "" Then
                MailingAddress = MailingAddress + CHR(10) + {T.Address2}
            end if
            formula = MailingAddress
            """);

        Assert.That(rdl, Is.EqualTo(
            "=(((Fields!Name.Value + Chr(10)) + Fields!Address1.Value) + IIf((Not IsNothing(Fields!Address2.Value) And Fields!Address2.Value <> \"\"), (Chr(10) + Fields!Address2.Value), \"\"))"));
    }

    // Each "if" that only appends adds its line once, where it wrote the whole value out on
    // both paths and so doubled it: an address of seven conditional lines passed the length
    // the reduction allows and printed nothing.
    [Test]
    public void ManyConditionalLines_GrowTheExpressionByALineEach()
    {
        var formula = new System.Text.StringBuilder("Shared Label as string\nLabel = {T.Name} + chr(10)\n");
        foreach (var line in new[] { "A", "B", "C", "D", "E", "F", "G" })
            formula.Append($"if trim({{T.{line}}}) <> \"\" then\n    Label = Label + trim({{T.{line}}}) + chr(10)\nend if\n");
        formula.Append("formula = Label");

        string rdl = Rdl(formula.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(rdl, Does.StartWith("=((((((((Fields!Name.Value + Chr(10)) + IIf((Trim(Fields!A.Value) <> \"\"), (Trim(Fields!A.Value) + Chr(10)), \"\"))"));
            Assert.That(rdl, Does.Contain("IIf((Trim(Fields!G.Value) <> \"\"), (Trim(Fields!G.Value) + Chr(10)), \"\")"));
            Assert.That(rdl.Length, Is.LessThan(1000));
        });
    }

    // A value changed by more than an append keeps both paths whole: here the else path
    // replaces it, and an append whose condition compares would change meaning if regrouped.
    [Test]
    public void AReplacementOrAComparison_KeepsBothPaths()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Rdl("""
                Dim s as string
                s = {T.A}
                if {T.B} <> "" then
                    s = s + {T.B}
                else
                    s = "none"
                end if
                formula = s
                """), Does.Contain("\"none\""));
            Assert.That(Rdl("""
                Dim n as number
                n = {T.A}
                if {T.B} > 0 then
                    n = n + {T.B} > 3
                end if
                formula = n
                """, "Float64"), Does.Not.Contain("+ IIf("));
        });
    }

    [Test]
    public void ADimAssignedInBothBranches_IsAConditional()
    {
        string rdl = Rdl("""
            Dim amount as number
            if {T.Kind} = "A" or {T.Kind} = "B" then
                amount = {T.Hours} * {T.Rate}
            else
                amount = {T.Hours}
            end if
            formula = amount
            """, "Float64");

        Assert.That(rdl, Is.EqualTo(
            "=IIf(((Fields!Kind.Value = \"A\") Or (Fields!Kind.Value = \"B\")), (Fields!Hours.Value * Fields!Rate.Value), Fields!Hours.Value)"));
    }

    // Several names on one declaration, VB's own numeric type name, an ElseIf chain and a
    // one-line If.
    [Test]
    public void DeclarationListsElseIfAndOneLineIfs_AreFollowed()
    {
        string rdl = Rdl("""
            Local xDay, xMonth as Double
            xDay = Day({T.When})
            xMonth = Month({T.When})
            If xMonth < 4 Then
                formula = "Q1"
            ElseIf xMonth < 7 Then
                formula = "Q2"
            Else
                formula = "H2"
            End If
            If xDay = 1 Then formula = "start"
            """);

        Assert.Multiple(() =>
        {
            Assert.That(rdl, Is.Not.EqualTo("=\"\""));
            Assert.That(rdl, Does.Contain("\"Q1\"").And.Contain("\"Q2\"").And.Contain("\"H2\"").And.Contain("\"start\""));
            Assert.That(rdl, Does.Not.Match(@"(?i)\b(xDay|xMonth|crystalFormulaResult)\b"));
        });
    }

    [TestCase("""
        Shared total as number
        total = total + {T.Qty}
        formula = total
        """, TestName = "A running total from earlier evaluations")]
    [TestCase("""
        Shared Amount as number
        If {?Style} = "0" Then Amount = 5 end if
        formula = Amount
        """, TestName = "A Shared number assigned only under a condition keeps an earlier value otherwise")]
    public void AVariableCarriedFromAnEarlierEvaluation_StaysEmpty(string formula)
    {
        Assert.That(Rdl(formula), Is.EqualTo("=\"\""));
    }

    // A field name may hold an apostrophe, which must not be taken for a comment.
    [Test]
    public void AnApostropheInAFieldName_IsNotAComment()
    {
        string rdl = Rdl("""
            Dim s as string
            s = {Customer.Last Year's Sales}
            formula = s
            """);

        Assert.That(rdl, Does.Contain("Last_Year_s_Sales").Or.Contain("Last Year"));
    }
}
