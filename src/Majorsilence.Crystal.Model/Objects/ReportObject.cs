namespace Majorsilence.Crystal.Model.Objects;

public abstract class ReportObject
{
    public string Name { get; init; } = string.Empty;
    public ObjectBounds Bounds { get; init; } = new(0, 0, 0, 0);
    public ObjectFormat Format { get; init; } = new();
}
