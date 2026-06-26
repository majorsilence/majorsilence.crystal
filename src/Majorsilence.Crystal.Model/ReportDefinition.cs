using Majorsilence.Crystal.Model.Fields;

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

    public string? RecordSelectionFormula { get; init; }
    public string? GroupSelectionFormula { get; init; }

    public List<ReportDefinition> Subreports { get; init; } = [];
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
