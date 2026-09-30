namespace Majorsilence.Crystal.Model.Objects;

public sealed class SubreportObject : ReportObject
{
    /// <summary>Crystal's name for the placed subreport (e.g. "Subreport1").</summary>
    public string SubreportName { get; init; } = string.Empty;

    /// <summary>Index N of the "Subdocument N" OLE storage holding the inner report.</summary>
    public int SubdocumentIndex { get; init; }

    /// <summary>The parsed inner report; null if the subdocument could not be parsed.</summary>
    public ReportDefinition? Report { get; set; }

    /// <summary>
    /// Runtime values for the inner report's parameters, keyed by the parameter's declared
    /// name in <see cref="Report"/>. Written into the RDL Subreport's parameter list as
    /// literals, where they replace the parent-field binding the converter would otherwise
    /// derive for that name. The runtime equivalent of setting a subreport-scoped parameter.
    /// </summary>
    public Dictionary<string, object?> ParameterValueOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
}
