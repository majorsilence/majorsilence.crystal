namespace Majorsilence.Crystal.Model.Fields;

public sealed class RunningTotalField : ReportField
{
    public string DataType { get; init; } = string.Empty;
    public string SummarizedFieldName { get; init; } = string.Empty;
    public AggregateFunction Function { get; init; } = AggregateFunction.Sum;

    /// <summary>When a record is added to the total: every record, or on a change of a field or
    /// group, or where a formula is true.</summary>
    public RunningTotalCondition EvaluateOn { get; init; }

    /// <summary>The formula's text for <see cref="RunningTotalCondition.OnFormula"/>, the
    /// "Table.Column" for <see cref="RunningTotalCondition.OnChangeOfField"/>.</summary>
    public string? EvaluateCondition { get; set; }

    /// <summary>The group, numbered from 1 outermost first, for <see cref="RunningTotalCondition.OnChangeOfGroup"/>.</summary>
    public int EvaluateGroup { get; init; }

    /// <summary>When the total starts again from nothing: never, or on a change of a field or
    /// group, or where a formula is true.</summary>
    public RunningTotalCondition ResetOn { get; init; }

    /// <summary>As <see cref="EvaluateCondition"/>, for the reset.</summary>
    public string? ResetCondition { get; set; }

    /// <summary>As <see cref="EvaluateGroup"/>, for the reset.</summary>
    public int ResetGroup { get; init; }
}

/// <summary>A running total's evaluate or reset condition, as Crystal numbers them in the file.</summary>
public enum RunningTotalCondition
{
    None = 0,
    OnChangeOfField = 1,
    OnChangeOfGroup = 2,
    OnFormula = 3
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
