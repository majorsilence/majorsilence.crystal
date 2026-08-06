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
    DistinctCount, StandardDeviation, Variance,

    /// <summary>
    /// Crystal's "Percentage of Total" summary — the prefix is a compound
    /// "Percentage of &lt;Function&gt; of Table.Column" (the inner function is
    /// discarded during parsing; RDL emission always divides by the DataSet-wide
    /// sum regardless of the inner function, since Crystal's optional custom
    /// "divide by" summary field isn't otherwise distinguishable here).
    /// </summary>
    Percentage
}
