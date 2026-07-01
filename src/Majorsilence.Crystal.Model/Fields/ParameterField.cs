using System.Collections.Generic;

namespace Majorsilence.Crystal.Model.Fields;

public sealed class ParameterField : ReportField
{
    public string PromptText { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public bool AllowMultipleValues { get; init; }
    public string? DefaultValue { get; init; }
    /// <summary>Static pick-list entries as (Value, Label) pairs. Empty when no pick-list is defined.</summary>
    public IReadOnlyList<(string Value, string Label)> PickListValues { get; init; } = [];
}
