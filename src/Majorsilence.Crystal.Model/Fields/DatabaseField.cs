namespace Majorsilence.Crystal.Model.Fields;

public sealed class DatabaseField : ReportField
{
    public string TableName { get; set; } = string.Empty;
    public string ColumnName { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
}
