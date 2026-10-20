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
            "=IIf((Fields!Address2.Value <> \"\"), ((((Fields!Name.Value + Chr(10)) + Fields!Address1.Value) + Chr(10)) + Fields!Address2.Value), ((Fields!Name.Value + Chr(10)) + Fields!Address1.Value))"));
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
        Shared Label as string
        If {?Style} = "0" Then Label = "short" end if
        formula = Label
        """, TestName = "A Shared variable assigned only under a condition keeps an earlier value otherwise")]
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
