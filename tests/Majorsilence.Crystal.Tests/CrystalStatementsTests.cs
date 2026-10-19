using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model.Fields;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A formula written as statements over variables of its own is reduced to the expression it
/// computes (#19), and one that depends on an earlier evaluation, loops, or uses arrays
/// keeps the empty value it had.
/// </summary>
[TestFixture]
public class CrystalStatementsTests
{
    private static string Rdl(string formula) =>
        FormulaTranspiler.ToRdlExpression(new FormulaField { Name = "F", FormulaText = formula });

    [TestCase("Local StringVar s := {T.Name}; s := s + \"!\"; s",
        TestName = "Assignments in sequence")]
    [TestCase("stringvar s := {T.Name}; if {T.Code} <> \"\" then s := s + \" (\" + {T.Code} + \")\"; s",
        TestName = "An if with no else keeps the old value on the other path")]
    [TestCase("Local NumberVar n; if {T.Qty} > 10 then (n := 2; n := n * {T.Qty};) else n := {T.Qty}; n",
        TestName = "A bracketed block and an else")]
    [TestCase("WhilePrintingRecords; Local StringVar a := {T.A}; Local StringVar b; if a = \"x\" then b := \"yes\" else if a = \"y\" then b := \"maybe\" else b := \"no\"; b",
        TestName = "An else if chain")]
    public void AStatementFormula_IsOneExpression(string formula)
    {
        string rdl = Rdl(formula);

        Assert.Multiple(() =>
        {
            Assert.That(rdl, Is.Not.EqualTo("=\"\""), "reduced, not degraded");
            Assert.That(rdl, Does.Not.Contain(":="));
            Assert.That(rdl, Does.Not.Match(@"(?i)\b(NumberVar|StringVar)\b"));
            // The variables are gone: only the fields remain.
            Assert.That(rdl, Does.Not.Match(@"(?<![!.\w])(s|n|a|b)(?![\w(])"), rdl);
        });
    }

    [TestCase("whileprintingrecords; stringvar list; trim(list)", TestName = "Reads an earlier evaluation's value")]
    [TestCase("global numbervar total; total := total + {T.Qty}", TestName = "Adds to an earlier evaluation's value")]
    [TestCase("global numbervar total := total + {T.Qty};", TestName = "Adds to an earlier evaluation's value in its own declaration")]
    [TestCase("Local NumberVar i; Local NumberVar c := 0; For i := 1 to 3 Do (c := c + i); c", TestName = "A loop")]
    [TestCase("Local StringVar Array a := Split({T.Name}, \" \"); a[1]", TestName = "An array")]
    [TestCase("stringvar s; if {T.Qty} > 1 then s := \"many\"", TestName = "Ends in an if with no else")]
    public void AFormulaItCannotReduce_StaysEmpty(string formula)
    {
        Assert.That(Rdl(formula), Is.EqualTo("=\"\""));
    }

    // The arithmetic of the reduced expression, through the transpiler's own if: an if
    // statement leaves "if c then (new) else (old)".
    [Test]
    public void TheElseLessIf_ReadsAsAConditionalExpression()
    {
        string rdl = Rdl("Local NumberVar n := 1; if {T.Qty} > 10 then n := 5; n + 1");

        Assert.That(rdl, Does.Contain("IIf(").IgnoreCase.And.Contain("Fields!Qty.Value"));
    }
}
