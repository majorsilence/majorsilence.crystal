namespace Majorsilence.Crystal.Model.Objects;

public sealed class FieldObject : ReportObject
{
    /// <summary>Name of the ReportField this object renders.</summary>
    public string FieldName { get; init; } = string.Empty;
}
