namespace Majorsilence.Crystal.Model.Fields;

public sealed class ParameterField : ReportField
{
    public string PromptText { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public bool AllowMultipleValues { get; init; }
    public string? DefaultValue { get; init; }
}
