using System.Buffers.Binary;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.SavedData;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// The row count a report was saved with. The header carries a total and the batch
/// descriptors are the check on it, so these tests are mostly about when the check refuses.
/// </summary>
[TestFixture]
public class SavedRecordsIndexTests
{
    /// <summary>A tag-109 batch descriptor: rows, an unidentified Int32, byte offset, byte length, count.</summary>
    private static TslvRecord Batch(int rows, int offset, int length)
    {
        var data = new byte[18];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0), rows);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), offset);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), length);
        return new TslvRecord { Tag = 109, Schema = 0x0800, Data = data };
    }

    [Test]
    public void OneBatch_CoveringTheWholeStream_ConfirmsTheTotal()
    {
        // ProductPriceList: 115 rows in one batch over its 1,881-byte records stream.
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(115, [Batch(115, 0, 1881)], 1881), Is.EqualTo(115));
    }

    [Test]
    public void Batches_OfAThousandRows_SumToTheTotal()
    {
        // SalesByCustomer-Grouped: 2,191 rows in three batches that tile 23,749 bytes.
        var batches = new[] { Batch(1000, 0, 10955), Batch(1000, 10955, 10650), Batch(191, 21605, 2144) };
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(2191, batches, 23749), Is.EqualTo(2191));
    }

    [Test]
    public void ASecondSection_RepeatsTheTotal_RatherThanAddingToIt()
    {
        // Some records streams hold two consecutive sections, each covering every row once.
        // Summing every batch would report twice the rows the report has.
        var batches = new[] { Batch(540, 0, 900), Batch(260, 900, 400), Batch(800, 1300, 200) };
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(800, batches, 1500), Is.EqualTo(800));
    }

    [Test]
    public void ALaterRun_DescribesAnotherStream_AndIsNotCounted()
    {
        // A descriptor starting again at byte 0 begins the run for a sibling stream, which
        // carries the same row counts and does not tile the records stream.
        var batches = new[] { Batch(1000, 0, 13696), Batch(651, 13696, 9184), Batch(1000, 0, 3587), Batch(651, 3587, 2420) };
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(1651, batches, 22880), Is.EqualTo(1651));
    }

    [Test]
    public void Batches_ThatDoNotReachTheEndOfTheStream_AreNotTrusted()
    {
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(115, [Batch(115, 0, 1800)], 1881), Is.Null);
    }

    [Test]
    public void Batches_WithAGapBetweenThem_AreNotTrusted()
    {
        var batches = new[] { Batch(100, 0, 500), Batch(15, 520, 1361) };
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(115, batches, 1881), Is.Null);
    }

    [Test]
    public void Rows_ThatDoNotMakeWholeSections_AreNotTrusted()
    {
        // The one report in 173 whose index does not add up: a total of 126 over a single
        // batch of 29. Reported as unknown, not as either number.
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(126, [Batch(29, 0, 1849)], 1849), Is.Null);
    }

    [Test]
    public void NoTotal_IsNotARowCount()
    {
        Assert.That(SavedRecordsIndex.ConfirmedRowCount(0, [Batch(0, 0, 10)], 10), Is.Null);
    }

    // ------------------------------------------------------------------ corpus
    //
    // Every committed fixture came from exporting its report's saved data, so each one's row
    // count is an independent measurement of the number this reads.

    private static readonly string CorpusDir =
        Path.GetFullPath("../../../../rpt-corpus", AppContext.BaseDirectory);
    private static readonly string FixtureDir =
        Path.GetFullPath("../../../../reference-data", AppContext.BaseDirectory);

    [TestCase("benbrahim777__BeforeTV")]
    [TestCase("benbrahim777__Country-Region-Sort")]
    [TestCase("benbrahim777__CustomerList")]
    [TestCase("benbrahim777__Orders10k")]
    [TestCase("benbrahim777__Orders5-150")]
    [TestCase("benbrahim777__ProductPriceList")]
    [TestCase("benbrahim777__ProductPriceList-xs")]
    [TestCase("benbrahim777__SalesByCustomer-Grouped")]
    [TestCase("boyum__SampleReport")]
    public void SavedRowCount_MatchesTheCommittedFixture(string report)
    {
        string rpt = Path.Combine(CorpusDir, report + ".rpt");
        Assume.That(File.Exists(rpt), Is.True, $"corpus file not downloaded: {rpt}");

        // One header line, then one line per row - no fixture value contains a line break.
        int fixtureRows = File.ReadAllLines(Path.Combine(FixtureDir, report + ".csv"))
            .Skip(1).Count(l => l.Length > 0);

        var parsed = RptParser.Parse(rpt);
        Assert.That(parsed.SavedRowCount, Is.EqualTo(fixtureRows));
    }

    [Test]
    public void AReportSavedWithoutData_HasNoSavedRowCount()
    {
        string rpt = Path.Combine(CorpusDir, "boyum__AccountBalance.rpt");
        Assume.That(File.Exists(rpt), Is.True, $"corpus file not downloaded: {rpt}");

        var parsed = RptParser.Parse(rpt);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.SavedRowCount, Is.Null);
    }
}
