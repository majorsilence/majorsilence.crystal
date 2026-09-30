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
/// One known gap, documented rather than silently mishandled: a multi-table Crystal
/// report's single flattened RDL <c>DataSet1</c> means <see cref="Data"/> must already be
/// one joined/flattened table when the source report spans more than one Crystal table,
/// and subreport-owned table data has no push mechanism in the underlying render engine
/// today, so there is no subreport-table-data override.
/// </summary>
public sealed class RuntimeOverrides
{
    /// <summary>
    /// Data for the report's single flattened dataset (RDL's <c>DataSet1</c>). Column
    /// names must match the Crystal report's raw column names (e.g. "Customer ID", not
    /// the sanitized "Customer_ID") — the underlying render engine matches by the RDL
    /// field's <c>DataField</c> value. Null means "render with no data" (structure and
    /// static content only).
    /// </summary>
    public DataTable? Data { get; set; }

    /// <summary>Parameter name (Crystal's, e.g. without the leading '?') -> value.</summary>
    public Dictionary<string, object?> Parameters { get; set; } = [];

    /// <summary>
    /// Subreport name -> (parameter name -> value), for subreport-scoped parameters. The
    /// value is written into the parent's RDL as the literal the subreport's parameter
    /// receives, replacing the parent-field link the converter would otherwise derive.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> SubreportParameters { get; set; } = [];

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
}

public sealed class MoveObjectOverride
{
    public required string ObjectName { get; init; }
    public required MoveAxis Axis { get; init; }
    public required int Amount { get; init; }
    public bool Relative { get; init; }
}

public enum MoveAxis { Left, Top }
