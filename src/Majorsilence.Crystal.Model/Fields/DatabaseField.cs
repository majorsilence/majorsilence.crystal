namespace Majorsilence.Crystal.Model.Fields;

public sealed class DatabaseField : ReportField
{
    public string TableName { get; set; } = string.Empty;
    public string ColumnName { get; init; } = string.Empty;
    // Settable for the same reason TableName is: RptParser.BackfillTableNamesFromFormulas
    // refines synthesized fields' types after extraction (see its numeric-usage pass).
    public string DataType { get; set; } = string.Empty;
}
