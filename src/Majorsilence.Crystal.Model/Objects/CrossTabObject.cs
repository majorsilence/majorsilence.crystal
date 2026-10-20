using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Model.Objects;

/// <summary>A Crystal cross-tab (pivot grid), mapped to an SSRS Matrix.</summary>
public sealed class CrossTabObject : ReportObject
{
    /// <summary>Row-axis group column names, outermost first.</summary>
    public List<string> RowGroupFields { get; init; } = [];

    /// <summary>Column-axis group column names, outermost first.</summary>
    public List<string> ColumnGroupFields { get; init; } = [];

    /// <summary>Summarized cells: column name + aggregate function.</summary>
    public List<CrossTabCell> Cells { get; init; } = [];

    /// <summary>
    /// Crystal's "Show Grid": lines around every cell of the grid, the corner above the row
    /// headers excepted. On by default.
    /// </summary>
    public bool ShowGrid { get; set; }

    /// <summary>
    /// Crystal's "Suppress Row Grand Totals". A row grand total is the total at the end of
    /// each row, so this hides the Total *column*, not the Total row.
    /// </summary>
    public bool SuppressRowGrandTotals { get; set; }

    /// <summary>
    /// Crystal's "Suppress Column Grand Totals": hides the Total *row* at the foot of every
    /// column.
    /// </summary>
    public bool SuppressColumnGrandTotals { get; set; }

    /// <summary>Height of the column-header band above the data, in twips; 0 when unknown.</summary>
    public int ColumnHeaderHeightTwips { get; set; }

    /// <summary>Width of the row-header band left of the data, in twips; 0 when unknown.</summary>
    public int RowHeaderWidthTwips { get; set; }

    /// <summary>
    /// The grid cells as the file lays them out: one per pairing of a row level with a
    /// column level, where level 0 is the grand total and the deepest level is the
    /// innermost group's own values. Empty when the file holds none.
    /// </summary>
    public List<CrossTabCellLayout> CellLayouts { get; init; } = [];

    /// <summary>Row-axis levels: index 0 is the grand-total level, then each group outermost first.</summary>
    public List<CrossTabAxisLevel> RowLevels { get; init; } = [];

    /// <summary>Column-axis levels: index 0 is the grand-total level, then each group outermost first.</summary>
    public List<CrossTabAxisLevel> ColumnLevels { get; init; } = [];
}

public sealed record CrossTabCell(string FieldName, AggregateFunction Function);

/// <summary>
/// One cell of the cross-tab grid. <see cref="Box"/> is its full extent - margins
/// included - in the same section-relative twips as every other object's position, so a
/// cell to the left of the data cell's column (a grand total printed first) has the smaller
/// Left.
/// </summary>
public sealed class CrossTabCellLayout
{
    public int RowLevel { get; init; }
    public int ColumnLevel { get; init; }
    public ObjectBounds Box { get; init; } = new(0, 0, 0, 0);

    /// <summary>The cell margin Crystal insets the value by; 0 with "Show Cell Margins" off.</summary>
    public int MarginLeftTwips { get; init; }
    public int MarginTopTwips { get; init; }

    /// <summary>The format of each summarized value in this cell, in <see cref="CrossTabObject.Cells"/> order.</summary>
    public List<ObjectFormat> SummaryFormats { get; init; } = [];
}

/// <summary>
/// One level of a cross-tab axis. Level 0 has no header of its own; its total label is the
/// grand total's ("Total"). A group level's label is the one its subtotals would carry.
/// </summary>
public sealed class CrossTabAxisLevel
{
    public int Level { get; init; }

    /// <summary>Format of the group-value header cell; null on level 0, which has none.</summary>
    public ObjectFormat? HeaderFormat { get; init; }

    /// <summary>Where the header cell's value sits (inside the cell margin); null on level 0.</summary>
    public ObjectBounds? HeaderBounds { get; init; }

    public string? TotalLabel { get; init; }
    public ObjectFormat? TotalLabelFormat { get; init; }

    /// <summary>
    /// The background of the totals this level prints, as "#RRGGBB", or null for none. On
    /// level 0 it is Crystal's grand-total colour: the row axis's fills the whole Total row,
    /// the column axis's the whole Total column.
    /// </summary>
    public string? TotalBackColor { get; init; }
}
