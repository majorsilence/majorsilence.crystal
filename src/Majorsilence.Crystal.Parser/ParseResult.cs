using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser.Chunks;
using Majorsilence.Crystal.Parser.Sections;

namespace Majorsilence.Crystal.Parser;

public sealed class ParseResult
{
    public ReportDefinition? Report { get; init; }
    public bool Success { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];

    /// <summary>All TSLV records decoded from the Contents stream, for diagnostics.</summary>
    public List<TslvRecord> RawChunks { get; init; } = [];

    /// <summary>
    /// How many rows the main report was saved with, or null when it was saved without data
    /// or the count cannot be confirmed. See <see cref="SavedData.SavedRecordsIndex"/>.
    /// </summary>
    public int? SavedRowCount { get; init; }

    /// <summary>
    /// The parsed QueryEngine session, when the file has a <c>QESession</c> stream. When
    /// <see cref="Sections.QeSessionRecord.DecryptionSucceeded"/> is true,
    /// <see cref="Sections.QeSessionRecord.DecryptedPayload"/> is the inflated TSLV
    /// dependency graph — the data source, tables and links for multi-table reports (#36).
    /// </summary>
    public QeSessionRecord? QeSession { get; init; }

    public static ParseResult Failed(string error) =>
        new() { Success = false, Errors = [error] };
}
