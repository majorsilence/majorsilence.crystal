namespace Majorsilence.Crystal.Model.Objects;

/// <summary>
/// A line or box. Crystal can draw one from its own section down into a later one: a frame
/// around two report headers, or from a group header to its footer.
/// </summary>
public abstract class ShapeObject : ReportObject
{
    /// <summary>The section the shape ends in, when it is a later one than its own; null when it ends in its own.</summary>
    public Section? EndSection { get; set; }

    /// <summary>How far down <see cref="EndSection"/> the shape ends, in twips.</summary>
    public int EndBottomTwips { get; set; }
}

/// <summary>A drawn line; maps to an RDL &lt;Line&gt; report item.</summary>
public sealed class LineObject : ShapeObject;

/// <summary>A drawn box/frame; maps to an RDL &lt;Rectangle&gt; report item.</summary>
public sealed class BoxObject : ShapeObject;
