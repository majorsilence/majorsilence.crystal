namespace Majorsilence.Crystal.Model.Fields;

public sealed class RunningTotalField : ReportField
{
    public string DataType { get; init; } = string.Empty;
    public string SummarizedFieldName { get; init; } = string.Empty;
    public AggregateFunction Function { get; init; } = AggregateFunction.Sum;
    public string? EvaluateCondition { get; init; }
    public string? ResetCondition { get; init; }
}

public enum AggregateFunction
{
    Sum, Count, Average, Maximum, Minimum,
    DistinctCount, StandardDeviation, Variance
}
