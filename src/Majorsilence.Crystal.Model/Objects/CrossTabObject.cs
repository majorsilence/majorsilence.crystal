using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model.Objects;

/// <summary>A Crystal cross-tab (pivot grid), mapped to an SSRS Matrix.</summary>
public sealed class CrossTabObject : ReportObject
{
    /// <summary>Row-axis group column names, outermost first.</summary>
    public List<string> RowGroupFields { get; init; } = [];

    /// <summary>Column-axis group column names, outermost first.</summary>
    public List<string> ColumnGroupFields { get; init; } = [];

    /// <summary>Summarized cells: column name + aggregate function.</summary>
    public List<CrossTabCell> Cells { get; init; } = [];
}

public sealed record CrossTabCell(string FieldName, AggregateFunction Function);
