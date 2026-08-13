using Majorsilence.Crystal.Runtime;

namespace Majorsilence.Crystal.UI.Avalonia;

/// <summary>
/// The "report document" a caller hands to <see cref="RptReportViewer"/> or
/// <see cref="RptReportManager"/> — the analog of TownSuiteTypes.Reports'
/// <c>ReportDocument</c> abstraction, scoped to what this repo actually has: an .rpt
/// source plus the same push-model <see cref="RuntimeOverrides"/> already used by
/// <c>Majorsilence.Crystal.RptEngine</c>, so both projects share one mental model of
/// "how do I get data into a report" instead of inventing a second one for the viewer.
/// </summary>
public sealed class RptReportDocument
{
    /// <summary>Opens a fresh, readable stream over the .rpt file each time it's called.</summary>
    public required Func<Stream> OpenRpt { get; init; }

    public RuntimeOverrides Overrides { get; init; } = new();
}
