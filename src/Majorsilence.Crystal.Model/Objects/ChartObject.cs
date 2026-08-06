using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model.Objects;

/// <summary>A Crystal chart/graph, mapped to an RDL &lt;Chart&gt; data region.</summary>
public sealed class ChartObject : ReportObject
{
    public string Title { get; init; } = string.Empty;

    /// <summary>Chart type; defaults to Column when the underlying byte is unrecognized.</summary>
    public ChartKind Kind { get; init; } = ChartKind.Column;

    /// <summary>
    /// Category (X) axis fields, outermost first — usually one column name (e.g.
    /// "Customer Name"), but "on change of group" charts can have multiple levels
    /// (e.g. year then month).
    /// </summary>
    public List<string> CategoryFields { get; init; } = [];

    /// <summary>Summarized value field, e.g. "Order Amount".</summary>
    public string SeriesField { get; init; } = string.Empty;

    public AggregateFunction SeriesFunction { get; init; } = AggregateFunction.Sum;
}

/// <summary>Mirrors the RDL Chart Type element's valid values (case-sensitive on emission).</summary>
public enum ChartKind
{
    Column, Bar, Line, Pie
}
