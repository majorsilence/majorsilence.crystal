using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Model;

public sealed class ReportDefinition
{
    public string ReportTitle { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string ReportComments { get; init; } = string.Empty;
    public int CrVersion { get; init; }

    public PageLayout Page { get; init; } = new();
    public List<DataSource> DataSources { get; init; } = [];
    public List<ReportField> Fields { get; init; } = [];
    public List<GroupDefinition> Groups { get; init; } = [];
    public List<SortField> SortFields { get; init; } = [];
    public List<Section> Sections { get; init; } = [];

    public string? RecordSelectionFormula { get; set; }
    public string? GroupSelectionFormula { get; set; }

    /// <summary>
    /// Finds a subreport by name anywhere in the report tree (recursively, since a
    /// subreport can itself contain nested subreports). Subreports are only ever
    /// reachable via <see cref="SubreportObject.Report"/> on the objects placed in
    /// each section — there is no separate flat subreport list on the model.
    /// </summary>
    public ReportDefinition? FindSubreport(string name)
    {
        foreach (var section in Sections)
        {
            foreach (var sub in section.Objects.OfType<SubreportObject>())
            {
                if (string.Equals(sub.SubreportName, name, StringComparison.OrdinalIgnoreCase))
                    return sub.Report;

                var nested = sub.Report?.FindSubreport(name);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }
}

public sealed class PageLayout
{
    public int WidthTwips { get; init; } = 12240;
    public int HeightTwips { get; init; } = 15840;
    public int TopMarginTwips { get; init; } = 720;
    public int BottomMarginTwips { get; init; } = 720;
    public int LeftMarginTwips { get; init; } = 720;
    public int RightMarginTwips { get; init; } = 720;
    public PageOrientation Orientation { get; init; } = PageOrientation.Portrait;
}

public enum PageOrientation { Portrait, Landscape }
