namespace Majorsilence.Crystal.Model.Objects;

public sealed class SubreportObject : ReportObject
{
    /// <summary>Crystal's name for the placed subreport (e.g. "Subreport1").</summary>
    public string SubreportName { get; init; } = string.Empty;

    /// <summary>Index N of the "Subdocument N" OLE storage holding the inner report.</summary>
    public int SubdocumentIndex { get; init; }

    /// <summary>The parsed inner report; null if the subdocument could not be parsed.</summary>
    public ReportDefinition? Report { get; set; }
}
