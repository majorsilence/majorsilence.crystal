namespace Majorsilence.Crystal.Model;

/// <summary>
/// Position and size in twips (1/1440 inch), matching Crystal Reports' internal unit.
/// </summary>
public sealed record ObjectBounds(int Left, int Top, int Width, int Height)
{
    public double LeftInches => Left / 1440.0;
    public double TopInches => Top / 1440.0;
    public double WidthInches => Width / 1440.0;
    public double HeightInches => Height / 1440.0;
}
