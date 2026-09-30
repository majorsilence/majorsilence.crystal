using System.Collections;
using System.Globalization;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Runtime;

namespace Majorsilence.Crystal.RptEngine.Tests;

/// <summary>
/// The rules a caller's parameter values are read by, one test each, so the two backends
/// a host might route to read one request the same way.
/// </summary>
[TestFixture]
public class ParameterCoercionTests
{
    private static ReportDefinition Report(params ParameterField[] parameters) => new()
    {
        ReportTitle = "Params",
        Fields = [.. parameters]
    };

    private static ParameterField P(string name, string type, string? defaultValue = "x") =>
        new() { Name = name, DataType = type, DefaultValue = defaultValue };

    private static (IDictionary Values, IReadOnlyList<string> Warnings) Coerce(ReportDefinition r, params (string, object?)[] supplied)
    {
        var (values, warnings) = ParameterCoercion.Coerce(r, supplied.ToDictionary(s => s.Item1, s => s.Item2));
        return (values ?? new Hashtable(), warnings);
    }

    [TestCase(true, true)]
    [TestCase("true", true)]
    [TestCase("False", false)]
    [TestCase("1", true)]
    [TestCase("0", false)]
    [TestCase("", false)]
    [TestCase(1, true)]
    public void Boolean_FromTrueFalseOrZeroOne(object raw, bool expected)
    {
        var (values, warnings) = Coerce(Report(P("Active", "Boolean")), ("Active", raw));
        Assert.That(values["Active"], Is.EqualTo(expected));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void Number_IsAnIntegerWhenItIsOne_AndADecimalOtherwise()
    {
        var r = Report(P("N", "Float64"));
        Assert.That(Coerce(r, ("N", "42")).Values["N"], Is.EqualTo(42).And.TypeOf<int>());
        Assert.That(Coerce(r, ("N", "42.5")).Values["N"], Is.EqualTo(42.5m).And.TypeOf<decimal>());
        Assert.That(Coerce(r, ("N", 7)).Values["N"], Is.EqualTo(7).And.TypeOf<int>());
        Assert.That(Coerce(r, ("N", 7.25)).Values["N"], Is.EqualTo(7.25m).And.TypeOf<decimal>());
        Assert.That(Coerce(r, ("N", "")).Values["N"], Is.EqualTo(0));
    }

    [Test]
    public void Number_ParsesInvariantFirst_ThenTheCurrentCulture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var r = Report(P("N", "Currency"));
            Assert.That(Coerce(r, ("N", "1.5")).Values["N"], Is.EqualTo(1.5m), "invariant wins even under a comma culture");
            Assert.That(Coerce(r, ("N", "1,5")).Values["N"], Is.EqualTo(1.5m), "a comma decimal falls through to the culture, not 15");
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Test]
    public void Date_TakenAsGiven_ThenIso_ThenInvariant_ThenCulture_AndBlankIsToday()
    {
        var r = Report(P("D", "DateTime"));
        var given = new DateTime(2020, 5, 6, 7, 8, 9);
        Assert.That(Coerce(r, ("D", given)).Values["D"], Is.EqualTo(given));
        Assert.That(Coerce(r, ("D", "2020-05-06T07:08:09")).Values["D"], Is.EqualTo(given));
        Assert.That(Coerce(r, ("D", "05/06/2020")).Values["D"], Is.EqualTo(new DateTime(2020, 5, 6)), "invariant reads month first");
        Assert.That(Coerce(r, ("D", "")).Values["D"], Is.EqualTo(DateTime.Today));

        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.That(Coerce(r, ("D", "31.12.2020")).Values["D"], Is.EqualTo(new DateTime(2020, 12, 31)), "a culture-only form still reads");
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Test]
    public void String_AsGiven_AndBlankIsASingleSpace()
    {
        var r = Report(P("S", "String"));
        Assert.That(Coerce(r, ("S", "hello")).Values["S"], Is.EqualTo("hello"));
        Assert.That(Coerce(r, ("S", "")).Values["S"], Is.EqualTo(" "), "the engine refuses an empty string");
        Assert.That(Coerce(r, ("S", null)).Values["S"], Is.EqualTo(" "));
    }

    [Test]
    public void Names_MatchExactlyFirst_ThenCaseInsensitively_AndTheKeyIsTheRdlName()
    {
        var r = Report(P("payamount", "Float64"), P("PayAmount", "String"), P("$[Cust Id]", "String"));

        var (values, warnings) = Coerce(r, ("PayAmount", "x"), ("payAMOUNT", "5"), ("cust id", "C1"));

        Assert.That(values["PayAmount"], Is.EqualTo("x"), "the exact-case match takes the string parameter");
        Assert.That(values["payamount"], Is.EqualTo(5), "the other one is found case-insensitively");
        Assert.That(values["Cust_Id"], Is.EqualTo("C1"), "keyed by the sanitized RDL name, wrapper stripped");
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void AnUnknownName_IsAWarning()
    {
        var (values, warnings) = Coerce(Report(P("A", "String")), ("Nope", "1"));
        Assert.That(values.Count, Is.EqualTo(0));
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("Nope"));
    }

    [Test]
    public void ARequiredParameterNotSupplied_GetsTheStandIn_AndAWarning()
    {
        var r = Report(P("Need", "Float64", defaultValue: null), P("HasDefault", "String", defaultValue: "d"));

        var (values, warnings) = Coerce(r);

        Assert.That(values["Need"], Is.EqualTo(0));
        Assert.That(values.Contains("HasDefault"), Is.False, "a parameter with a saved default is left to it");
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("Need").And.Contain("required"));
    }

    [Test]
    public void AValueThatCannotBeRead_IsAWarningAndTheStandIn_NotAThrow()
    {
        var r = Report(P("N", "Float64"), P("B", "Boolean"), P("D", "DateTime"));

        var (values, warnings) = Coerce(r, ("N", "abc"), ("B", "maybe"), ("D", "not a date"));

        Assert.That(values["N"], Is.EqualTo(0));
        Assert.That(values["B"], Is.EqualTo(false));
        Assert.That(values["D"], Is.EqualTo(DateTime.Today));
        Assert.That(warnings, Has.Count.EqualTo(3));
    }
}
