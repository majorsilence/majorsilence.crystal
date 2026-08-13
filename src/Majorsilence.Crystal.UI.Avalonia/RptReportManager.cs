using Avalonia.Controls;

namespace Majorsilence.Crystal.UI.Avalonia;

/// <summary>
/// Static entry point mirroring <c>ReportManager.ShowReport</c> in
/// TownSuiteTypes.UserInterface.Winforms.Reports — collapsed to the one backend this
/// repo has (no runtime-type dispatch needed, since there's only ever an
/// <see cref="RptReportViewer"/>).
/// </summary>
public static class RptReportManager
{
    /// <summary>Opens and shows a window immediately; the report loads asynchronously afterward.</summary>
    public static Window ShowReport(RptReportDocument document, string? title = null)
    {
        var viewer = new RptReportViewer();
        var window = new Window
        {
            Title = title ?? "Report",
            Width = 900,
            Height = 700,
            Content = viewer
        };

        window.Show();
        _ = viewer.SetReportAsync(document);
        return window;
    }

    /// <summary>Loads the report first, then opens and shows the window.</summary>
    public static async Task<Window> ShowReportAsync(RptReportDocument document, string? title = null)
    {
        var viewer = new RptReportViewer();
        await viewer.SetReportAsync(document);

        var window = new Window
        {
            Title = title ?? "Report",
            Width = 900,
            Height = 700,
            Content = viewer
        };
        window.Show();
        return window;
    }
}
