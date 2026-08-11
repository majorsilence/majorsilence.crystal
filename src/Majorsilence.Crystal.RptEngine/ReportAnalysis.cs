namespace Majorsilence.Crystal.RptEngine;

/// <summary>
/// Read-only metadata about a parsed .rpt, shaped to mirror the sibling CrystalCmd
/// solution's <c>CrystalReportsAnalyzer</c> introspection output (parameters, tables,
/// subreports recursively, report object positions/text) — but sourced directly from
/// the already-parsed <c>Majorsilence.Crystal.Model.ReportDefinition</c>, with no render
/// step needed.
/// </summary>
public sealed class ReportAnalysis
{
    public required IReadOnlyList<string> Parameters { get; init; }

    /// <summary>Parameter name -> this model's <c>ParameterField.DataType</c> string.</summary>
    public required IReadOnlyDictionary<string, string> ParametersExtended { get; init; }

    public required IReadOnlyList<DataTableAnalysis> DataTables { get; init; }
    public required IReadOnlyList<SubreportAnalysis> Subreports { get; init; }
    public required IReadOnlyList<ReportObjectAnalysis> ReportObjects { get; init; }
}

public sealed class DataTableAnalysis
{
    public required string TableName { get; init; }
    public required IReadOnlyList<string> ColumnNames { get; init; }
}

public sealed class SubreportAnalysis
{
    public required string SubreportName { get; init; }
    public required IReadOnlyList<string> Parameters { get; init; }
    public required IReadOnlyList<DataTableAnalysis> DataTables { get; init; }
}

public sealed class ReportObjectAnalysis
{
    public required string ObjectName { get; init; }
    public required int Width { get; init; }
    public required int TopPosition { get; init; }

    /// <summary>Literal text for a TextObject; otherwise the object's model type name.</summary>
    public required string ObjectValue { get; init; }
}
