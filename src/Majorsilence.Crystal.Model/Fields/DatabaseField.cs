namespace Majorsilence.Crystal.Model.Fields;

public sealed class DatabaseField : ReportField
{
    public string TableName { get; set; } = string.Empty;
    public string ColumnName { get; init; } = string.Empty;
    // Settable for the same reason TableName is: RptParser.BackfillTableNamesFromFormulas
    // refines synthesized fields' types after extraction (see its numeric-usage pass).
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// A date without a time of day (Crystal's Date type). <see cref="DataType"/> is
    /// "DateTime" for it as for a date and time, which is what the engine holds it as; this
    /// says Crystal prints only the date.
    /// </summary>
    public bool DateOnly { get; init; }

    /// <summary>A time of day without a date (Crystal's Time type); see <see cref="DateOnly"/>.</summary>
    public bool TimeOnly { get; init; }
}
