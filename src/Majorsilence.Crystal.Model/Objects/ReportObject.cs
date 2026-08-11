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
}
