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

    public static ParseResult Failed(string error) =>
        new() { Success = false, Errors = [error] };
}
