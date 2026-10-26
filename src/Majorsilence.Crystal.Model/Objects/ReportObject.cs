namespace Majorsilence.Crystal.Model.Objects;

public abstract class ReportObject
{
    public string Name { get; init; } = string.Empty;
    public ObjectBounds Bounds { get; set; } = new(0, 0, 0, 0);
    public ObjectFormat Format { get; set; } = new();

    /// <summary>
    /// Runtime override for this object's suppression, distinct from the section-level
    /// <see cref="Section.SuppressFormula"/>/<see cref="Section.Suppress"/>. Null means
    /// "no override" (fall back to whatever the section already applies); non-null wins,
    /// mirroring Crystal's <c>ReportObjects[x].ObjectFormat.EnableSuppress</c>.
    /// </summary>
    public bool? SuppressOverride { get; set; }

    /// <summary>
    /// The object's own conditional Suppress formula, in Crystal syntax: the object prints
    /// only where it is false. Crystal lets it decide whether or not the Suppress box is
    /// ticked, and it may read CurrentFieldValue, the object's own value. Null when there is
    /// none. <see cref="SuppressOverride"/>, when set, wins over it.
    /// </summary>
    public string? SuppressFormula { get; set; }
}
