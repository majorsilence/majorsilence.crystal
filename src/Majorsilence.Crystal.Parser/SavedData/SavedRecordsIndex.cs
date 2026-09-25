using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.Decryption;
using Majorsilence.Crystal.Parser.OleStorage;

namespace Majorsilence.Crystal.Parser.SavedData;

/// <summary>
/// How many rows a report was saved with, read from the index that describes its saved data.
///
/// A report saved with data carries its rows in a <c>SavedRecordsStream &lt;n&gt;l</c> stream
/// that cannot be decoded yet, but the index describing that stream can. It lives in the
/// <c>DataSourceManager &lt;n&gt;l</c> stream, which is framed and encrypted exactly as
/// <c>Contents</c> is, so the existing decryptor reads it unchanged.
///
/// Inside, one top-level tag-45 record describes the saved dataset:
/// <list type="bullet">
///   <item>its payload's big-endian Int32 at offset 6 is the row total;</item>
///   <item>after a fixed header, a sequence of tag-109 records (schema 0x0800) are batch
///   descriptors: Int32 row count, Int32 (unidentified), Int32 byte offset, Int32 byte
///   length, UInt16 count, Int32[count].</item>
/// </list>
///
/// The descriptors come in runs, each starting again at byte offset 0 and covering one
/// stream end to end; the first run covers the records stream. Within that run the
/// descriptors fall into one or more consecutive sections that each account for every row
/// once - a batch holds at most 1,000 rows, so SalesByCustomer-Grouped's 2,191 are
/// 1,000 + 1,000 + 191, and a report with a second section repeats its total there
/// (CustomFunctions' 6,997 is 1,000 x 6 + 997, then 213 x 32 + 181).
///
/// So the header's total is accepted only when the descriptors confirm it: the first run
/// must tile the records stream's bytes exactly and divide into whole sections that each
/// sum to the total. That holds for 172 of the 173 reports carrying saved data across a
/// 88-file public corpus, a 114-file third-party one and a 2,324-file private one. The
/// exception's descriptors neither tile nor sum, and it reads as unknown rather than as a
/// number.
/// </summary>
public static class SavedRecordsIndex
{
    private const int TagDataset    = 45;
    private const int TagBatch      = 109;
    private const int BatchSchema   = 0x0800;
    private const int BatchMinBytes = 18;
    private const int RowTotalOffset = 6;

    /// <summary>
    /// The number of rows the report was saved with, or null when it was saved without data
    /// or its index does not confirm a total. Covers the main report only; a subreport keeps
    /// its own saved data under its <c>Subdocument</c> storage.
    /// </summary>
    public static int? ReadRowCount(OleReader ole)
    {
        var top = ole.EnumerateEntries().Where(e => !e.IsStorage).ToList();
        var records = top.FirstOrDefault(e => e.Path.StartsWith("SavedRecordsStream", StringComparison.Ordinal));
        var index = top.FirstOrDefault(e => e.Path.StartsWith("DataSourceManager", StringComparison.Ordinal));
        if (records is null || index is null) return null;

        List<TslvRecord> recs;
        try
        {
            recs = TslvReader.ReadAll(ContentDecryptor.Decrypt(ole.ReadStreamAt(index.Path)));
        }
        catch (Exception)
        {
            return null;
        }

        var dataset = recs.FirstOrDefault(r => r.Tag == TagDataset);
        if (dataset is null || dataset.Data.Length < RowTotalOffset + 4) return null;

        return ConfirmedRowCount(dataset.ReadInt32BE(RowTotalOffset), Descriptors(dataset), records.Length);
    }

    /// <summary>
    /// The batch descriptors in a tag-45 payload, in order. They follow a header whose length
    /// is not decoded, so reading starts at the first offset that frames as a descriptor.
    /// </summary>
    internal static List<TslvRecord> Descriptors(TslvRecord dataset)
    {
        var d = dataset.Data;
        for (int k = 0; k + 2 <= d.Length; k++)
        {
            List<TslvRecord> from;
            try { from = TslvReader.ParseDecoded(d[k..]); }
            catch (Exception) { continue; }
            if (from.Count == 0 || !IsDescriptor(from[0])) continue;
            return from.Where(IsDescriptor).ToList();
        }
        return [];
    }

    private static bool IsDescriptor(TslvRecord r) =>
        r.Tag == TagBatch && r.Schema == BatchSchema && r.Data.Length >= BatchMinBytes;

    /// <summary>
    /// The header's row total, if the descriptors confirm it: the first run tiles the records
    /// stream exactly and splits into whole sections that each sum to the total.
    /// </summary>
    internal static int? ConfirmedRowCount(int headerTotal, IReadOnlyList<TslvRecord> descriptors,
        long recordsStreamLength)
    {
        if (headerTotal <= 0 || descriptors.Count == 0) return null;

        // The first run ends where a later descriptor starts again at byte 0.
        int runLength = 1;
        while (runLength < descriptors.Count && descriptors[runLength].ReadInt32BE(8) != 0) runLength++;

        long bytes = 0, rows = 0;
        int sections = 0;
        for (int i = 0; i < runLength; i++)
        {
            var batch = descriptors[i];
            if (batch.ReadInt32BE(8) != bytes) return null;
            bytes += batch.ReadInt32BE(12);

            rows += batch.ReadInt32BE(0);
            if (rows == headerTotal) { rows = 0; sections++; }
            else if (rows > headerTotal) return null;
        }

        return bytes == recordsStreamLength && rows == 0 && sections > 0 ? headerTotal : null;
    }
}
