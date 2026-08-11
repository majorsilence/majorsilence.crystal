namespace Majorsilence.Crystal.Model;

public sealed class SortField
{
    public string FieldName { get; set; } = string.Empty;
    public SortDirection Direction { get; set; } = SortDirection.Ascending;
}

public enum SortDirection { Ascending, Descending }
