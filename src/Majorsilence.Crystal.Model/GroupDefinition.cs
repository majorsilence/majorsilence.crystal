namespace Majorsilence.Crystal.Model;

public sealed class GroupDefinition
{
    public int Level { get; init; }
    public string FieldName { get; init; } = string.Empty;
    public GroupSortOrder SortOrder { get; init; } = GroupSortOrder.Ascending;
    public GroupCondition Condition { get; init; } = GroupCondition.EachValue;
    public bool RepeatGroupHeader { get; init; }
}

public enum GroupSortOrder { Ascending, Descending, OriginalOrder, Specified }
public enum GroupCondition { EachValue, Daily, Weekly, Monthly, Quarterly, Annually }
