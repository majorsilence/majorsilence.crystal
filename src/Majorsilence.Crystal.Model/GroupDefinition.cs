using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model;

public sealed class GroupDefinition
{
    public int Level { get; init; }
    // Settable: a sort override on a grouped report regroups it on the new field, as Crystal does.
    public string FieldName { get; set; } = string.Empty;
    /// <summary>The group field's table, by the alias the report uses; empty for a formula.</summary>
    public string TableName { get; set; } = string.Empty;
    public GroupSortOrder SortOrder { get; init; } = GroupSortOrder.Ascending;
    public GroupCondition Condition { get; init; } = GroupCondition.EachValue;
    public bool RepeatGroupHeader { get; init; }

    /// <summary>
    /// Keep only this group's top (or bottom) N instances by a summary, in that order; null
    /// for a group that prints every instance in its own sort order.
    /// </summary>
    public GroupTopN? TopN { get; set; }
}

/// <summary>
/// A group's top or bottom N: the groups ranked by a summary of a field, the first
/// <see cref="Count"/> printed. The others are discarded or gathered under
/// <see cref="OthersName"/>; either way Crystal's report totals still include their records.
/// </summary>
public sealed class GroupTopN
{
    public int Count { get; init; }

    /// <summary>The lowest N rather than the highest.</summary>
    public bool Bottom { get; set; }

    public AggregateFunction Function { get; set; } = AggregateFunction.Sum;

    /// <summary>The summarised column, and its table by the alias the report uses.</summary>
    public string FieldName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;

    /// <summary>Whether the groups past N are left out, rather than gathered as one.</summary>
    public bool DiscardOthers { get; init; } = true;

    /// <summary>What the gathered other groups are called when they are not discarded.</summary>
    public string OthersName { get; init; } = "Others";
}

public enum GroupSortOrder { Ascending, Descending, OriginalOrder, Specified }
public enum GroupCondition { EachValue, Daily, Weekly, Monthly, Quarterly, Annually }
