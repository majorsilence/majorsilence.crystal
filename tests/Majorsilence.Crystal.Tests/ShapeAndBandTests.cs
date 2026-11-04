using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Lines and boxes drawn as Crystal draws them, a single Details band that columns cannot
/// hold written free-form, and a running total in its own number format. The box and line
/// values are the Crystal runtime's own (BoxObject / LineObject properties for each file).
/// </summary>
[TestFixture]
public class ShapeAndBandTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static string Corpus(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus/{name}", AppContext.BaseDirectory);

    private static string External(string name) =>
        Path.GetFullPath($"../../../../rpt-corpus-external/{name}", AppContext.BaseDirectory);

    private static ReportDefinition Report(string path)
    {
        Assume.That(File.Exists(path), Is.True, $"{Path.GetFileName(path)} not found — run scripts/download-test-rpts.sh");
        var result = RptParser.Parse(path);
        Assert.That(result.Success, Is.True);
        return result.Report!;
    }

    private static IEnumerable<ReportObject> Objects(ReportDefinition report) => report.Sections.SelectMany(s => s.Objects);

    [Test]
    public void ABox_KeepsItsFill_LineColourAndWidth()
    {
        // The runtime: FillColor #E0E0E0, LineColor #808080, LineThickness 30, SingleLine.
        var box = Objects(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))).OfType<BoxObject>().Single();

        Assert.That(box.Format.BackColor, Is.EqualTo("#E0E0E0"));
        Assert.That(box.Format.BorderColor, Is.EqualTo("#808080"));
        Assert.That(box.Format.BorderWidthTwips, Is.EqualTo(30));
        Assert.That(new[] { box.Format.BorderLeft, box.Format.BorderRight, box.Format.BorderTop, box.Format.BorderBottom },
            Is.All.EqualTo((byte)1));
    }

    [Test]
    public void AColourWhoseFlagIsFF_IsStillAColour()
    {
        // The runtime: the banner box FillColor #5F3A1F with no line, the rules LineColor
        // #998C2C, 30 twips. The file stores them FF 1F 3A 5F and FF 2C 8C 99, flag first.
        var objects = Objects(Report(External("meridian_projects__19_employee_directory.rpt"))).ToList();
        var banner = objects.OfType<BoxObject>().Single();
        var rule = objects.OfType<LineObject>().First();

        Assert.Multiple(() =>
        {
            Assert.That(banner.Format.BackColor, Is.EqualTo("#5F3A1F"));
            Assert.That(new[] { banner.Format.BorderLeft, banner.Format.BorderRight, banner.Format.BorderTop, banner.Format.BorderBottom },
                Is.All.EqualTo((byte)0));
            Assert.That(rule.Format.BorderColor, Is.EqualTo("#998C2C"));
            Assert.That(rule.Format.BorderWidthTwips, Is.EqualTo(30));
        });
    }

    [Test]
    public void ABox_IsDrawnFilled_InItsOwnColourAndWidth()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))));
        // Not counting the copy of the Details that prints when there are no rows.
        var style = doc.Descendants(Rdl + "Rectangle").Where(r => !r.Attribute("Name")!.Value.EndsWith("_NoRows"))
            .Select(r => r.Element(Rdl + "Style"))
            .Single(s => (string?)s?.Element(Rdl + "BackgroundColor") == "#E0E0E0")!;

        Assert.That((string?)style.Element(Rdl + "BorderColor")?.Element(Rdl + "Default"), Is.EqualTo("#808080"));
        Assert.That((string?)style.Element(Rdl + "BorderWidth")?.Element(Rdl + "Default"), Is.EqualTo("1.5pt"));
    }

    [Test]
    public void ALine_IsDrawnFlat_AlongItsLongSide()
    {
        // Crystal draws lines only across or down the page; a line whose bounds are 2.95pt
        // tall was drawn as a diagonal.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__ChinaOrders-RunningTotals.rpt"))));
        var lines = doc.Descendants(Rdl + "Line").ToList();

        Assert.That(lines, Is.Not.Empty);
        Assert.That(lines.All(l => (string?)l.Element(Rdl + "Height") == "0pt" || (string?)l.Element(Rdl + "Width") == "0pt"), Is.True);
    }

    [Test]
    public void ASingleDetailsBandColumnsCannotHold_IsWrittenFreeForm_WithItsLabels()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(External("worrall__CountriesAndCapitolCitiesOfTheWorld.rpt"))));
        var details = doc.Descendants(Rdl + "Details").Single();
        var values = details.Descendants(Rdl + "Value").Select(v => v.Value).ToList();

        // The card's labels are text objects, which the one-row column layout dropped.
        Assert.That(values, Does.Contain("COUNTRY"));
        Assert.That(details.Descendants(Rdl + "BackgroundColor").Any(c => c.Value == "#E0E0E0"), Is.True, "the card's box");
    }

    // The runtime's SQLExpressionFieldDefinition for each: name, value type and text.
    [TestCase("rpt-corpus/benbrahim777__USA-Orders-RWB.rpt", "%Amount", "Currency", "{Orders.Order Amount}")]
    [TestCase("rpt-corpus-external/meridian_probes__24_sql_expression_view.rpt", "%NetDays", "Float64",
        "({invoice.due_date} - {invoice.invoice_date})")]
    public void ASqlExpression_IsReadAsTheFormulaItAmountsTo(string file, string name, string type, string formula)
    {
        var report = Report(Path.GetFullPath($"../../../../{file}", AppContext.BaseDirectory));
        var field = report.Fields.OfType<Majorsilence.Crystal.Model.Fields.FormulaField>().Single(f => f.Name == name);

        Assert.That(field.ResultType, Is.EqualTo(type));
        Assert.That(field.FormulaText, Is.EqualTo(formula));
    }

    [Test]
    public void AGroupFooter_PrintsWhatIsPlacedInIt_NotAnInventedTotal()
    {
        // USA-Orders-RWB's state footer holds the group name and %Amount, nothing summing
        // Order Amount; a Sum was written under that column anyway.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__USA-Orders-RWB.rpt"))));
        var footer = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!;
        var values = footer.Descendants(Rdl + "Value").Select(v => v.Value).ToList();

        Assert.That(values, Does.Contain("=Fields!_Amount.Value"));
        Assert.That(values.Any(v => v.Contains("Sum(")), Is.False);
    }

    [Test]
    public void NewPageBeforeAGroup_BreaksBetweenGroups_NotBeforeTheFirst()
    {
        // Crystal prints the first state under the report's headers; a break at the start of
        // every group put it on page 2. Its New Page Before is "Not OnFirstRecord", read on
        // each group's first record.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__USA-Orders-Pct-colored.rpt"))));
        var grouping = doc.Descendants(Rdl + "TableGroup").First().Element(Rdl + "Grouping")!;
        var condition = doc.Descendants(Rdl + "Field").Single(f => (string?)f.Attribute("Name") == "CrystalNewPageGroup1");

        Assert.That((string?)grouping.Element(Rdl + "PageBreakAtStart"), Is.EqualTo("true"));
        Assert.That((string?)grouping.Element(Rdl + "PageBreakCondition"), Is.EqualTo("=Fields!CrystalNewPageGroup1.Value"));
        Assert.That((string?)condition.Element(Rdl + "Value"), Is.EqualTo("=Not ((RowNumber(Nothing) = 1))"));
    }

    [Test]
    public void ABandsColourFormula_ColoursItsRows()
    {
        // ServiceContract's item bands: "IF Recordnumber MOD 2 <> 0 THEN crSilver ELSE
        // crNocolor", over a fixed silver that painted every row.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("boyum__ServiceContract.rpt"))));
        var colours = doc.Descendants(Rdl + "Details").Descendants(Rdl + "BackgroundColor").Select(c => c.Value).ToList();

        Assert.That(colours, Is.Not.Empty);
        Assert.That(colours, Has.All.Contains("Silver").And.All.Contains("Transparent").And.All.Contains("Mod"));
    }

    [Test]
    public void DataDateAndDataTime_PrintTheirValues_NotTheirNames()
    {
        var rdl = new RdlConverter().Convert(Report(Corpus("boyum__ServiceContract.rpt")));

        Assert.That(rdl, Does.Not.Contain("[Data Date]"));
        Assert.That(rdl, Does.Not.Contain("[Data Time]"));
    }

    [Test]
    public void UnderlayFollowingSections_IsRead_AndTakesNoSpace()
    {
        // The runtime: Top5USAunderlay's first report header (TSection4) underlays the
        // sections after it, and no other section does.
        var report = Report(Corpus("benbrahim777__Top5USAunderlay.rpt"));
        var underlaid = report.Sections.Where(s => s.Underlay).ToList();

        Assert.That(underlaid, Has.Count.EqualTo(1));
        Assert.That(underlaid[0].Type, Is.EqualTo(SectionType.ReportHeader));
        Assert.That(underlaid[0].Objects.OfType<SubreportObject>().Any(), Is.True, "the one holding the France subreport");

        // The details table starts below the report headers that take space - the second,
        // 1,350 twips - not below the underlaid one as well.
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var top = doc.Descendants(Rdl + "Table").Single(t => (string?)t.Attribute("Name") == "Table1").Element(Rdl + "Top");
        Assert.That((string?)top, Is.EqualTo("67.5pt"));
    }

    // A statement printed from its group footers, its details suppressed: an underlaid
    // footer holding two crests, and three footers drawn over it, each a block of lines
    // laid out across the page. The hidden detail column starts 4,000 twips in.
    private static ReportDefinition StatementReport() => new()
    {
        Fields =
        [
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Account", ColumnName = "Account", DataType = "String" },
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account" }],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = true,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(4000, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 9000, Underlay = true,
                Objects =
                [
                    new TextObject { Name = "TopCrest", Text = "CREST-TOP", Bounds = new(4400, 500, 1400, 1200) },
                    new TextObject { Name = "LowCrest", Text = "CREST-LOW", Bounds = new(4400, 6500, 1400, 1200) }
                ] },
            StatementBlock("Head", 3000),
            StatementBlock("Charges", 3000),
            StatementBlock("Stub", 3000)
        ]
    };

    private static Section StatementBlock(string name, int height) => new()
    {
        Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = height,
        Objects =
        [
            new TextObject { Name = name + "Title", Text = name.ToUpperInvariant(), Bounds = new(300, 100, 3000, 300) },
            new TextObject { Name = name + "Note", Text = name + " note", Bounds = new(8000, 600, 3000, 240) }
        ]
    };

    private static XElement Row(XDocument doc, string textboxName) =>
        doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == textboxName)
            .Ancestors(Rdl + "TableRow").First();

    private static string? Top(XDocument doc, string textboxName) =>
        (string?)doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == textboxName)
            .Element(Rdl + "Top");

    private static XElement Band(XDocument doc, string textboxName) =>
        doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == textboxName)
            .Ancestors(Rdl + "Rectangle").Last();

    private static string? ZIndex(XElement item) => (string?)item.Element(Rdl + "ZIndex");

    private static string? DrawBeneath(XElement item) =>
        (string?)item.Element(Rdl + "Custom")?.Element(Rdl + "DrawBeneath");

    // The footers after an underlaid one print over it, from its top: it is a row of no
    // height, its band drawn beneath them and as tall as the section, which is where the
    // group's next footer or group starts no higher than.
    [Test]
    public void AnUnderlaidGroupFooter_IsARowOfNoHeight_DrawnBeneathTheFootersAfterIt()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(StatementReport()));
        var footerRows = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!
            .Descendants(Rdl + "TableRow").ToList();
        var crests = doc.Descendants(Rdl + "Textbox").Where(t => ((string?)t.Attribute("Name"))!.EndsWith("Crest")).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(footerRows.Select(r => (string?)r.Element(Rdl + "Height")),
                Is.EqualTo(new[] { "0pt", "150pt", "150pt", "150pt" }));
            Assert.That(Row(doc, "TopCrest"), Is.SameAs(footerRows[0]));
            Assert.That(Row(doc, "LowCrest"), Is.SameAs(footerRows[0]));
            Assert.That(ZIndex(Band(doc, "TopCrest")), Is.EqualTo("-1"));
            Assert.That(DrawBeneath(Band(doc, "TopCrest")), Is.EqualTo("true"));
            Assert.That((string?)Band(doc, "TopCrest").Element(Rdl + "Height"), Is.EqualTo("450pt"));
            // Each crest where Crystal draws it, from the underlay's top, and beneath the rest.
            Assert.That(crests.Select(c => (string?)c.Element(Rdl + "Top")), Is.EqualTo(new[] { "25pt", "325pt" }));
            Assert.That(crests.Select(ZIndex), Is.All.EqualTo("-1"));
            Assert.That(ZIndex(doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == "HeadTitle")), Is.Null);
        });
    }

    // A picture running down past the footers drawn over it, a bill's watermark behind them,
    // is drawn in the underlay's row like the rest of it. A report with no detail column lays
    // its groups out in a table of its own, and takes the underlay the same way.
    [TestCase(true)]
    [TestCase(false)]
    public void AnUnderlaidPictureAcrossFooters_IsDrawnFromItsOwnTop_BeneathTheRest(bool detailColumn)
    {
        var report = StatementReport();
        if (!detailColumn)
            report.Sections.Single(s => s.Type == SectionType.Details).Objects.Clear();
        var under = report.Sections.Single(s => s.Underlay);
        under.Objects[0] = new ImageObject { Name = "Watermark", Bounds = new(1000, 500, 6000, 4000), Source = ImageSourceKind.Embedded,
            ImageData = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="), MimeType = "image/png" };

        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var image = doc.Descendants(Rdl + "Image").Single(i => (string?)i.Attribute("Name") == "Watermark");
        var footerRows = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!
            .Descendants(Rdl + "TableRow").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(footerRows.Select(r => (string?)r.Element(Rdl + "Height")),
                Is.EqualTo(new[] { "0pt", "150pt", "150pt", "150pt" }));
            Assert.That(image.Ancestors(Rdl + "TableRow").First(), Is.SameAs(footerRows[0]));
            Assert.That((string?)image.Element(Rdl + "Top"), Is.EqualTo("25pt"));
            Assert.That(ZIndex(image), Is.EqualTo("-1"));
        });
    }

    // A grouped listing whose group header is drawn under its lines: a 4,000-twip header,
    // its heading at the top and its total at the foot, with the details printed over it.
    private static ReportDefinition HeaderUnderlayReport(bool detailsPrint = true) => new()
    {
        Fields =
        [
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Account", ColumnName = "Account", DataType = "String" },
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 4000, Underlay = true,
                Objects =
                [
                    new TextObject { Name = "Heading", Text = "HEADING", Bounds = new(300, 0, 3000, 300) },
                    new TextObject { Name = "Foot", Text = "FOOT", Bounds = new(300, 3600, 3000, 300) }
                ] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Suppress = !detailsPrint,
                Objects = [new FieldObject { Name = "Amount", FieldName = "Amount", Bounds = new(4000, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 300,
                Objects = [new TextObject { Name = "Closing", Text = "CLOSING", Bounds = new(300, 0, 3000, 300) }] }
        ]
    };

    [Test]
    public void AnUnderlaidGroupHeader_IsDrawnBeneathTheDetails()
    {
        var doc = XDocument.Parse(new RdlConverter().Convert(HeaderUnderlayReport()));
        var headerRows = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(headerRows.Select(r => (string?)r.Element(Rdl + "Height")), Is.EqualTo(new[] { "0pt" }));
            Assert.That(ZIndex(Band(doc, "Heading")), Is.EqualTo("-1"));
            Assert.That(DrawBeneath(Band(doc, "Heading")), Is.EqualTo("true"));
            Assert.That((string?)Band(doc, "Heading").Element(Rdl + "Height"), Is.EqualTo("200pt"));
            Assert.That(ZIndex(doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == "Foot")), Is.EqualTo("-1"));
            Assert.That(doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!.Descendants(Rdl + "ZIndex"), Is.Empty);
        });
    }

    // One that nothing prints over is an ordinary section, as Crystal prints it: its footer
    // and the next group print below it, as they would anyway. So does a lone Details section
    // set to underlay, each record below the last.
    [Test]
    public void AnUnderlayNothingPrintsOver_IsAnOrdinarySection()
    {
        var header = XDocument.Parse(new RdlConverter().Convert(HeaderUnderlayReport(detailsPrint: false)));
        var details = XDocument.Parse(new RdlConverter().Convert(new ReportDefinition
        {
            Fields = [new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 1200, Underlay = true,
                    Objects =
                    [
                        new TextObject { Name = "Label", Text = "Amount:", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { Name = "Amount", FieldName = "Amount", Bounds = new(0, 300, 1440, 240) }
                    ] }
            ]
        }));

        Assert.Multiple(() =>
        {
            Assert.That((string?)Row(header, "Heading").Element(Rdl + "Height"), Is.EqualTo("200pt"));
            Assert.That(header.Descendants(Rdl + "ZIndex"), Is.Empty);
            Assert.That(header.Descendants(Rdl + "DrawBeneath"), Is.Empty);
            Assert.That((string?)Row(details, "Label").Element(Rdl + "Height"), Is.EqualTo("60pt"));
            Assert.That(details.Descendants(Rdl + "ZIndex"), Is.Empty);
        });
    }

    // A page header's picture down the side of the page, the page drawn over it.
    [Test]
    public void AnUnderlaidPageHeader_IsARowOfNoHeight_InTheTablesHeader()
    {
        var report = HeaderUnderlayReport();
        report.Sections.Insert(0, new Section { Type = SectionType.PageHeader, HeightTwips = 330,
            Objects = [new TextObject { Name = "Dated", Text = "Date:", Bounds = new(2500, 0, 1200, 240) }] });
        report.Sections.Insert(0, new Section { Type = SectionType.PageHeader, HeightTwips = 13000, Underlay = true,
            Objects = [new TextObject { Name = "Banner", Text = "BANNER", Bounds = new(0, 0, 2000, 13000) }] });

        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var tableHeaderRows = doc.Descendants(Rdl + "Table").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(tableHeaderRows.Select(r => (string?)r.Element(Rdl + "Height")), Is.EqualTo(new[] { "0pt", "16.5pt" }));
            Assert.That(ZIndex(Band(doc, "Banner")), Is.EqualTo("-1"));
            Assert.That(DrawBeneath(Band(doc, "Banner")), Is.EqualTo("true"));
            Assert.That(ZIndex(Band(doc, "Dated")), Is.Null);
            Assert.That(DrawBeneath(Band(doc, "Dated")), Is.Null);
        });
    }

    [Test]
    public void AGroupFooterAcrossThePage_StartsTheTableAtItsLeft_NotAtTheHiddenColumn()
    {
        // Re-based on the hidden detail column, 4,000 twips in, everything left of it was
        // drawn at the table's edge, over what Crystal draws further right.
        var doc = XDocument.Parse(new RdlConverter().Convert(StatementReport()));
        var table = doc.Descendants(Rdl + "Table").Single(t => (string?)t.Attribute("Name") == "Table1");
        var title = doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == "HeadTitle");

        Assert.That((string?)table.Element(Rdl + "Left"), Is.EqualTo("15pt"));
        Assert.That((string?)title.Element(Rdl + "Left"), Is.EqualTo("0pt"));
        Assert.That((string?)doc.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == "HeadNote")
            .Element(Rdl + "Left"), Is.EqualTo("385pt"));
    }

    // A section-properties record (tag 255): its tag-254 flags child, then one formula-hook
    // entry naming the suppress formula when there is one. section says whether the flags
    // are a section's or an area's; visible and shown are the !suppress and !hide flags.
    private static Majorsilence.Crystal.Parser.Chunks.TslvRecord SectionProps(bool section, bool visible,
        bool shown, string? suppressFormula)
    {
        var flags = new byte[29];
        flags[4] = section ? (byte)1 : (byte)0;
        flags[6] = visible ? (byte)1 : (byte)0;
        flags[8] = shown ? (byte)1 : (byte)0;
        var data = new List<byte> { 0xA4, 0x00, 0x00, 254, 0x00, 0x00, 0x00, (byte)flags.Length };
        data.AddRange(flags.Select(b => (byte)(b ^ 254)));
        if (suppressFormula is not null)
        {
            var name = System.Text.Encoding.ASCII.GetBytes("@" + suppressFormula + "\0");
            data.AddRange([0, 0, 0, (byte)name.Length]);
            data.AddRange(name);
            data.AddRange([0, 0, 0]);
        }
        return new Majorsilence.Crystal.Parser.Chunks.TslvRecord { Tag = 255, Data = data.ToArray() };
    }

    [TestCase(true, true, "Section_Visibility", TestName = "an area that prints leaves the section its formula")]
    [TestCase(false, true, null, TestName = "an area suppressed takes the section out whatever its formula says")]
    [TestCase(true, false, null, TestName = "an area hidden takes the section out whatever its formula says")]
    public void ASectionsSuppressFormula_DecidesOnlyWhereItsAreaPrints(bool areaVisible, bool areaShown, string? expected)
    {
        var sectionProps = SectionProps(section: true, visible: true, shown: true, "Section_Visibility");
        var areaProps = SectionProps(section: false, visible: areaVisible, shown: areaShown, null);

        Assert.That(RptParser.SectionSuppressFormulaName(sectionProps, areaProps), Is.EqualTo(expected));
    }

    // A section with no formula of its own takes its area's, but its own Suppress still takes
    // it out where that formula is false: a statement's group header area has a formula over
    // two spacer sections each suppressed by its checkbox, and Crystal prints neither.
    [TestCase(true, "Area_Visibility", TestName = "a section that prints takes its area's formula")]
    [TestCase(false, null, TestName = "a section suppressed by its own checkbox stays suppressed under its area's formula")]
    public void AnAreasSuppressFormula_LeavesASuppressedSectionSuppressed(bool sectionVisible, string? expected)
    {
        var sectionProps = SectionProps(section: true, visible: sectionVisible, shown: true, null);
        var areaProps = SectionProps(section: false, visible: true, shown: true, "Area_Visibility");

        Assert.That(RptParser.SectionSuppressFormulaName(sectionProps, areaProps), Is.EqualTo(expected));
    }

    [Test]
    public void ASectionInAHiddenArea_TakesNoSuppressFormula()
    {
        // CustomerProfileReport hides its order-level group header area for drill-down; its
        // three sections carry suppress formulas, and two of them are false for one order or
        // another. Crystal prints none of them, where each formula let its section print.
        var report = Report(Corpus("souvikduttachoudhury__CustomerProfileReport.rpt"));
        var orderHeaders = report.Sections.Where(s => s.Type == SectionType.GroupHeader && s.GroupLevel == 3).ToList();

        Assert.That(orderHeaders, Has.Count.EqualTo(3));
        Assert.That(orderHeaders, Has.All.Matches<Section>(s => s.Suppress && s.SuppressFormula is null));
    }

    [Test]
    public void AGroupHeaderSummaryDrawnInAnotherColumn_KeepsItsOwnPosition()
    {
        // Top5USAunderlay's percentage is drawn at 3,600 twips beside the group name; the
        // Order Amount column it summarises starts at 5,760. Written by name into that column
        // it printed far to the right; written free-form it stays where Crystal draws it.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__Top5USAunderlay.rpt"))));
        var header = doc.Descendants(Rdl + "TableGroup").First().Element(Rdl + "Header")!;
        var percentage = header.Descendants(Rdl + "Textbox").First(t => t.Element(Rdl + "Value")!.Value.Contains("Order_Amount"));

        // Relative to the table, which starts at the group name's 480 twips: 3,600 - 480 =
        // 3,120 twips.
        Assert.That((string?)percentage.Element(Rdl + "Left"), Is.EqualTo("156pt"));
    }

    [Test]
    public void ATextObjectsParagraphs_StartNewLines()
    {
        // Each tag-192 paragraph after the first is a new line; the heading and its text
        // were run together as "...Multiple).Lorem ipsum".
        var report = Report(External("typography__paragraph_typography.rpt"));
        var text = Objects(report).OfType<TextObject>().Single(t => t.Name == "Txt1").Text;

        Assert.That(text, Does.StartWith("SingleSpacing (LineSpacing=1.0, Multiple).\nLorem ipsum"));

        // In the RDL the break is Chr(10) between quoted parts.
        Assert.That(new RdlConverter().Convert(report), Does.Contain("Multiple).\" &amp; Chr(10) &amp; \"Lorem ipsum"));
    }

    [Test]
    public void ABoxEndingInALaterSection_KnowsWhereItEnds()
    {
        // The runtime: Top5-Canadian-Customers' Box2 starts 1,320 twips into the first report
        // header, EndSectionName is the third, Bottom 72. Box1, in the page footer, ends there.
        var report = Report(Corpus("benbrahim777__Top5-Canadian-Customers.rpt"));
        var headers = report.Sections.Where(s => s.Type == SectionType.ReportHeader).ToList();
        var frame = headers[0].Objects.OfType<BoxObject>().Single();

        Assert.That(frame.EndSection, Is.SameAs(headers[2]));
        Assert.That(frame.EndBottomTwips, Is.EqualTo(72));
        Assert.That(report.Sections.Single(s => s.Type == SectionType.PageFooter)
            .Objects.OfType<BoxObject>().Single().EndSection, Is.Null);
    }

    [Test]
    public void ABoxAcrossReportHeaders_IsDrawnAPieceInEach_AndPushesNothingDown()
    {
        // Drawn whole in the first header at its record's 1,047 twips, the frame made that
        // header 737 twips taller and everything below it printed 37pt low.
        var doc = XDocument.Parse(new RdlConverter().Convert(Report(Corpus("benbrahim777__Top5-Canadian-Customers.rpt"))));
        string? Top(string name) => (string?)doc.Descendants().Single(e => (string?)e.Attribute("Name") == name).Element(Rdl + "Top");

        // The second header starts at 1,630 twips (81.5pt), the third at 2,170 (108.5pt).
        Assert.That(Top("Text3"), Is.EqualTo("87.5pt"));
        Assert.That(Top("Chart1"), Is.EqualTo("126.5pt"), "a chart takes its header's offset too");

        // 310 twips of the first header, all 540 of the second, 72 of the third; the page
        // footer's own box, filled the same, comes after them.
        var pieces = doc.Descendants(Rdl + "Rectangle")
            .Where(r => (string?)r.Element(Rdl + "Style")?.Element(Rdl + "BackgroundColor") == "#EEEEEE")
            .Select(r => (string?)r.Element(Rdl + "Height")).Take(3).ToList();
        Assert.That(pieces, Is.EqualTo(new[] { "15.5pt", "27pt", "3.6pt" }));
    }

    [Test]
    public void ATextObjectsTabs_AreKept()
    {
        // The wizard header: "Printed Date:  {Print Date}", a run that is only "\t\t", then
        // "Last modified:". The runs' trailing tabs were trimmed, so that one vanished and the
        // halves ran together; the engine now puts each tab on the next half-inch stop.
        var report = Report(Corpus("benbrahim777__Top5-Canadian-Customers.rpt"));
        var header = Objects(report).OfType<TextObject>().Single(t => t.Name == "Text2");

        Assert.That(header.Text, Does.Contain("}\t\tLast modified:"));

        // Inside an expression's quotes a tab is Chr(9), as a line break is Chr(10).
        Assert.That(new RdlConverter().Convert(report), Does.Contain("Chr(9) &amp; \"\" &amp; Chr(9) &amp; \"Last modified:"));
    }

    [Test]
    public void ARunningTotal_IsFormattedAsTheNumberItSums()
    {
        // ChinaOrders-RunningTotals' Order Total sums Order Amount, a currency column, and
        // Crystal prints it as $40.50 and $12,424.80.
        var total = Objects(Report(Corpus("benbrahim777__ChinaOrders-RunningTotals.rpt"))).OfType<FieldObject>()
            .Single(f => f.FieldName.TrimStart('#').Equals("Order Total", StringComparison.OrdinalIgnoreCase));

        Assert.That(total.Format.FormatString, Does.Contain("#,##0.00"));
    }

    // A list grouped by account: the details in two columns, a group footer holding only a
    // rule across the page, and a second group header with nothing in it, a spacer.
    private static ReportDefinition RuledListReport() => new()
    {
        Fields =
        [
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Account", ColumnName = "Account", DataType = "String" },
            new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Account" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Account", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 115 },
            new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects =
                [
                    new FieldObject { FieldName = "Account", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) }
                ] },
            new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 220,
                Objects = [new LineObject { Name = "Rule", Bounds = new(60, 120, 8000, 0) }] }
        ]
    };

    [Test]
    public void AGroupFootersRule_IsDrawn()
    {
        // A row of cells has nowhere to put a line, and the rule under each group was dropped.
        var doc = XDocument.Parse(new RdlConverter().Convert(RuledListReport()));
        var footer = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Footer")!;

        Assert.That(footer.Descendants(Rdl + "Line").Count(), Is.EqualTo(1));
        Assert.That((string?)footer.Descendants(Rdl + "TableRow").Single().Element(Rdl + "Height"), Is.EqualTo("11pt"));
    }

    [Test]
    public void AnEmptyGroupSection_TakesItsHeight()
    {
        // Crystal spaces by an empty section; with nothing to put in a cell it had no row.
        var doc = XDocument.Parse(new RdlConverter().Convert(RuledListReport()));
        var rows = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").ToList();

        Assert.That(rows.Select(r => (string?)r.Element(Rdl + "Height")), Is.EqualTo(new[] { "12pt", "5.75pt" }));
        Assert.That(rows[1].Element(Rdl + "Visibility"), Is.Null);
    }

    [Test]
    public void AnEmptyGroupSectionSuppressed_IsHidden()
    {
        var report = RuledListReport();
        report.Sections[1] = new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 115, Suppress = true };
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var spacer = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").Last();

        Assert.That((string?)spacer.Element(Rdl + "Visibility")?.Element(Rdl + "Hidden"), Is.EqualTo("true"));
    }

    [Test]
    public void AnEmptyGroupSectionThatSuppressesBlank_TakesNoRow()
    {
        var report = RuledListReport();
        report.Sections[1] = new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 115, SuppressBlank = true };
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var rows = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!.Descendants(Rdl + "TableRow");

        Assert.That(rows.Select(r => (string?)r.Element(Rdl + "Height")), Is.EqualTo(new[] { "12pt" }));
    }

    // A group header holding only a field suppressed whatever the data prints nothing, and
    // Crystal makes no group name up for it: a bill's header strip, its town address hidden,
    // printed the group's value in bold and the hidden address below it.
    [Test]
    public void AGroupHeaderOfSuppressedObjects_PrintsNoGroupName()
    {
        var report = RuledListReport();
        report.Sections[0] = new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
            Objects = [new FieldObject { FieldName = "Amount", Bounds = new(2880, 0, 1440, 240), SuppressOverride = true }] };
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var header = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!;

        Assert.That(header.Descendants(Rdl + "Value").Select(v => v.Value).Where(v => v.Length > 0), Is.Empty);
    }

    // A further group header's column labels, listed by the file in another order than their
    // columns ("Amount" before "Account"), go free-form, each where Crystal draws it. A row of
    // cells takes the objects in the order listed, and a bill's "Amount" heading printed in
    // the column beside "Issued" with the amounts far to its right.
    [Test]
    public void ColumnLabelsListedOutOfTheirColumnsOrder_KeepTheirPlaces()
    {
        var report = RuledListReport();
        report.Sections[1] = new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
            Objects = [
                new TextObject { Name = "AmountLabel", Text = "Amount", Bounds = new(2880, 0, 1440, 240) },
                new TextObject { Name = "AccountLabel", Text = "Account", Bounds = new(0, 0, 1440, 240) }] };
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var labels = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").Last().Descendants(Rdl + "Textbox").ToList();

        Assert.That(labels.Select(t => ((string?)t.Element(Rdl + "Value"), (string?)t.Element(Rdl + "Left"))),
            Is.EquivalentTo(new[] { ("Amount", "144pt"), ("Account", "0pt") }));
    }

    [Test]
    public void SuppressBlankSection_IsRead()
    {
        // The runtime: SalesByCustomer-Grouped's TSection6, 405 twips tall, suppresses itself
        // when blank, and no other section of the report does.
        var report = Report(Corpus("benbrahim777__SalesByCustomer-Grouped.rpt"));

        Assert.That(report.Sections.Where(s => s.SuppressBlank).Select(s => s.HeightTwips), Is.EqualTo(new[] { 405 }));
    }

    // A ledger grouped by account: the details in three columns, and a group header holding
    // the account at the left and its opening balance, a column the details do not show,
    // under the third.
    [Test]
    public void AGroupHeadersFieldOfNoColumn_KeepsItsPlace()
    {
        var report = new ReportDefinition
        {
            Fields =
            [
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Account", ColumnName = "Account", DataType = "String" },
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Balance", ColumnName = "Balance", DataType = "Float64" },
                new Majorsilence.Crystal.Model.Fields.DatabaseField { Name = "Opening", ColumnName = "Opening", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Account" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Account", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { Name = "OpeningField", FieldName = "Opening", Bounds = new(5760, 0, 1440, 240) }
                    ] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Account", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) },
                        new FieldObject { FieldName = "Balance", Bounds = new(5760, 0, 1440, 240) }
                    ] }
            ]
        };

        // The opening balance filled the first empty cell, under Amount; Crystal prints it
        // where it was drawn, under Balance.
        var doc = XDocument.Parse(new RdlConverter().Convert(report));
        var header = doc.Descendants(Rdl + "TableGroup").Single().Element(Rdl + "Header")!;
        var opening = header.Descendants(Rdl + "Textbox").Single(t => (string?)t.Element(Rdl + "Value") == "=Fields!Opening.Value");

        Assert.That((string?)opening.Element(Rdl + "Left"), Is.EqualTo("288pt"));
    }
}
