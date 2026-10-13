namespace Majorsilence.Crystal.Model;

/// <summary>
/// One of the report's record sorts, the order its details print in within each group. A
/// group's own sort is on <see cref="GroupDefinition"/>, not here.
/// </summary>
public sealed class SortField
{
    /// <summary>The column, or a formula's name with its leading '@'.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>The column's table, by the alias the report uses; empty for a formula.</summary>
    public string TableName { get; set; } = string.Empty;

    public SortDirection Direction { get; set; } = SortDirection.Ascending;
}

public enum SortDirection { Ascending, Descending }
