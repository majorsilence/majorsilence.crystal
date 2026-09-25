using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser.Chunks;

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

    public static ParseResult Failed(string error) =>
        new() { Success = false, Errors = [error] };
}
