using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model.Objects;

public sealed class FieldObject : ReportObject
{
    /// <summary>Name of the ReportField this object renders.</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>
    /// Aggregate function when this object is a Crystal summary field
    /// (field reference of the form "Sum of Table.Column"); null for plain fields.
    /// </summary>
    public AggregateFunction? SummaryFunction { get; init; }
}
