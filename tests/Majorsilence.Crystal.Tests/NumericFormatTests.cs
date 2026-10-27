using System.Globalization;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// What a numeric format record prints, value by value. Each expectation is what the Crystal
/// runtime printed for the same options, read from its PDF export of copies of a public
/// report with one option changed at a time: the space after a positive or zero value is a
/// real space glyph in Crystal's text, written here as the no-break space the format uses.
/// </summary>
[TestFixture]
public class NumericFormatTests
{
    private const string Nbsp = " ";

    private static RptParser.NumericFormatRecord Record(int negative = 1, int symbol = 0,
        int position = 0, string currency = "$", int decimals = 2, bool leadingZero = true,
        bool suppressZero = false, string? zeroText = null) =>
        new(decimals, negative, Grouping: true, leadingZero, suppressZero, symbol, position,
            ",", ".", currency, zeroText);

    private static string Print(RptParser.NumericFormatRecord record, double value,
        bool machineFormats = false, bool reserveRoom = true) =>
        value.ToString(RptParser.BuildNumericFormat(record, machineFormats, reserveRoom),
            CultureInfo.GetCultureInfo("en-US"));

    // Bracketed, with the symbol floating outside the brackets: Price (SRP)'s format.
    [TestCase(14.5, "$14.50" + Nbsp)]
    [TestCase(-14.5, "$(14.50)")]
    [TestCase(0.0, "$0.00" + Nbsp)]
    public void Bracketed_LeavesRoomForTheBracket(double value, string expected) =>
        Assert.That(Print(Record(negative: 3, symbol: 2, position: 1), value), Is.EqualTo(expected));

    // A leading minus leaves no room, symbol or none: a receipt's tendered amount ends flush.
    [TestCase(0.0, 2, "$0.00")]
    [TestCase(-53.9, 2, "-$53.90")]
    [TestCase(1234.5, 0, "1,234.50")]
    public void LeadingMinus_LeavesNoRoom(double value, int symbol, string expected) =>
        Assert.That(Print(Record(negative: 1, symbol: symbol), value), Is.EqualTo(expected));

    [TestCase(3.0, "3.00" + Nbsp)]
    [TestCase(-3.0, "3.00-")]
    public void TrailingMinus_LeavesRoomForTheMinus(double value, string expected) =>
        Assert.That(Print(Record(negative: 2), value), Is.EqualTo(expected));

    // A symbol after the number takes the place the bracket would have had.
    [TestCase(3, 3.0, "3$")]
    [TestCase(3, -3.0, "(3)$")]
    [TestCase(2, -3.0, "(3$)")]
    public void TrailingSymbol_TakesTheBracketsPlace(int position, double value, string expected) =>
        Assert.That(Print(Record(negative: 3, symbol: 2, position: position, decimals: 0), value),
            Is.EqualTo(expected));

    // A stored "$" that the record does not show is not printed: an invoice list's balance.
    [Test]
    public void StoredSymbol_NotShown_IsNotPrinted() =>
        Assert.That(Print(Record(negative: 3, symbol: 0), 458.5), Is.EqualTo("458.50" + Nbsp));

    [TestCase(3, "-" + Nbsp)]
    [TestCase(1, "-")]
    public void ZeroText_PrintsInPlaceOfZero(int negative, string expected) =>
        Assert.That(Print(Record(negative: negative, zeroText: "-"), 0.0), Is.EqualTo(expected));

    [Test]
    public void SuppressIfZero_PrintsNothing() =>
        Assert.That(Print(Record(suppressZero: true), 0.0), Is.Empty);

    [Test]
    public void NoNegativeFormat_PrintsNoSign() =>
        Assert.That(Print(Record(negative: 0), -3.0), Is.EqualTo("3.00"));

    [Test]
    public void LeadingZeroOff_DropsTheZero() =>
        Assert.That(Print(Record(leadingZero: false), 0.5), Is.EqualTo(".50"));

    // The machine's formats are Windows' "-$53.90", whatever the record says.
    [TestCase(53.9, "$53.90")]
    [TestCase(-53.9, "-$53.90")]
    public void MachineFormats_LeadingMinusAndNoRoom(double value, string expected) =>
        Assert.That(Print(Record(negative: 3, symbol: 2, position: 1), value, machineFormats: true),
            Is.EqualTo(expected));

    // A cross-tab cell ends at its margin, bracketed or not.
    [Test]
    public void CrossTabCell_LeavesNoRoom() =>
        Assert.That(Print(Record(negative: 3, symbol: 2, position: 1), 230190.63, reserveRoom: false),
            Is.EqualTo("$230,190.63"));

    // The field dictionary numbers a Currency column 7: Price (SRP) is one to the runtime,
    // and Product ID, an integer, is not.
    [TestCase("Price (SRP)", true)]
    [TestCase("Product ID", false)]
    public void CurrencyColumn_IsRead(string column, bool currency)
    {
        string path = Path.GetFullPath("../../../../rpt-corpus/benbrahim777__ProductPriceList.rpt",
            AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "Corpus file not found — run scripts/download-test-rpts.sh");

        var field = RptParser.Parse(path).Report!.Fields.OfType<DatabaseField>()
            .Single(f => f.ColumnName == column);
        Assert.That(field.Currency, Is.EqualTo(currency));
    }
}
