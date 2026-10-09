using System.Data;

namespace Majorsilence.Crystal.Runtime;

/// <summary>
/// Runtime overrides applied to a parsed .rpt before it's converted and rendered —
/// the equivalent of what a caller would do to a real
/// <c>CrystalDecisions.CrystalReports.Engine.ReportDocument</c> via
/// <c>Database.Tables[x].SetDataSource</c>, <c>SetParameterValue</c>,
/// <c>DataDefinition.FormulaFields[x].Text</c>, <c>RecordSelectionFormula</c>,
/// <c>DataDefinition.SortFields[0].Field</c>, and <c>ReportObjects[x]</c>
/// suppress/resize/move/text/can-grow.
///
/// Names are matched case-insensitively, and a key that names nothing in the report is
/// skipped and reported (see <c>RenderPrep.ApplyBakeTimeOverrides</c>'s return value)
/// rather than failing the render, which is how the real engine's callers treat a bad key.
///
/// A multi-table report takes its data either as one table per Crystal table
/// (<see cref="TableData"/>), joined here with the links the file records, or as one table
/// the caller joined already (<see cref="Data"/>). A subreport takes one flattened table.
/// </summary>
public sealed class RuntimeOverrides
{
    /// <summary>
    /// Data for the report's single flattened dataset (RDL's <c>DataSet1</c>), already joined
    /// when the report reads more than one table. Column names must match the Crystal
    /// report's raw column names (e.g. "Customer ID", not the sanitized "Customer_ID"); the
    /// render engine matches by the RDL field's <c>DataField</c> value, and the qualified
    /// names the converter writes ("Customer.Customer ID") are added before the push, so
    /// bare names keep working. Null means "render with no data" (structure and static
    /// content only). Ignored when <see cref="TableData"/> is given.
    /// </summary>
    public DataTable? Data { get; set; }

    /// <summary>
    /// Table name (the alias the report uses, matched case-insensitively) -> that table's
    /// rows, one entry per Crystal table, for a report that reads several. The engine joins
    /// them with the links the file records, in link order: inner and left-outer joins on
    /// equal keys, as the Crystal runtime does. A table the report does not list is reported
    /// and skipped; a table the report lists but the caller did not push is reported and
    /// treated as empty. Leave empty to push one pre-joined table through <see cref="Data"/>.
    /// </summary>
    public Dictionary<string, DataTable> TableData { get; set; } = [];

    /// <summary>Parameter name (Crystal's, e.g. without the leading '?') -> value.</summary>
    public Dictionary<string, object?> Parameters { get; set; } = [];

    /// <summary>
    /// Subreport name -> (parameter name -> value), for subreport-scoped parameters. The
    /// value is written into the parent's RDL as the literal the subreport's parameter
    /// receives, replacing the parent-field link the converter would otherwise derive.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> SubreportParameters { get; set; } = [];

    /// <summary>
    /// Subreport name -> the data that subreport renders from, its own single flattened
    /// table, as <see cref="Data"/> is the main report's. A subreport placed more than once
    /// gets the same table each time. A table with no rows renders the subreport's no-rows
    /// state, as the main report's does.
    /// </summary>
    public Dictionary<string, DataTable> SubreportData { get; set; } = [];

    /// <summary>Formula field name (with or without the leading '@') -> replacement Crystal formula text.</summary>
    public Dictionary<string, string> FormulaFieldText { get; set; } = [];

    /// <summary>Replaces the report's whole-report row filter (Crystal formula text).</summary>
    public string? RecordSelectionFormula { get; set; }

    /// <summary>Report object name -> forced suppress (true) / forced visible (false).</summary>
    public Dictionary<string, bool> Suppress { get; set; } = [];

    /// <summary>Report object name -> whether the object may grow to fit its value.</summary>
    public Dictionary<string, bool> CanGrow { get; set; } = [];

    /// <summary>Report object name -> new width, in twips (same unit as the parsed model's Bounds).</summary>
    public Dictionary<string, int> Resize { get; set; } = [];

    /// <summary>TextObject name -> replacement literal text.</summary>
    public Dictionary<string, string> ObjectText { get; set; } = [];

    /// <summary>
    /// Report object position moves, applied in order. A move of <see cref="MoveAxis.Top"/>
    /// is kept inside the object's section, as the real engine's callers do: between 0 and
    /// the section's height less the object's height.
    /// </summary>
    public List<MoveObjectOverride> MoveObjectPosition { get; set; } = [];

    /// <summary>
    /// The field the report's first sort field is replaced with, as "Field", "Table.Field"
    /// or "{Table.Field}", resolved against the report's database fields. A report with no
    /// sort fields gets this one as its only sort, since the parsed model does not yet
    /// carry the file's own sort order; the real engine, which does, has nowhere to put it
    /// and reports an error instead.
    /// </summary>
    public string? SortByFieldName { get; set; }

    /// <summary>
    /// The paper the printer has, for a template that prints on its printer's default paper
    /// (<see cref="Model.PageLayout.PaperFromPrinter"/>). Crystal formats such a report on
    /// whatever paper the printer it is formatted against holds, so a host whose printer
    /// holds A4 prints A4; without this the page the template was designed on is used. The
    /// template's orientation is kept. A template that names its own paper is not changed.
    /// </summary>
    public PaperSize? PrinterPaper { get; set; }
}

/// <summary>A sheet of paper, portrait, in twips.</summary>
public sealed record PaperSize(int WidthTwips, int HeightTwips)
{
    public static PaperSize Letter { get; } = new(12240, 15840);
    public static PaperSize Legal { get; } = new(12240, 20160);
    public static PaperSize A4 { get; } = new(11906, 16838);
}

public sealed class MoveObjectOverride
{
    public required string ObjectName { get; init; }
    public required MoveAxis Axis { get; init; }
    public required int Amount { get; init; }
    public bool Relative { get; init; }
}

public enum MoveAxis { Left, Top }
