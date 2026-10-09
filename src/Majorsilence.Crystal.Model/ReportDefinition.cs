using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;

namespace Majorsilence.Crystal.Model;

public sealed class ReportDefinition
{
    public string ReportTitle { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string ReportComments { get; init; } = string.Empty;
    public int CrVersion { get; init; }

    /// <summary>
    /// BCP 47 tag chosen to carry the number separators the file records, emitted as RDL's
    /// report-level Language. It is a separator carrier, not a claim about where the report
    /// is from: .NET substitutes the culture's own separators for "," and "." in a format
    /// string, so a report whose numbers use "." for thousands and "," for decimals can only
    /// be formatted correctly by naming a culture that spells them that way. Null when the
    /// file records no separators to go on.
    /// </summary>
    public string? Language { get; init; }

    public PageLayout Page { get; set; } = new();
    public List<DataSource> DataSources { get; init; } = [];

    /// <summary>
    /// How the report's tables are joined, in the order the links apply. Empty for a report
    /// that reads one table, and for one whose links the file does not record.
    /// </summary>
    public List<TableLink> TableLinks { get; init; } = [];

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

    /// <summary>
    /// The template prints on its printer's default paper. Crystal then takes the page from
    /// whichever printer the report is formatted against, so <see cref="WidthTwips"/> and
    /// <see cref="HeightTwips"/> are only the page the designer's printer had.
    /// </summary>
    public bool PaperFromPrinter { get; init; }
}

public enum PageOrientation { Portrait, Landscape }
