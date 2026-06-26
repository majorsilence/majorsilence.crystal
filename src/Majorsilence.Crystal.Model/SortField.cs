namespace Majorsilence.Crystal.Model;

public sealed class SortField
{
    public string FieldName { get; init; } = string.Empty;
    public SortDirection Direction { get; init; } = SortDirection.Ascending;
}

public enum SortDirection { Ascending, Descending }
