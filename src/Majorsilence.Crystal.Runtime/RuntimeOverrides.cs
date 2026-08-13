using System.Data;

namespace Majorsilence.Crystal.Runtime;

/// <summary>
/// Runtime overrides applied to a parsed .rpt before it's converted and rendered —
/// the equivalent of what a caller would do to a real
/// <c>CrystalDecisions.CrystalReports.Engine.ReportDocument</c> via
/// <c>Database.Tables[x].SetDataSource</c>, <c>SetParameterValue</c>,
/// <c>DataDefinition.FormulaFields[x].Text</c>, <c>RecordSelectionFormula</c>,
/// <c>DataDefinition.SortFields[0].Field</c>, and <c>ReportObjects[x]</c>
/// suppress/resize/move/text.
///
/// Two known gaps, documented rather than silently mishandled (see the project's
/// planning notes): a multi-table Crystal report's single flattened RDL
/// <c>DataSet1</c> means <see cref="Data"/> must already be one joined/flattened
/// table when the source report spans more than one Crystal table; and subreport-owned
/// table data has no confirmed push mechanism in the underlying render engine today, so
/// <see cref="SubreportParameters"/> is honoured but there is no equivalent
/// subreport-table-data override.
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

    /// <summary>Subreport name -> (parameter name -> value), for subreport-scoped parameters.</summary>
    public Dictionary<string, Dictionary<string, object?>> SubreportParameters { get; set; } = [];

    /// <summary>Formula field name -> replacement Crystal formula text.</summary>
    public Dictionary<string, string> FormulaFieldText { get; set; } = [];

    /// <summary>Replaces the report's whole-report row filter (Crystal formula text).</summary>
    public string? RecordSelectionFormula { get; set; }

    /// <summary>Report object name -> forced suppress (true) / forced visible (false).</summary>
    public Dictionary<string, bool> Suppress { get; set; } = [];

    /// <summary>Report object name -> new width, in twips (same unit as the parsed model's Bounds).</summary>
    public Dictionary<string, int> Resize { get; set; } = [];

    /// <summary>TextObject name -> replacement literal text.</summary>
    public Dictionary<string, string> ObjectText { get; set; } = [];

    /// <summary>Report object position moves, applied in order.</summary>
    public List<MoveObjectOverride> MoveObjectPosition { get; set; } = [];

    /// <summary>
    /// Replaces the field name of the report's first sort field. Mirrors the real
    /// engine's own constraint: a report with no sort fields defined has nowhere to put
    /// this override, so it's a no-op when <c>ReportDefinition.SortFields</c> is empty.
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
