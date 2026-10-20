using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model.Objects;

/// <summary>A Crystal chart/graph, mapped to an RDL &lt;Chart&gt; data region.</summary>
public sealed class ChartObject : ReportObject
{
    public string Title { get; init; } = string.Empty;

    /// <summary>Chart type; defaults to Column when the underlying byte is unrecognized.</summary>
    public ChartKind Kind { get; init; } = ChartKind.Column;

    /// <summary>
    /// Category (X) axis fields, outermost first — usually one column name (e.g.
    /// "Customer Name"), but "on change of group" charts can have multiple levels
    /// (e.g. year then month).
    /// </summary>
    public List<string> CategoryFields { get; init; } = [];

    /// <summary>Summarized value field, e.g. "Order Amount".</summary>
    public string SeriesField { get; init; } = string.Empty;

    public AggregateFunction SeriesFunction { get; init; } = AggregateFunction.Sum;

    /// <summary>
    /// How the bars, lines or areas of one category relate to each other, from the chart's
    /// subtype: side by side, stacked, or stacked to 100%. Only bar, line and area charts
    /// have a stacking; everything else is <see cref="ChartStacking.None"/>.
    /// </summary>
    public ChartStacking Stacking { get; init; } = ChartStacking.None;

    /// <summary>The category (group) axis title, empty when Crystal shows none.</summary>
    public string CategoryAxisTitle { get; init; } = string.Empty;

    /// <summary>The value (data) axis title, empty when Crystal shows none.</summary>
    public string ValueAxisTitle { get; init; } = string.Empty;

    /// <summary>Whether Crystal draws a legend, and where.</summary>
    public bool ShowLegend { get; init; }

    public ChartLegendPosition LegendPosition { get; init; } = ChartLegendPosition.Right;

    /// <summary>Major gridlines across the value axis (horizontal lines on a column chart).</summary>
    public bool ValueAxisGridLines { get; init; }

    /// <summary>Major gridlines across the category axis.</summary>
    public bool CategoryAxisGridLines { get; init; }

    public ChartFont TitleFont { get; init; } = new();
    public ChartFont CategoryAxisTitleFont { get; init; } = new();
    public ChartFont ValueAxisTitleFont { get; init; } = new();
    public ChartFont LegendFont { get; init; } = new();
    public ChartFont CategoryLabelFont { get; init; } = new();
    public ChartFont ValueLabelFont { get; init; } = new();

    /// <summary>
    /// The chart's series colours in order, as "#RRGGBB". Empty when the file does not say
    /// which colours Crystal draws. Filled from the chart's own drawing-template stream
    /// after the object itself is parsed, so it is settable.
    /// </summary>
    public List<string> Palette { get; set; } = [];

    /// <summary>
    /// Whether Crystal gives every category of a single-series chart a colour of its own and
    /// lists the categories in the legend, rather than drawing every riser in one colour.
    /// Set together with <see cref="Palette"/>.
    /// </summary>
    public bool ColorByCategory { get; set; }

    /// <summary>
    /// Which chart this is among the report's charts, in the order the report stores them
    /// (0 for the first). The chart's drawing template is a stream of its own, and the
    /// streams are numbered in this same order.
    /// </summary>
    public int ChartIndex { get; init; } = -1;
}

/// <summary>Mirrors the RDL Chart Type element's valid values (case-sensitive on emission).</summary>
public enum ChartKind
{
    Column, Bar, Line, Pie, Area, Doughnut
}

public enum ChartStacking
{
    None, Stacked, PercentStacked
}

public enum ChartLegendPosition
{
    Right, Left, Bottom
}

/// <summary>
/// One of a chart's text styles. Crystal keeps the face, weight and slant with the chart's
/// definition and the size in the chart's drawing template, where it is a fraction of the
/// chart's height; <see cref="SizePt"/> is that fraction already applied to the chart as
/// placed. Null members mean the file did not say.
/// </summary>
public sealed class ChartFont
{
    public string? Name { get; set; }
    public double? SizePt { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
}
