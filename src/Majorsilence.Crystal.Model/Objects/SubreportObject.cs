namespace Majorsilence.Crystal.Model.Objects;

public sealed class SubreportObject : ReportObject
{
    /// <summary>The placed subreport object's name (e.g. "Subreport1").</summary>
    public string SubreportName { get; init; } = string.Empty;

    /// <summary>
    /// The name the subreport's own report carries (e.g. "CanadaOrders.rpt"), which is what
    /// the Crystal runtime calls the subreport (<c>Subreports[name]</c>,
    /// <c>SubreportObject.SubreportName</c>) and so what a host's request names it by. Empty
    /// when the file records none; <see cref="SubreportName"/> is then the only name.
    /// </summary>
    public string ReportName { get; set; } = string.Empty;

    /// <summary>Whether a caller's name for a subreport names this one: either name, with or without ".rpt", ignoring case.</summary>
    public bool IsNamed(string name)
    {
        static string Bare(string n) => n.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
        string wanted = Bare(name.Trim());
        return string.Equals(Bare(SubreportName), wanted, StringComparison.OrdinalIgnoreCase)
            || (ReportName.Length > 0 && string.Equals(Bare(ReportName), wanted, StringComparison.OrdinalIgnoreCase));
    }

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
