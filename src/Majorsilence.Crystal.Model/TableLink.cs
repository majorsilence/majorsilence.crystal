namespace Majorsilence.Crystal.Model;

/// <summary>
/// One link between two of a report's tables: the pair of fields the tables are joined on,
/// and how. Crystal records one link per field pair, so two tables linked on two fields
/// carry two links. Links are in the order the Crystal runtime applies them, which is also
/// the order a join should take them in.
/// </summary>
public sealed class TableLink
{
    /// <summary>The table the link starts from, by its alias (the name the report uses).</summary>
    public string SourceTable { get; init; } = string.Empty;
    public string SourceColumn { get; init; } = string.Empty;

    /// <summary>The table the link points to, by its alias.</summary>
    public string TargetTable { get; init; } = string.Empty;
    public string TargetColumn { get; init; } = string.Empty;

    public TableJoinType JoinType { get; init; }
    public LinkOperator Operator { get; init; }
}

/// <summary>How a <see cref="TableLink"/> joins its two tables, in Crystal's terms.</summary>
public enum TableJoinType
{
    /// <summary>Rows with a match on both sides. Crystal calls this an equal join.</summary>
    Inner,
    /// <summary>Every source row, with the target's columns empty where it has no match.</summary>
    LeftOuter,
    /// <summary>Every target row, with the source's columns empty where it has no match.</summary>
    RightOuter,
    /// <summary>Every row of both, matched where they match.</summary>
    FullOuter
}

/// <summary>The comparison a <see cref="TableLink"/> matches its two fields with.</summary>
public enum LinkOperator
{
    Equal,
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    NotEqual,
    /// <summary>A comparison this model does not name; a join treats it as <see cref="Equal"/>.</summary>
    Unknown
}
