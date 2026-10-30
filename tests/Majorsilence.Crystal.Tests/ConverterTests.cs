using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

[TestFixture]
public class ConverterTests
{
    // Crystal lays a page out at its true height, anchors it from the bottom, and writes
    // the box truncated to whole points, so an A4 page (841.8pt) draws everything 0.8pt
    // higher than its twips say. The emulation is a whole-point page and a top margin
    // lifted by the fraction. A Letter page (792pt) has no fraction and must not move.
    //
    // The whole number is written so the engine keeps it whole: it stores sizes as parts of
    // 1/2540in and truncates the page box, so 595pt - whose nearest part falls just short -
    // is written as the first part above it, or the box would come out a point short of
    // Crystal's. 841pt, 842pt and 792pt are whole after the engine's own rounding.
    [TestCase(16836, 240, "841pt", "11.2pt", TestName = "RdlConverter_A4Page_IsLaidOutOnWholePointsAndLiftedByTheFraction")]
    [TestCase(15840, 240, "792pt", "12pt", TestName = "RdlConverter_LetterPage_IsWrittenAsIs")]
    [TestCase(16841, 240, "842pt", "11.95pt", TestName = "RdlConverter_A4VariantPage_IsLiftedByItsOwnFraction")]
    [TestCase(11904, 240, "595.0205pt", "11.8pt", TestName = "RdlConverter_A4LandscapePage_KeepsItsWholePointsThroughTheEngine")]
    public void RdlConverter_PageHeightFraction(int heightTwips, int topMarginTwips,
        string expectedHeight, string expectedTopMargin)
    {
        var report = new ReportDefinition
        {
            Page = new PageLayout { HeightTwips = heightTwips, TopMarginTwips = topMarginTwips },
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }],
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        Assert.Multiple(() =>
        {
            Assert.That(doc.Root.Element(ns + "PageHeight")!.Value, Is.EqualTo(expectedHeight));
            Assert.That(doc.Root.Element(ns + "TopMargin")!.Value, Is.EqualTo(expectedTopMargin));
            // The body's height is what paginates; it must not change.
            Assert.That(doc.Root.Element(ns + "BottomMargin")!.Value, Is.EqualTo("36pt"));
        });
    }

    [Test]
    public void RdlConverter_ProducesValidXml_ForMinimalReport()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Sales Report",
            Author = "Test",
            CrVersion = 7,
            Fields =
            [
                new DatabaseField { Name = "Orders.OrderID", TableName = "Orders", ColumnName = "OrderID", DataType = "Integer" },
                new DatabaseField { Name = "Orders.CustomerName", TableName = "Orders", ColumnName = "CustomerName", DataType = "String" },
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details,
                    HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "OrderID", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "CustomerName", Bounds = new(1440, 0, 2880, 240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("Report"));
        Assert.That(rdl, Does.Contain("DataSet1"));
        Console.WriteLine(rdl);
    }

    [Test]
    public void FormulaTranspiler_ConvertsCrystalFieldReference()
    {
        var formula = new FormulaField
        {
            Name = "FullName",
            FormulaText = "{Customer.FirstName} & ' ' & {Customer.LastName}",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Contain("Fields!FirstName.Value"));
        Assert.That(result, Does.Contain("Fields!LastName.Value"));
    }

    // Crystal compares strings as if padded with spaces: "" = " " and "ab" = "ab   " are true.
    [TestCase("Mid({?Code}, 1, 1) <> ' '", "=(RTrim(Mid(Parameters!Code.Value, 1, 1)) <> \"\")")]
    [TestCase("{T.Code} = 'AB  '", "=(RTrim(Fields!Code.Value) = \"AB\")")]
    [TestCase("{T.Code} < Trim({T.Other})", "=(RTrim(Fields!Code.Value) < Trim(Fields!Other.Value))")]
    [TestCase("{T.Amount} > 5", "=(Fields!Amount.Value > 5)")]
    public void FormulaTranspiler_StringComparisons_IgnoreTrailingSpaces(string crystal, string expected)
    {
        var formula = new FormulaField { Name = "Test", FormulaText = crystal, Syntax = FormulaSyntax.Crystal };

        Assert.That(FormulaTranspiler.ToRdlExpression(formula), Is.EqualTo(expected));
    }

    [Test]
    public void FormulaTranspiler_ConvertsIfThenElse()
    {
        var formula = new FormulaField
        {
            Name = "Status",
            FormulaText = "If {Orders.Amount} > 1000 Then 'High' Else 'Low'",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Contain("IIf("));
    }

    [Test]
    public void FormulaTranspiler_MapsCommonFunctions()
    {
        var formula = new FormulaField
        {
            Name = "Formatted",
            FormulaText = "ToText({Orders.Amount}, 2) & ' on ' & ToText(CurrentDate, 'yyyy-MM-dd')",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Contain("CStr("));
        Assert.That(result, Does.Contain("Today"));
    }

    [Test]
    public void RdlConverter_WithObjectFormat_EmitsStyleElement()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Styled Report",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details,
                    HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject
                        {
                            FieldName = "Amount",
                            Bounds = new(0, 0, 1440, 240),
                            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, Bold = false }
                        }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("FontFamily"));
        Assert.That(rdl, Does.Contain("Arial"));
        // 8.95pt, not 10pt: Crystal's size is a character-cell height and RDL's is an em.
        // See RdlConverter.EmPointsFor and the dedicated cases below.
        Assert.That(rdl, Does.Contain("8.95pt"));
        // A data row whose only object says Bold=false must emit no FontWeight at all.
        // This used to expect exactly one, because the table synthesized a bold row of
        // column names; nothing in the report asked for that row and it is no longer
        // written, so the correct expectation is none.
        int fwElements = System.Text.RegularExpressions.Regex.Matches(rdl, @"<\w+:FontWeight>|<FontWeight>").Count;
        Assert.That(fwElements, Is.EqualTo(0), "a data row with Bold=false must not emit FontWeight");
    }

    [Test]
    public void RdlConverter_WithForeColor_EmitsColorElement()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Color Report",
            Fields = [new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details,
                    HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject
                        {
                            FieldName = "Name",
                            Bounds = new(0, 0, 1440, 240),
                            Format = new ObjectFormat { ForeColor = "#800000" }
                        }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("<Color>#800000</Color>").Or.Contain(":Color>#800000<"));
    }

    private static readonly string CorpusDir =
        Path.GetFullPath("../../../../rpt-corpus", AppContext.BaseDirectory);

    [Test]
    public void RdlConverter_CorpusFile_ProducesValidXml()
    {
        if (!Directory.Exists(CorpusDir))
            Assert.Ignore("Corpus directory not found — skipping round-trip test");

        var file = Directory.GetFiles(CorpusDir, "*.rpt").FirstOrDefault();
        if (file is null)
            Assert.Ignore("No corpus .rpt files found");

        var result = RptParser.Parse(file);
        Assume.That(result.Success, Is.True);

        var converter = new RdlConverter();
        string rdl = converter.Convert(result.Report!);

        Console.WriteLine(rdl[..Math.Min(2000, rdl.Length)]);

        Assert.That(rdl, Does.Contain("<Report"));
        Assert.That(rdl, Does.Contain("DataSet"));
        // DB fields should be present
        var dbFields = result.Report!.Fields.OfType<Majorsilence.Crystal.Model.Fields.DatabaseField>().ToList();
        foreach (var f in dbFields)
            Assert.That(rdl, Does.Contain(SanitizeName(f.ColumnName)));
        // Font info should appear if FieldObjects were found
        if (result.Report.Sections.Any(s => s.Objects.OfType<FieldObject>().Any(fo => fo.Format.FontName is not null)))
            Assert.That(rdl, Does.Contain("FontFamily"));
    }

    private static IEnumerable<TestCaseData> ConverterCorpusFiles()
    {
        if (!Directory.Exists(CorpusDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.rpt", SearchOption.TopDirectoryOnly))
            yield return new TestCaseData(f).SetName(Path.GetFileName(f));
    }

    [Test]
    [TestCaseSource(nameof(ConverterCorpusFiles))]
    public void RdlConverter_CorpusFile_AllProduceValidXml(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True, $"Parser failed for {Path.GetFileName(rptPath)}");

        var converter = new RdlConverter();
        string rdl = string.Empty;
        Assert.DoesNotThrow(() => rdl = converter.Convert(result.Report!),
            $"Converter threw for {Path.GetFileName(rptPath)}");

        // Basic structure checks
        Assert.That(rdl, Does.Contain("<Report"), "Missing <Report> element");
        Assert.That(rdl, Does.Contain("</Report>"), "Missing </Report> closing tag");

        // Validate as well-formed XML
        Assert.DoesNotThrow(() =>
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(rdl);
        }, "Output should be well-formed XML");

        Console.WriteLine($"  RDL length: {rdl.Length} chars");
    }

    // A grouped report shaped like SalesByCustomer-Grouped, which is the measured case
    // behind these four assertions: a caption at the very left of the band, two column
    // labels further across at their own offsets, and a detail band whose first column
    // starts well right of the caption. Every number below is that report's own geometry.
    private static ReportDefinition GroupBandReport() => new()
    {
        ReportTitle = "Sales By Customer",
        Fields =
        [
            new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
            new DatabaseField { Name = "Date", ColumnName = "Date", DataType = "DateTime" }
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
        Sections =
        [
            // A page footer at the band's left edge is what puts a lead column on this
            // table: the table has to start left of its first data column to hold it.
            new Section { Type = SectionType.PageFooter, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] },
            new Section { Type = SectionType.GroupHeader, HeightTwips = 405, GroupLevel = 0,
                Objects =
                [
                    // The caption: 60 twips in, where the first data column starts at 532,
                    // and 124 twips down a 405-twip band.
                    new FieldObject { FieldName = "Customer", Bounds = new(60, 124, 11340, 300),
                        Format = new ObjectFormat { FontName = "Arial", FontSize = 10.0, Bold = true,
                            ForeColor = "#000080" } },
                    // A label 1,122 twips into the column that starts at 3558. Its
                    // "underline" is a bottom border on the label itself, which is what a
                    // Crystal column heading's rule always is.
                    new TextObject { Text = "Order Amount", Bounds = new(4680, 124, 1444, 237),
                        Format = new ObjectFormat { BorderBottom = 1, BorderWidthTwips = 20 } },
                    // And one that happens to start exactly on its column.
                    new TextObject { Text = "Date", Bounds = new(7080, 120, 1207, 237),
                        Format = new ObjectFormat { BorderBottom = 1, BorderWidthTwips = 20 } }
                ] },
            new Section { Type = SectionType.Details, HeightTwips = 289,
                Objects =
                [
                    new FieldObject { FieldName = "Customer", Bounds = new(532, 15, 3026, 255) },
                    new FieldObject { FieldName = "Amount", Bounds = new(3558, 15, 2554, 255) },
                    new FieldObject { FieldName = "Date", Bounds = new(7080, 0, 1748, 274) }
                ] },
            // The subtotal is boxed and filled, as Crystal draws it: a border on all four
            // sides and a grey background, both belonging to the object and not to the
            // column it happens to sit in.
            new Section { Type = SectionType.GroupFooter, HeightTwips = 423, GroupLevel = 0,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(4320, 120, 2600, 263),
                    Format = new ObjectFormat { BorderLeft = 1, BorderRight = 1, BorderTop = 1,
                        BorderBottom = 1, BorderWidthTwips = 20, BackColor = "#C0C0C0" } }] }
        ]
    };

    private static System.Xml.Linq.XElement GroupHeaderRow(System.Xml.Linq.XDocument doc,
        System.Xml.Linq.XNamespace ns) =>
        doc.Descendants(ns + "TableGroup").First()
            .Descendants(ns + "Header").First()
            .Descendants(ns + "TableRow").First();

    // A group band's own cells hold the fields matching a detail column; anything else in
    // the band went into a queue of extras, and a field was never put in that queue. So a
    // formula placed in a group footer, which matches no detail column, was dropped and the
    // footer printed nothing in its place.
    [Test]
    public void RdlConverter_AFormulaInAGroupFooter_IsWritten()
    {
        var report = GroupBandReport();
        report.Fields.Add(new FormulaField { Name = "Note", FormulaText = "\"GROUP-NOTE\"" });
        report.Sections.Single(s => s.Type == SectionType.GroupFooter).Objects
            .Add(new FieldObject { Name = "NoteField", FieldName = "@Note", Bounds = new(8000, 120, 1440, 240) });

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var footer = doc.Descendants(ns + "TableGroup").First().Element(ns + "Footer")!;

        Assert.That(footer.Descendants(ns + "Value").Select(v => v.Value), Has.Some.EqualTo("=Fields!Note.Value"));
    }

    // The other half: a field the band's own cell writes is not also queued, which would
    // print it twice, or lift it out of the table where no Fields! reference resolves.
    [Test]
    public void RdlConverter_AGroupFooterFieldWithAColumn_IsWrittenOnce()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var amounts = doc.Descendants(ns + "Value").Count(v => v.Value == "=Sum(Fields!Amount.Value)");

        Assert.That(amounts, Is.EqualTo(1));
    }

    // Crystal's default group caption IS the group's own field, so the caption is very
    // often a FieldObject and not a TextObject. Only the TextObject case was read, and with
    // no text object in the band's first column the caption fell back to a synthesized
    // { Bold = true } - the right words in a font the report never asked for. On
    // SalesByCustomer-Grouped that rendered the customer name 14% narrower than the real
    // engine draws it, which had been mistaken for a font-metrics disagreement.
    [Test]
    public void RdlConverter_GroupCaptionFromAFieldObject_KeepsThatObjectsFont()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var caption = GroupHeaderRow(doc, ns).Descendants(ns + "Textbox").First();
        var style = caption.Element(ns + "Style")!;

        Assert.That(caption.Element(ns + "Value")!.Value, Is.EqualTo("=Fields!Customer.Value"));
        Assert.Multiple(() =>
        {
            Assert.That(style.Element(ns + "FontFamily")?.Value, Is.EqualTo("Arial"));
            Assert.That(style.Element(ns + "Color")?.Value, Is.EqualTo("#000080"));
            Assert.That(style.Element(ns + "FontWeight")?.Value, Is.EqualTo("Bold"));
            // 10pt of Crystal cell height is 8.75pt of em in Arial Bold, 8.95 in regular
            // Arial - see EmPointsFor.
            Assert.That(style.Element(ns + "FontSize")?.Value, Is.EqualTo("8.75pt"));
        });
    }

    // The lead column is not padding when the group caption is the thing that reaches into
    // it. Writing an empty lead cell unconditionally pushed the caption into the first data
    // column, a third of an inch right of the report's own position and indented past the
    // detail rows it captions. It spans both columns because a caption confined to the lead
    // column would be clipped to the lead column's width.
    [Test]
    public void RdlConverter_GroupCaptionLeftOfTheFirstColumn_SpansTheLeadColumn()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var cells = GroupHeaderRow(doc, ns).Descendants(ns + "TableCell").ToList();

        // Three cells over four columns: the caption's two, plus one for each label.
        Assert.That(cells, Has.Count.EqualTo(3));
        Assert.That(cells[0].Element(ns + "ColSpan")?.Value, Is.EqualTo("2"),
            "the caption covers the lead column and the first data column");
        // 60 twips from the table's left edge, which is where the lead column starts.
        Assert.That(Framed(cells[0], ns).Element(ns + "Left")!.Value, Is.EqualTo("3pt"),
            "and sits at its own Left inside them");
    }

    /// <summary>
    /// The item a band cell puts its content in: a Textbox at the object's own bounds
    /// inside a Rectangle that fills the cell. Fails the cell rather than returning
    /// something else when the cell holds a bare Textbox instead.
    /// </summary>
    private static System.Xml.Linq.XElement Framed(System.Xml.Linq.XElement cell,
        System.Xml.Linq.XNamespace ns) =>
        cell.Elements(ns + "ReportItems").Single().Elements(ns + "Rectangle").Single()
            .Elements(ns + "ReportItems").Single().Elements(ns + "Textbox").Single();

    // A group band's objects do not define this table's columns - the detail band does - so
    // a label here starts somewhere inside the column that contains it rather than at that
    // column's left edge, and the band is one row as tall as the whole section with each
    // object at its own Top in it. Written as plain cells they were all flushed to their
    // column's start and to the top of the row.
    [TestCase(1, "56.1pt", "6.2pt", "72.2pt", TestName = "a label 1,122 twips into its column")]
    [TestCase(2, "0pt", "6pt", "60.35pt", TestName = "a label already on its column's start")]
    public void RdlConverter_GroupLabel_SitsWhereTheReportDrewItInsideItsColumn(
        int cellIndex, string expectedLeft, string expectedTop, string expectedWidth)
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var label = Framed(GroupHeaderRow(doc, ns).Descendants(ns + "TableCell").ToList()[cellIndex], ns);

        Assert.Multiple(() =>
        {
            Assert.That(label.Element(ns + "Left")!.Value, Is.EqualTo(expectedLeft));
            Assert.That(label.Element(ns + "Top")!.Value, Is.EqualTo(expectedTop));
            Assert.That(label.Element(ns + "Width")!.Value, Is.EqualTo(expectedWidth),
                "the label's own width, not the column's");
            // 237 twips of label, and 30 more below it: the label is underlined, and Crystal
            // draws a border 2pt outside the object's bounds on the side that has one. Its
            // Left, Top and Width stay the label's own, because only the bottom has a border.
            Assert.That(label.Element(ns + "Height")!.Value, Is.EqualTo("13.35pt"));
        });
    }

    // What the report draws as a column heading's underline is a bottom border on the label
    // object, and a border belongs to whatever item carries it. A band cell used to be a
    // bare Textbox filling its column, so the two labels' 305px and 256px rules came out as
    // one continuous 1,098px rule across the band - the column's width, not the label's.
    // Padding cannot fix that: it insets the text and leaves the frame where it was.
    [Test]
    public void RdlConverter_GroupLabelsUnderline_IsTheLabelsOwnWidthAndNotTheColumns()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        // The item the border is written on, for every cell that has one: its Style's
        // grandparent, which is the Textbox.
        var bordered = GroupHeaderRow(doc, ns).Descendants(ns + "TableCell")
            .Where(c => c.Descendants(ns + "BorderStyle").Any())
            .Select(c => c.Descendants(ns + "BorderStyle").Single().Parent!.Parent!)
            .ToList();

        Assert.That(bordered, Has.Count.EqualTo(2), "both column labels are underlined");
        // The columns here are 3,522 and 1,748 twips wide; the labels are 1,444 and 1,207.
        // A border on a box filling the cell would measure the former pair, and a box
        // filling a cell has no Width of its own at all.
        Assert.That(bordered.Select(b => b.Element(ns + "Width")?.Value),
            Is.EqualTo(new[] { "72.2pt", "60.35pt" }));
        Assert.That(bordered.Select(b => b.Parent!.Parent!.Name.LocalName),
            Is.EqualTo(new[] { "Rectangle", "Rectangle" }),
            "the border is on the object's own box inside the cell, not on one filling it");
    }

    // The group footer is the same band problem, and its subtotal shows the other half of
    // it: Crystal fills the object's box grey, where a cell background fills the column.
    // Its cells are matched to columns by field name rather than by position, so the frame
    // only applies where the object really does sit in the column it was matched to - a
    // summary drawn somewhere else entirely would otherwise be pushed by a distance that
    // measures nothing.
    [Test]
    public void RdlConverter_GroupFooterSubtotal_IsBoxedAndFilledAtItsOwnSize()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var summaryCell = doc.Descendants(ns + "TableGroup").First()
            .Descendants(ns + "Footer").First()
            .Descendants(ns + "TableCell")
            .First(c => c.Descendants(ns + "Value").Any(v => v.Value.Contains("Sum(Fields!Amount.Value)")));
        var summary = Framed(summaryCell, ns);

        Assert.Multiple(() =>
        {
            // 4320 - 3558 = 762 twips into the Amount column, 120 twips down a 423-twip band,
            // 2,600 by 263 twips of object - and then 30 twips further out on every side,
            // because it is framed on all four and Crystal draws a frame 2pt outside the
            // bounds. Its padding puts the value back where the bounds have it.
            Assert.That(summary.Element(ns + "Left")!.Value, Is.EqualTo("36.6pt"));
            Assert.That(summary.Element(ns + "Top")!.Value, Is.EqualTo("4.5pt"));
            Assert.That(summary.Element(ns + "Width")!.Value, Is.EqualTo("133pt"));
            Assert.That(summary.Element(ns + "Height")!.Value, Is.EqualTo("16.15pt"));
            Assert.That(summary.Element(ns + "Style")!.Element(ns + "PaddingLeft")?.Value, Is.EqualTo("1.5pt"));
            Assert.That(summary.Element(ns + "Style")!.Element(ns + "PaddingTop")?.Value, Is.EqualTo("1.5pt"));
            Assert.That(summary.Descendants(ns + "BackgroundColor").Single().Value,
                Is.EqualTo("#C0C0C0"));
            Assert.That(summary.Descendants(ns + "BorderStyle").Single().Elements().Select(e => e.Name.LocalName),
                Is.EquivalentTo(new[] { "Left", "Right", "Top", "Bottom" }));
        });
    }

    // Crystal's default group caption is as wide as the whole band and simply overlaps
    // whatever labels sit further across. A table row cannot overlap, so the caption gets a
    // cell spanning the columns before the first one holding something of its own - here
    // 3,498 twips of cell for an 11,340-twip object. The Textbox keeps the object's own
    // width rather than being clamped to the cell: Rectangle offsets its contents and does
    // not clip them (Majorsilence.Reporting's Rectangle.RunPage), so the overflow renders
    // the way Crystal's own overlap does, and clamping would have cut a caption short.
    [Test]
    public void RdlConverter_GroupCaptionWiderThanItsCell_KeepsTheObjectsOwnWidth()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(GroupBandReport()));
        var ns = doc.Root!.Name.Namespace;

        var caption = Framed(GroupHeaderRow(doc, ns).Descendants(ns + "TableCell").First(), ns);

        Assert.That(caption.Element(ns + "Value")!.Value, Is.EqualTo("=Fields!Customer.Value"));
        Assert.That(caption.Element(ns + "Width")!.Value, Is.EqualTo("567pt"),
            "11,340 twips of caption, undiminished by a cell that spans 3,498");
    }

    // The free-form writer is how the report header and page footer bands of this same
    // table are written, and routing a group band through it would lose BuildSummaryExpression:
    // a group's "Count of X" would quietly become a plain field reference. The frame is
    // applied per cell for that reason, so the summary logic stays where it is.
    [Test]
    public void RdlConverter_GroupHeaderSummaryField_KeepsItsAggregateInsideAFramedCell()
    {
        var report = GroupBandReport();
        report.Sections.First(s => s.Type == SectionType.GroupHeader).Objects.Add(
            new FieldObject { FieldName = "Amount", Bounds = new(3700, 124, 900, 237),
                SummaryFunction = AggregateFunction.Count });

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var countCell = GroupHeaderRow(doc, ns).Descendants(ns + "TableCell")
            .First(c => c.Descendants(ns + "Value").Any(v => v.Value.StartsWith("=Count(")));
        var counted = Framed(countCell, ns);

        Assert.That(counted.Element(ns + "Value")!.Value, Is.EqualTo("=Count(Fields!Amount.Value)"));
        // 3700 - 3558 = 142 twips into the Amount column.
        Assert.That(counted.Element(ns + "Left")!.Value, Is.EqualTo("7.1pt"));
        Assert.That(counted.Element(ns + "Width")!.Value, Is.EqualTo("45pt"));
    }

    // ReportDefinition.Language carries the number separators the file records. It used to
    // be emitted as RDL's report-level <Language>, which is the whole report's culture and
    // takes dates with it: a report whose numbers use "," and "." was declared en-US, and
    // every date in it that Crystal leaves to the rendering machine came out US-style.
    // Country-Region-Sort's print date is Format(ExecutionTime, "d") - the machine's own
    // short date - and the real engine renders 2026-08-28 on this box where we rendered
    // 9/11/2026 from the same run.
    private static ReportDefinition SeparatorCultureReport() => new()
    {
        ReportTitle = "Numbers And Dates",
        Language = "de-DE",
        Fields =
        [
            new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
            new DatabaseField { Name = "When", ColumnName = "When", DataType = "DateTime" }
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects =
                [
                    // A numeric picture: its "." and "," are placeholders the culture fills.
                    new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240),
                        Format = new ObjectFormat { FormatString = "#,##0.00" } },
                    // A date whose separators are quoted literals, so no culture is needed -
                    // and a date that defers to the machine carries no format string at all.
                    new FieldObject { FieldName = "When", Bounds = new(1440, 0, 1440, 240),
                        Format = new ObjectFormat { FormatString = "MM\'/\'dd\'/\'yyyy" } }
                ] }
        ]
    };

    [Test]
    public void RdlConverter_SeparatorCulture_IsNotDeclaredForTheWholeReport()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(SeparatorCultureReport()));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Root.Element(ns + "Language"), Is.Null,
            "naming a culture on the report governs its dates too, and Crystal leaves those "
            + "to the rendering machine - which is what the engine falls back to with none named");
    }

    [Test]
    public void RdlConverter_SeparatorCulture_GoesOnTheNumericObjectThatNeedsIt()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(SeparatorCultureReport()));
        var ns = doc.Root!.Name.Namespace;

        var styles = doc.Descendants(ns + "Details").First()
            .Descendants(ns + "Style").ToList();

        var numeric = styles.Single(s => s.Element(ns + "Format")?.Value == "#,##0.00");
        Assert.That(numeric.Element(ns + "Language")?.Value, Is.EqualTo("de-DE"),
            "\",\" and \".\" in a numeric picture are placeholders only a culture can fill");

        var date = styles.Single(s => s.Element(ns + "Format")?.Value == "MM\'/\'dd\'/\'yyyy");
        Assert.That(date.Element(ns + "Language"), Is.Null,
            "a date format from this converter quotes its separators, so it needs no culture "
            + "- and taking one would override the machine the way the report tag did");
    }

    // A detail band is one row as tall as the whole section, and its objects sit at their
    // own Top inside it. Written as plain cells they were all flushed to the top of the row,
    // which is not a per-object error but a per-row one: on BeforeTV the middle field is
    // drawn 68 twips down a 289-twip band, and flushing it collapsed every row's ink into a
    // 33-37px band where the real engine draws 45-46px.
    [TestCase(0, null, "3.4pt", TestName = "a field at the top of its band pads below")]
    [TestCase(1, "3.4pt", null, TestName = "a field 68 twips down its band pads above")]
    public void RdlConverter_DetailField_SitsAtItsOwnTopInsideTheRow(
        int cellIndex, string? expectedTop, string? expectedBottom)
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Offset Details",
            Fields =
            [
                new DatabaseField { Name = "A", ColumnName = "A", DataType = "String" },
                new DatabaseField { Name = "B", ColumnName = "B", DataType = "String" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 289,
                    Objects =
                    [
                        new FieldObject { FieldName = "A", Bounds = new(0, 0, 1440, 221) },
                        new FieldObject { FieldName = "B", Bounds = new(1440, 68, 1440, 221) }
                    ] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var cell = doc.Descendants(ns + "Details").First()
            .Descendants(ns + "TableCell").ToList()[cellIndex];

        Assert.That(cell.Descendants(ns + "PaddingTop").SingleOrDefault()?.Value,
            Is.EqualTo(expectedTop));
        Assert.That(cell.Descendants(ns + "PaddingBottom").SingleOrDefault()?.Value,
            Is.EqualTo(expectedBottom));
    }

    // The same guard the horizontal half has: a section reporting no height gets a 240-twip
    // fallback, which is a guess rather than a measurement, and padding against a guess would
    // move text that is currently in the right place.
    [Test]
    public void RdlConverter_DetailBandWithNoMeasuredHeight_IsNotPaddedVertically()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Unmeasured",
            Fields = [new DatabaseField { Name = "A", ColumnName = "A", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 0,
                    Objects = [new FieldObject { FieldName = "A", Bounds = new(0, 68, 1440, 221) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var details = doc.Descendants(ns + "Details").First();
        Assert.That(details.Descendants(ns + "PaddingTop").Any(), Is.False);
        Assert.That(details.Descendants(ns + "PaddingBottom").Any(), Is.False);
    }

    [Test]
    public void RdlConverter_GroupFooter_EmitsSumExpression()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Grouped Report",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Customer", Bounds = new(0,0,2880,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Customer", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }
                    ]
                },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }] }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("Sum(Fields!Amount.Value)"), "GroupFooter FieldObject for known DB field should emit Sum() aggregate");
        Assert.That(rdl, Does.Contain("<Footer>"), "TableGroup should have a Footer section");
        Console.WriteLine(rdl);
    }

    [Test]
    public void RdlConverter_CenterAlignment_EmitsTextAlignCenter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Aligned Report",
            Fields = [new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.PageHeader,
                    HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject
                        {
                            FieldName = "Name",
                            Bounds = new(0, 0, 1440, 240),
                            Format = new ObjectFormat { HAlign = HorizontalAlignment.Center }
                        }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("TextAlign").And.Contain("Center"),
            "Center-aligned FieldObject should emit <TextAlign>Center</TextAlign>");
    }

    [Test]
    public void RdlConverter_TextObjectWithFieldRef_EmitsExpression()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Text Ref Report",
            Fields = [new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.ReportHeader,
                    HeightTwips = 240,
                    Objects =
                    [
                        new TextObject
                        {
                            Name = "Text1",
                            Text = "Total for {Customer}:",
                            Bounds = new(0, 0, 2880, 240)
                        }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        // "{Customer}" should resolve to a Fields! expression, not literal text
        Assert.That(rdl, Does.Contain("Fields!Customer.Value"),
            "TextObject with {Customer} should emit Fields!Customer.Value");
        Assert.That(rdl, Does.Contain("Total for"),
            "Literal prefix text should still be present");
    }

    [Test]
    public void RdlConverter_TextObjectGroupNameRef_ResolvesToGroupField()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Group Label Report",
            Fields = [new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.GroupHeader,
                    HeightTwips = 240,
                    GroupLevel = 0,
                    Objects =
                    [
                        new TextObject
                        {
                            Name = "Text1",
                            Text = "Total for {Group #1 Name}:",
                            Bounds = new(0, 0, 2880, 240)
                        }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("Fields!Customer.Value"),
            "{Group #1 Name} in TextObject should resolve to the group's field reference");
        Assert.That(rdl, Does.Contain("Total for"),
            "Literal prefix should still appear");
    }

    [Test]
    public void RdlConverter_TextObjectPureLiteral_NoExpression()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Plain Text Report",
            Sections =
            [
                new Section
                {
                    Type = SectionType.ReportHeader,
                    HeightTwips = 240,
                    Objects =
                    [
                        new TextObject { Name = "Text1", Text = "Hello World", Bounds = new(0,0,1440,240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        // Pure literal text should NOT become an expression (no leading "=")
        Assert.That(rdl, Does.Contain("Hello World"));
        Assert.That(rdl, Does.Not.Contain("=\"Hello World\""),
            "Pure literal TextObject should not be wrapped in an expression");
    }

    [Test]
    public void RdlConverter_RecordSelectionFormula_EmitsDataSetFilter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Filtered Report",
            RecordSelectionFormula = "{Customer.Country} = \"USA\"",
            Fields = [new DatabaseField { Name = "Country", ColumnName = "Country", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Country", Bounds = new(0,0,1440,240) }] }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("<Filters>"), "Should emit a <Filters> element for RecordSelectionFormula");
        Assert.That(rdl, Does.Contain("<Filter>"), "Should emit a <Filter> element");
        Assert.That(rdl, Does.Contain("Country"), "Filter expression should reference the Country field");
        Assert.That(rdl, Does.Contain("USA"), "Filter expression should contain the literal value");
        Assert.That(rdl, Does.Contain("=true"), "Filter value should be =true for a boolean expression");
    }

    [Test]
    public void RdlConverter_TableWidth_EqualsColumnWidthSum()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Width Test",
            Fields =
            [
                new DatabaseField { Name = "A", ColumnName = "A", DataType = "String" },
                new DatabaseField { Name = "B", ColumnName = "B", DataType = "String" }
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "A", Bounds = new(0, 0, 2160, 240) },   // 1.5in
                        new FieldObject { FieldName = "B", Bounds = new(2160, 0, 2880, 240) }  // 2in
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        // 2160 + 2880 = 5040 twips = 3.5in
        Assert.That(rdl, Does.Contain("252pt"), "Table width should be the sum of column widths");
    }

    [Test]
    public void RdlConverter_ParameterField_EmitsReportParameters()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Param Report",
            Fields =
            [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new ParameterField { Name = "AmtMin", PromptText = "Minimum Amount", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("<ReportParameters>"), "Should emit <ReportParameters> element");
        Assert.That(rdl, Does.Contain("AmtMin"), "Should include parameter name");
        Assert.That(rdl, Does.Contain("Minimum Amount"), "Should include prompt text");
        Assert.That(rdl, Does.Not.Contain("<!--"), "Parameters should be real elements, not XML comments");
    }

    [Test]
    public void RdlConverter_BareParameterSelectionFormula_DoesNotEmitFilter()
    {
        // A Crystal record selection formula that is just a parameter reference
        // (no comparison operator) should NOT emit a <Filters> element
        var report = new ReportDefinition
        {
            ReportTitle = "Param Filter Report",
            RecordSelectionFormula = "?Order_Amt_Range",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Not.Contain("<Filters>"),
            "Bare parameter reference without comparison should not emit a filter");
    }

    // This used to assert that every Textbox grows. Nothing should: Crystal's Can Grow is a
    // per-object flag, off by default, and an object occupies the height the report drew it
    // at. Growing them discarded that height in favour of whatever line box the engine gives
    // the font - in a table row that is a pitch, so it compounds down the page, and in a
    // free-form object it lets text wrap, which moves the text off the Top the object
    // declares even though the box itself has not moved.
    [Test]
    public void RdlConverter_NeitherADetailCellNorAFreeFormTextbox_Grows()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "CanGrow Report",
            Fields = [new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 240,
                    Objects = [new TextObject { Name = "Title", Text = "A title",
                        Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Name", Bounds = new(0,0,1440,240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var detailCell = doc.Descendants(ns + "Details").First()
            .Descendants(ns + "Textbox").First();
        Assert.That(detailCell.Element(ns + "CanGrow")?.Value, Is.EqualTo("false"),
            "a detail row is the height the report drew it, so its cells do not grow");

        // A free-form object was left growing at first, on the reasoning that it carries its
        // own position so growing it moves nothing else. That reasoning was wrong: a grown
        // box wraps its text, and the wrapped block no longer starts at the object's Top. A
        // page header's column labels ended up rendering below the first detail row.
        var title = doc.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Title");
        Assert.That(title.Element(ns + "CanGrow")?.Value, Is.EqualTo("false"));
    }

    // A column is as wide as the gap to the next column's start, which is wider than the
    // field the report drew whenever Crystal left a space between them. A Textbox Width
    // inside a TableCell is ignored, so right-aligned text ran to the column's right edge -
    // the next column's left edge - and a number ended flush against the following word.
    // Padding the cell by the difference puts the text back inside the field.
    [Test]
    public void RdlConverter_DetailCellWiderThanItsField_IsPaddedBackToTheFieldsWidth()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Spaced",
            Fields =
            [
                new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" },
                new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        // A 1000-twip field, but the next column starts 1440 twips along,
                        // so the column is 1440 wide and 440 twips of it are not the field.
                        new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1000, 240) },
                        new FieldObject { FieldName = "Name", Bounds = new(1440, 0, 1440, 240) }
                    ] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var cells = doc.Descendants(ns + "Details").First()
            .Descendants(ns + "Textbox").ToList();

        Assert.That(cells[0].Descendants(ns + "PaddingRight").Select(e => e.Value).SingleOrDefault(),
            Is.EqualTo("22pt"),
            "the 440 twips the column has beyond the field become padding on its right");

        // The last column has no next column to measure a gap against, so its width is the
        // field's own and there is nothing to pad. Padding it on a guess would move text
        // that is already where the report put it.
        Assert.That(cells[1].Descendants(ns + "PaddingRight").Any(), Is.False,
            "a column that is already the field's width is left alone");
    }

    // Border formatting reaches the RDL as a per-edge BorderStyle plus a width, and a
    // background colour as BackgroundColor - the engine draws an edge only where both its
    // style and width are set.
    [Test]
    public void RdlConverter_ObjectBordersAndBackground_ReachTheStyle()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Bordered",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 480,
                    Objects = [new TextObject { Name = "Framed", Text = "In a box",
                        Bounds = new(0, 0, 2880, 240),
                        Format = new ObjectFormat
                        {
                            BorderLeft = 1, BorderRight = 1, BorderTop = 1, BorderBottom = 1,
                            BorderWidthTwips = 20, BackColor = "#C0C0C0",
                        } }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var framed = doc.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Framed");
        var style = framed.Element(ns + "Style")!;

        var borderStyle = style.Element(ns + "BorderStyle");
        Assert.That(borderStyle, Is.Not.Null);
        Assert.That(borderStyle!.Elements().Select(e => (e.Name.LocalName, e.Value)),
            Is.EquivalentTo(new[]
            {
                ("Left", "Solid"), ("Right", "Solid"), ("Top", "Solid"), ("Bottom", "Solid"),
            }));
        Assert.That(style.Element(ns + "BorderWidth")?.Element(ns + "Default")?.Value,
            Is.EqualTo("1pt"), "20 twips is 1pt");
        Assert.That(style.Element(ns + "BackgroundColor")?.Value, Is.EqualTo("#C0C0C0"));
    }

    // An object whose Suppress box is ticked prints nowhere - in a details column as much as
    // anywhere else.
    [Test]
    public void RdlConverter_ASuppressedDetailField_IsHidden()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" },
                      new DatabaseField { Name = "Note", ColumnName = "Note", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "IdField", FieldName = "ID", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { Name = "NoteField", FieldName = "Note", Bounds = new(1440, 0, 1440, 240), SuppressOverride = true }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var note = doc.Descendants(ns + "Textbox").Where(t => (string?)t.Element(ns + "Value") == "=Fields!Note.Value").ToList();

        // Not written at all, or written hidden: either way it does not print.
        Assert.That(note.All(t => (string?)t.Element(ns + "Visibility")?.Element(ns + "Hidden") == "true"),
            $"Note printed: {string.Join(" | ", note.Select(t => t.ToString()))}");
    }

    // An object's Suppress formula hides it, with CurrentFieldValue read as its own field, and
    // an empty box of its size holds its space: hidden alone, the engine closes the space up.
    [Test]
    public void RdlConverter_AnObjectsSuppressFormula_HidesItAndKeepsItsSpace()
    {
        var debit = new FieldObject { Name = "Debit1", FieldName = "Lines.Debit", Bounds = new(0, 0, 1440, 240),
            SuppressFormula = "currentfieldvalue = 0" };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Debit", ColumnName = "Debit", TableName = "Lines", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 480, Objects = [debit] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Debit1");
        var hidden = (string?)box.Element(ns + "Visibility")?.Element(ns + "Hidden");

        Assert.Multiple(() =>
        {
            Assert.That(hidden, Does.Contain("Fields!Debit.Value").And.Contain("= 0"));
            Assert.That(doc.Descendants(ns + "Textbox").Count(t => (string?)t.Element(ns + "Value") == ""
                && (string?)t.Element(ns + "Top") == (string?)box.Element(ns + "Top")), Is.EqualTo(1), "the spacer");
        });
    }

    // The box that holds a suppressible object's space starts a twip above an object of no
    // height and ends where it does. The engine takes items in order of top and left, and at
    // the object's own place the box could come second and be placed below the hidden object,
    // which marks a place that leaves out where its band starts.
    [Test]
    public void RdlConverter_TheSpaceOfASuppressibleRule_IsHeldFromATwipAboveIt()
    {
        var rule = new LineObject { Name = "Rule", Bounds = new(1440, 600, 2880, 0), SuppressFormula = "pagenumber = 1" };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 960, Objects = [
                new TextObject { Name = "Title", Text = "Title", Bounds = new(0, 0, 2880, 240) }, rule] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var spacer = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Element(ns + "Value") == ""
            && (string?)t.Element(ns + "Left") == "72pt");

        Assert.Multiple(() =>
        {
            Assert.That((string?)spacer.Element(ns + "Top"), Is.EqualTo("29.95pt"));
            Assert.That((string?)spacer.Element(ns + "Height"), Is.EqualTo("0.05pt"));
        });
    }

    // A group band whose objects stand on more than one line is written as one free-form row,
    // each object where Crystal puts it: as cells it came out as a single row of columns.
    [Test]
    public void RdlConverter_AGroupHeaderOfSeveralLines_IsWrittenFreeForm()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owner", ColumnName = "Owner", DataType = "String" },
                      new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Owner" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 1200, Objects = [
                    new TextObject { Name = "Line1", Text = "PROPERTY #:", Bounds = new(0, 0, 2880, 240) },
                    new TextObject { Name = "Line2", Text = "REQUESTED BY", Bounds = new(0, 480, 2880, 240) },
                    new FieldObject { Name = "OwnerField", FieldName = "Owner", Bounds = new(0, 900, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(4320, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var header = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Header")!;
        var line2 = header.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Line2");

        Assert.That((string?)line2.Element(ns + "Top"), Is.EqualTo("24pt"), "at its own place, below the first line");
    }

    // A group's second header whose objects stand on several lines is free-form too, as its
    // first is, and so is a band whose one tall object stands beside a column of labels.
    [Test]
    public void RdlConverter_AFurtherGroupHeaderOfSeveralLines_IsWrittenFreeForm()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owner", ColumnName = "Owner", DataType = "String" },
                      new DatabaseField { Name = "Address", ColumnName = "Address", DataType = "String" },
                      new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Owner" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "OwnerField", FieldName = "Owner", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 1500, Objects = [
                    new FieldObject { Name = "AddressField", FieldName = "Address", Bounds = new(1440, 0, 2880, 1200) },
                    new TextObject { Name = "Phone", Text = "Phone :", Bounds = new(5760, 0, 1440, 240) },
                    new TextObject { Name = "Email", Text = "Email :", Bounds = new(5760, 480, 1440, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(4320, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var email = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Email");

        Assert.That((string?)email.Element(ns + "Top"), Is.EqualTo("24pt"), "below the phone label, where Crystal puts it");
    }

    // A report with no title prints nothing where its Report Title field is, as Crystal does,
    // not the RDL's own internal name.
    [TestCase("", "=\"\"")]
    [TestCase("Rates by Property", "=\"Rates by Property\"")]
    public void RdlConverter_ReportTitle_IsTheReportsOwn(string title, string expected)
    {
        var report = new ReportDefinition
        {
            ReportTitle = title,
            Sections = [new Section { Type = SectionType.PageHeader, HeightTwips = 300, Objects = [
                new FieldObject { Name = "TitleField", FieldName = "Report Title", Bounds = new(0, 0, 5760, 300) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        Assert.That((string?)doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "TitleField").Element(ns + "Value"),
            Is.EqualTo(expected));
    }

    // A summary embedded in a text object is the aggregate it names, as a placed one is.
    [Test]
    public void RdlConverter_ASummaryEmbeddedInText_IsItsAggregate()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owing", ColumnName = "Owing", TableName = "T", DataType = "Float64" },
                      new FormulaField { Name = "total", FormulaText = "{T.Owing} * 2", ResultType = "Float64" }],
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 240, Objects = [
                new TextObject { Name = "Sentence", Text = "totally {Sum of @total} and {Count of T.Owing} items",
                    Bounds = new(0, 0, 5760, 240) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string value = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Sentence").Element(ns + "Value")!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(value, Does.Contain("Sum(Fields!total.Value)"));
            Assert.That(value, Does.Contain("Count(Fields!Owing.Value)"));
            Assert.That(value, Does.Not.Contain("{Sum of"));
        });
    }

    // A page header section that is only a band of colour is drawn as a row of that colour
    // above the next one's column headings.
    [Test]
    public void RdlConverter_APageHeaderOfColourOnly_IsARowOfThatColour()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owner", ColumnName = "Owner", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 171, BackColor = "#0080C0" },
                new Section { Type = SectionType.PageHeader, HeightTwips = 668, BackColor = "#000000", Objects = [
                    new TextObject { Name = "OwnerLabel", Text = "OWNER", Bounds = new(0, 200, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "OwnerField", FieldName = "Owner", Bounds = new(0, 0, 2880, 240) }] },
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var rows = doc.Descendants(ns + "Table").First().Element(ns + "Header")!.Descendants(ns + "TableRow").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Descendants(ns + "BackgroundColor").Select(b => b.Value), Does.Contain("#0080C0"));
            Assert.That((string?)rows[0].Element(ns + "Height"), Is.EqualTo("8.55pt"));
        });
    }

    // New Page After on a group header that prints nothing is a new page between groups.
    [Test]
    public void RdlConverter_NewPageAfterAnEmptyGroupHeader_BreaksBetweenGroups()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Cheque", ColumnName = "Cheque", TableName = "T", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Cheque", TableName = "T" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 0, Suppress = true,
                    NewPageAfterFormula = "NOT OnLastRecord" },
                new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 480, Objects = [
                    new FieldObject { Name = "ChequeField", FieldName = "T.Cheque", Bounds = new(0, 0, 2880, 240) },
                    new TextObject { Name = "Label", Text = "Cheque", Bounds = new(0, 240, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 0, Suppress = true },
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "Grouping").Single().Element(ns + "PageBreakAtEnd")?.Value, Is.EqualTo("true"));
    }

    // A subreport's frame is drawn in its own colour: one framed in white shows nothing.
    [Test]
    public void RdlConverter_ASubreportsFrame_IsInItsOwnColour()
    {
        var sub = new SubreportObject
        {
            Name = "Notes", SubreportName = "Notes", Bounds = new(0, 0, 5760, 300),
            Format = new ObjectFormat { BorderLeft = 1, BorderRight = 1, BorderTop = 1, BorderBottom = 1, BorderColor = "#FFFFFF" },
            Report = new ReportDefinition { ReportTitle = "Notes" },
        };
        var report = new ReportDefinition { Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 300, Objects = [sub] }] };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var frame = doc.Descendants(ns + "Rectangle").Single(r => (string?)r.Attribute("Name") == "Notes_Frame");

        Assert.That(frame.Element(ns + "Style")!.Element(ns + "BorderColor")!.Element(ns + "Default")!.Value, Is.EqualTo("#FFFFFF"));
    }

    // A box drawn from a group's second header into its third is drawn in both, the third's
    // piece where its suppress formula lets it print, though its Suppress box is ticked.
    [Test]
    public void RdlConverter_ABoxAcrossAGroupsHeaders_IsDrawnInEach()
    {
        var third = new Section
        {
            Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 1500, Suppress = true,
            SuppressFormula = "{T.Owner} = \"\"",
            Objects = [new TextObject { Name = "Client", Text = "Client :", Bounds = new(240, 100, 1440, 240) },
                       new TextObject { Name = "Phone", Text = "Phone :", Bounds = new(240, 500, 1440, 240) }],
        };
        var box = new BoxObject { Name = "Frame", Bounds = new(120, 300, 11000, 3000), EndSection = third, EndBottomTwips = 1400 };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owner", ColumnName = "Owner", TableName = "T", DataType = "String" },
                      new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "T", DataType = "Float64" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Owner", TableName = "T" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "OwnerField", FieldName = "T.Owner", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 1000, Objects = [
                    new TextObject { Name = "Title", Text = "INSTRUCTIONS", Bounds = new(240, 360, 2400, 240) },
                    new TextObject { Name = "Request", Text = "Request By :", Bounds = new(240, 700, 1440, 240) },
                    box] },
                third,
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "AmountField", FieldName = "T.Amount", Bounds = new(4320, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var pieces = doc.Descendants(ns + "Rectangle").Where(r => (string?)r.Attribute("Name") == "Frame").ToList();

        Assert.That(pieces.Select(p => (string?)p.Element(ns + "Height")), Is.EqualTo(new[] { "35pt", "70pt" }),
            "the second header's 700 twips below the box's top, and the third's down to where it ends");
    }

    // A text object with no text still draws its border: a payroll report's rule under every
    // detail line is an empty object with a bottom border, placed in a group footer.
    [Test]
    public void RdlConverter_AnEmptyTextObjectWithABorder_IsWritten()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Owner", ColumnName = "Owner", DataType = "String" },
                      new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Owner" }],
            Sections =
            [
                new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 220, Objects = [
                    new TextObject { Name = "Rule", Text = "", Bounds = new(120, 60, 11400, 89),
                        Format = new ObjectFormat { BorderBottom = 1 } }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(4320, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var footer = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Footer")!;

        Assert.That(footer.Descendants(ns + "BorderStyle").Any(b => (string?)b.Element(ns + "Bottom") == "Solid"),
            footer.ToString());
    }

    // A date embedded in a text object prints in its own format; a text field embedded the
    // same way is left alone, since a date format given text prints the format itself.
    [Test]
    public void RdlConverter_AnEmbeddedDate_PrintsInItsOwnFormat()
    {
        var notice = new TextObject
        {
            Name = "Notice", Text = "On {?Sale Date} for {Owner.Name}", Bounds = new(0, 0, 5760, 240),
            FieldFormats = new() { ["?Sale Date"] = "MMMM' 'dd', 'yyyy", ["Owner.Name"] = "MMMM' 'dd', 'yyyy" },
        };
        var report = new ReportDefinition
        {
            Fields = [new ParameterField { Name = "Sale Date", DataType = "DateTime" },
                      new DatabaseField { Name = "Name", ColumnName = "Name", TableName = "Owner", DataType = "String" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [notice] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string value = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Notice").Element(ns + "Value")!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(value, Does.Contain("Format(Parameters!Sale_Date.Value, \"MMMM' 'dd', 'yyyy\")"));
            Assert.That(value, Does.Contain("Fields!Name.Value").And.Not.Contain("Format(Fields!Name"));
        });
    }

    // A date whose Date Order formula reads a parameter prints in the order the parameter
    // picks: 0 year-month-day, 1 day-month-year, 2 month-day-year, and any other value the
    // order the record holds.
    [Test]
    public void RdlConverter_ADateOrderFormula_ChoosesTheFormatByItsValue()
    {
        var date = new FieldObject
        {
            Name = "IssueDate1", FieldName = "?Issue Date", Bounds = new(0, 0, 1440, 240),
            Format = new ObjectFormat
            {
                FormatString = "MMM' 'dd', 'yyyy",
                DateOrderFormula = "// returns crYearMonthDay, crDayMonthYear or crMonthDayYear\r\n{?Order}",
                DateFormatsByOrder = ["yyyy' 'MMM', 'dd", "dd' 'MMM', 'yyyy", "MMM' 'dd', 'yyyy"],
            },
        };
        var report = new ReportDefinition
        {
            Fields = [new ParameterField { Name = "Issue Date", DataType = "DateTime" },
                      new ParameterField { Name = "Order", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [date] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string? format = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "IssueDate1")
            .Element(ns + "Style")?.Element(ns + "Format")?.Value;

        Assert.That(format, Is.EqualTo(
            "=IIF((Parameters!Order.Value) = 0, \"yyyy' 'MMM', 'dd\", "
            + "IIF((Parameters!Order.Value) = 1, \"dd' 'MMM', 'yyyy\", "
            + "IIF((Parameters!Order.Value) = 2, \"MMM' 'dd', 'yyyy\", \"MMM' 'dd', 'yyyy\")))"));
    }

    // A growing field is given the room down to its band's bottom, so the engine moves nothing
    // below it, as Crystal moves nothing within a section; one with a border keeps its own
    // height, since the border would show the difference.
    [Test]
    public void RdlConverter_AGrowingField_TakesTheRoomToItsBandsBottom()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Address", ColumnName = "Address", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 2400, Objects = [
                new FieldObject { Name = "Address1", FieldName = "Address", Bounds = new(0, 240, 2880, 240),
                    Format = new ObjectFormat { CanGrow = true } },
                new FieldObject { Name = "Address2", FieldName = "Address", Bounds = new(4320, 240, 2880, 240),
                    Format = new ObjectFormat { CanGrow = true, BorderBottom = 1 } },
                new TextObject { Name = "Salutation", Text = "Dear Customer:", Bounds = new(0, 1440, 2880, 240) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string? Height(string name) => (string?)doc.Descendants(ns + "Textbox")
            .Single(t => (string?)t.Attribute("Name") == name).Element(ns + "Height");

        Assert.Multiple(() =>
        {
            Assert.That(Height("Address1"), Is.EqualTo("108pt"), "down to the band's bottom, past the salutation");
            Assert.That(Height("Address2"), Is.Not.EqualTo("108pt"), "a bordered one keeps its height");
        });
    }

    // A growing field's room ends at its band's designed bottom, not at the bottom of a rule
    // drawn from the band down into a later section: the room to there made the band's row
    // as tall as the rule.
    [Test]
    public void RdlConverter_AGrowingFieldsRoom_EndsAtTheBandsDesignedBottom()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Address", ColumnName = "Address", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [
                new FieldObject { Name = "Address1", FieldName = "Address", Bounds = new(0, 240, 2880, 240),
                    Format = new ObjectFormat { CanGrow = true } },
                new LineObject { Name = "Rule", Bounds = new(3000, 1200, 0, 2400) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var address = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Address1");

        Assert.That((string?)address.Element(ns + "Height"), Is.EqualTo("60pt"), "1,440 - 240 twips, not to the rule's bottom");
    }

    // A growing field limited to a number of lines that fit in its room is that many lines
    // tall and may not grow, as Crystal grows it no further; one whose lines do not fit grows.
    // Arial 10 lines are 12pt apart.
    [TestCase(2400, 5, false, "60pt")]
    [TestCase(2400, 0, true, "108pt")]
    [TestCase(960, 5, true, "36pt")]
    public void RdlConverter_ALineLimitedGrowingField_GrowsOnlyPastItsRoom(int bandTwips, int maxLines, bool grows, string height)
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Address", ColumnName = "Address", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = bandTwips, Objects = [
                new FieldObject { Name = "Address1", FieldName = "Address", Bounds = new(0, 240, 2880, 240),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 10, CanGrow = true, MaxLines = maxLines } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var address = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Address1");

        Assert.Multiple(() =>
        {
            Assert.That((string?)address.Element(ns + "CanGrow"), Is.EqualTo(grows ? "true" : "false"));
            Assert.That((string?)address.Element(ns + "Height"), Is.EqualTo(height));
        });
    }

    // A framed growing field goes in a frameless Rectangle down to its band's bottom, so that
    // its growth moves nothing below it in the band, and keeps its own height inside it.
    [Test]
    public void RdlConverter_AFramedGrowingField_IsHeldInARectangleToTheBandsBottom()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Details", ColumnName = "Details", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 2400, Objects = [
                new FieldObject { Name = "Details1", FieldName = "Details", Bounds = new(4320, 240, 2880, 480),
                    Format = new ObjectFormat { CanGrow = true, BorderTop = 1, BorderBottom = 1, BorderLeft = 1, BorderRight = 1 } },
                new TextObject { Name = "Paragraph", Text = "Below the details.", Bounds = new(0, 960, 7200, 240) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var details = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Details1");
        var holder = details.Parent!.Parent!;
        double Points(System.Xml.Linq.XElement e, string name) =>
            double.Parse(((string)e.Element(ns + name)!).Replace("pt", ""), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Multiple(() =>
        {
            Assert.That(holder.Name.LocalName, Is.EqualTo("Rectangle"));
            Assert.That((string?)holder.Attribute("Name"), Is.EqualTo("Details1_Room"));
            Assert.That(Points(holder, "Top") + Points(holder, "Height"), Is.EqualTo(120).Within(0.01), "to the band's bottom");
            Assert.That(Points(details, "Height"), Is.LessThan(Points(holder, "Height")), "the field keeps its own height");
            Assert.That((string?)details.Element(ns + "Top"), Is.EqualTo("0pt"));
            Assert.That((string?)details.Element(ns + "CanGrow"), Is.EqualTo("true"));
            Assert.That(holder.Element(ns + "Style"), Is.Null, "the Rectangle draws no frame of its own");
        });
    }

    // An on-demand subreport prints its caption in its place and nothing of its own content:
    // its caption formula's value, or its report's name where it has none.
    [Test]
    public void RdlConverter_AnOnDemandSubreport_PrintsItsCaptionAndNotItsContent()
    {
        var inner = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new TextObject { Name = "Inner", Text = "Detail lines", Bounds = new(0, 0, 2880, 240) }] }]
        };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "AccountName", ColumnName = "AccountName", DataType = "String" },
                new FormulaField { Name = "Caption", FormulaText = "{Ledger.AccountName}", ResultType = "String" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new SubreportObject { Name = "Detail1", SubreportName = "Detail1", Bounds = new(1440, 0, 2400, 180),
                    OnDemand = true, OnDemandCaptionFormula = "@Caption", Report = inner },
                new SubreportObject { Name = "Detail2", SubreportName = "Detail2", ReportName = "Ledger Detail.rpt",
                    Bounds = new(4320, 0, 2400, 180), OnDemand = true, Report = inner }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string Caption(string frame) => doc.Descendants(ns + "Rectangle")
            .Single(r => (string?)r.Attribute("Name") == frame).Descendants(ns + "Value").Single().Value;

        Assert.Multiple(() =>
        {
            Assert.That(doc.Descendants(ns + "Subreport"), Is.Empty);
            Assert.That(Caption("Detail1_Frame"), Does.Contain("Fields!Caption.Value"));
            Assert.That(Caption("Detail2_Frame"), Is.EqualTo("Ledger Detail.rpt"));
        });
    }

    // A special date with a format of its own prints in it: a notice's Data Date set to
    // "MMMM dd, yyyy" printed the machine's short date.
    [Test]
    public void RdlConverter_ASpecialDateInItsOwnFormat_PrintsInIt()
    {
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [
                new FieldObject { Name = "DataDate1", FieldName = "Data Date", Bounds = new(0, 0, 2880, 240),
                    Format = new ObjectFormat { FormatString = "MMMM' 'dd', 'yyyy" } },
                new FieldObject { Name = "PrintDate1", FieldName = "Print Date", Bounds = new(2880, 0, 2880, 240) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var dataDate = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "DataDate1");
        var printDate = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "PrintDate1");

        Assert.Multiple(() =>
        {
            Assert.That((string?)dataDate.Element(ns + "Value"), Is.EqualTo("=Globals!ExecutionTime"));
            Assert.That(dataDate.Descendants(ns + "Format").Single().Value, Is.EqualTo("MMMM' 'dd', 'yyyy"));
            Assert.That((string?)printDate.Element(ns + "Value"), Is.EqualTo("=Format(Globals!ExecutionTime, \"d\")"),
                "with none it is still the machine's short date");
        });
    }

    // A growing box drawn shorter than one line at Crystal's pitch is left the engine's own
    // line height: at the pitch its one line grew it, and with it every row of the table.
    [TestCase(180, null, TestName = "RdlConverter_AGrowingBoxShorterThanALine_KeepsTheEnginesLineHeight")]
    [TestCase(480, "12pt", TestName = "RdlConverter_AGrowingBoxOfTwoLines_IsSetAtThePitch")]
    public void RdlConverter_AGrowingBox_IsPitchedOnlyWhereALineFits(int heightTwips, string? lineHeight)
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Note", ColumnName = "Note", DataType = "String" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 480, Objects = [
                new FieldObject { Name = "NoteField", FieldName = "Note", Bounds = new(0, 0, 2880, heightTwips),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 10, CanGrow = true } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var note = doc.Descendants(ns + "Textbox").First(t => (string?)t.Element(ns + "Value") == "=Fields!Note.Value");

        Assert.That((string?)note.Descendants(ns + "LineHeight").SingleOrDefault(), Is.EqualTo(lineHeight));
    }

    // Crystal grows a section to the bottom of the object that grew, so the room a cell has
    // below a growing box is no padding: three lines of a printer's Arial 8 make a row of
    // 27.75pt, not that and the room. Only the 0.75pt the line is dropped to Crystal's
    // baseline by comes off, and, where there is no room below, the half point by which the
    // engine, measuring a line in whole pixels at 96 dpi, finds one 9.25pt line 9.75pt tall:
    // the measure then ends where the row does and a row of one line does not grow.
    [TestCase(240, 185, "-0.75pt", TestName = "RdlConverter_AGrowingBoxWithRoomBelow_GivesTheRoomNoPadding")]
    [TestCase(185, 185, "-1.25pt", TestName = "RdlConverter_AGrowingBoxAsTallAsItsRow_GivesUpPaddingForThePixels")]
    public void RdlConverter_AGrowingBox_MeasuresNoTallerThanItsRow(int rowTwips, int heightTwips, string paddingBottom)
    {
        var report = new ReportDefinition
        {
            PrinterName = "Office Laser",
            Fields = [new DatabaseField { Name = "Note", ColumnName = "Note", DataType = "String" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = rowTwips, Objects = [
                new FieldObject { Name = "NoteField", FieldName = "Note", Bounds = new(0, 0, 2880, heightTwips),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 8, CanGrow = true } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Textbox").First(t => (string?)t.Element(ns + "Value") == "=Fields!Note.Value")
            .Element(ns + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)style.Element(ns + "LineHeight"), Is.EqualTo("9.25pt"));
            Assert.That((string?)style.Element(ns + "PaddingBottom"), Is.EqualTo(paddingBottom));
        });
    }

    // Lines are set at Crystal's pitch, which for Arial 10 is 16 screen pixels (12pt), not at
    // the font size; a family the table does not hold keeps the engine's own.
    [TestCase("Arial", 10.0, false, "12pt")]
    [TestCase("Arial", 12.0, true, "15pt")]
    [TestCase("Garamond", 10.0, false, null)]
    public void RdlConverter_LineHeight_IsCrystalsPitch(string family, double size, bool bold, string? lineHeight)
    {
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Para", Text = "One\nTwo", Bounds = new(0, 0, 2880, 720),
                    Format = new ObjectFormat { FontName = family, FontSize = size, Bold = bold } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Para");

        Assert.That((string?)box.Element(ns + "Style")?.Element(ns + "LineHeight"), Is.EqualTo(lineHeight));
    }

    // A report set for a printer sets its lines at the printer's pitch, as the runtime does:
    // Arial 10 at 11.4pt rather than the screen's 12, Times New Roman 9 at 10.55 rather than
    // 12. A family the printer table does not hold keeps the screen's.
    [TestCase("Arial", 8.0, false, "9.25pt")]
    [TestCase("Arial", 8.0, true, "9.6pt")]
    [TestCase("Arial", 10.0, false, "11.4pt")]
    [TestCase("Times New Roman", 9.0, false, "10.55pt")]
    [TestCase("Arial Rounded MT Bold", 10.0, false, "12pt")]
    [TestCase("Garamond", 10.0, false, null)]
    public void RdlConverter_LineHeight_InAReportSetForAPrinter_IsThePrintersPitch(string family, double size, bool bold, string? lineHeight)
    {
        var report = new ReportDefinition
        {
            PrinterName = "Office Laser",
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Para", Text = "One\nTwo", Bounds = new(0, 0, 2880, 720),
                    Format = new ObjectFormat { FontName = family, FontSize = size, Bold = bold } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Para");

        Assert.That((string?)box.Element(ns + "Style")?.Element(ns + "LineHeight"), Is.EqualTo(lineHeight));
    }

    // Crystal breaks a line where it would run past the object measured in a font whose em
    // is the point size, though it draws the smaller em GDI gives that size as a cell, so
    // the engine is given that much less room. An 8pt bold heading 58.5pt wide breaks
    // where it measures 8.37pt to the em at 96 dpi (11 pixels, hinted 1.5% wide), or 8.04
    // at a printer's 600, against the 6.95 drawn. The room comes off the side away from
    // the alignment, so the text still sits against its own edge.
    [TestCase(null, HorizontalAlignment.Right, "9.7pt", null)]
    [TestCase(null, HorizontalAlignment.Left, null, "9.7pt")]
    [TestCase(null, HorizontalAlignment.Default, null, "9.7pt")]
    [TestCase(null, HorizontalAlignment.Justify, null, "9.7pt")]
    [TestCase(null, HorizontalAlignment.Center, "4.85pt", "4.85pt")]
    [TestCase("Office Laser", HorizontalAlignment.Right, "7.7pt", null)]
    public void RdlConverter_TextObject_IsGivenTheRoomCrystalBreaksItsLinesIn(string? printer,
        HorizontalAlignment align, string? paddingLeft, string? paddingRight)
    {
        var report = new ReportDefinition
        {
            PrinterName = printer,
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Heading", Text = "Amount Still Owing", Bounds = new(0, 0, 1170, 384),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 8, Bold = true, CanGrow = true, HAlign = align } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Heading").Element(ns + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)style.Element(ns + "PaddingLeft"), Is.EqualTo(paddingLeft));
            Assert.That((string?)style.Element(ns + "PaddingRight"), Is.EqualTo(paddingRight));
        });
    }

    // Text in pieces of more than one size has no one font to give it the room of, and is
    // left the whole box; pieces that differ only in weight are still the object's font.
    [TestCase(8.0, "9.7pt")]
    [TestCase(11.0, null)]
    public void RdlConverter_TextInSeveralSizes_KeepsTheWholeBox(double secondSize, string? paddingRight)
    {
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Notice", Text = "Amount Still Owing", Bounds = new(0, 0, 1170, 384),
                    Runs = [new TextRun("Amount ", "Arial", 8, true, false, false), new TextRun("Still Owing", "Arial", secondSize, false, false, false)],
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 8, Bold = true, CanGrow = true } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Notice").Element(ns + "Style")!;

        Assert.That((string?)style.Element(ns + "PaddingRight"), Is.EqualTo(paddingRight));
    }

    // A text object that may not grow, in a box that holds one line, is not broken into lines
    // at all: Crystal sets it on the one line and cuts it off at the box's edge. So it keeps
    // the whole box, where one that holds two lines is given Crystal's room to break them in.
    [TestCase(200, null, TestName = "RdlConverter_TextInABoxOfOneLine_KeepsTheWholeBox")]
    [TestCase(480, "9.7pt", TestName = "RdlConverter_TextInABoxOfTwoLines_IsGivenCrystalsRoom")]
    public void RdlConverter_TextThatMayNotGrow_IsBrokenOnlyInABoxOfMoreThanOneLine(int heightTwips, string? paddingRight)
    {
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Label", Text = "Amount Still Owing", Bounds = new(0, 0, 1170, heightTwips),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 8, Bold = true } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Label").Element(ns + "Style")!;

        Assert.That((string?)style.Element(ns + "PaddingRight"), Is.EqualTo(paddingRight));
    }

    // Only a field that can grow breaks into lines, and only text, so only such a field is
    // given less room: one that cannot grow prints on one line cut off at its edge, and a
    // number keeps the whole box. A family whose drawn em is not known keeps it too.
    [TestCase("Name", "Arial", true, "9.7pt")]
    [TestCase("Name", "Arial", false, null)]
    [TestCase("Balance", "Arial", true, null)]
    [TestCase("Name", "Garamond", true, null)]
    public void RdlConverter_Field_IsGivenCrystalsRoomOnlyForText(string column, string family, bool canGrow, string? paddingRight)
    {
        var report = new ReportDefinition
        {
            Fields =
            [
                new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" },
                new DatabaseField { Name = "Balance", ColumnName = "Balance", DataType = "Currency" },
            ],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 480, Objects = [
                new FieldObject { Name = "Value", FieldName = column, Bounds = new(0, 0, 1170, 240),
                    Format = new ObjectFormat { FontName = family, FontSize = 8, Bold = true, CanGrow = canGrow, HAlign = HorizontalAlignment.Left } }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Element(ns + "Value") == $"=Fields!{column}.Value");

        Assert.That((string?)box.Element(ns + "Style")?.Element(ns + "PaddingRight"), Is.EqualTo(paddingRight));
    }

    // A detail cell is given Crystal's room too, on top of the padding that keeps its text
    // inside the field: a growing 58.5pt field in a 72pt column keeps the 13.5pt between
    // them and gives up 9.7pt more, on the side away from its alignment. A column narrower
    // than the field has already taken most of that room, and only the rest is padded.
    [TestCase(1440, true, HorizontalAlignment.Left, null, "23.2pt")]
    [TestCase(1440, true, HorizontalAlignment.Right, "9.7pt", "13.5pt")]
    [TestCase(1440, false, HorizontalAlignment.Left, null, "13.5pt")]
    [TestCase(1000, true, HorizontalAlignment.Left, null, "1.2pt")]
    public void RdlConverter_DetailCell_IsGivenTheRoomCrystalBreaksItsLinesIn(int nextColumnLeft, bool canGrow,
        HorizontalAlignment align, string? paddingLeft, string? paddingRight)
    {
        var report = new ReportDefinition
        {
            Fields =
            [
                new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" },
                new DatabaseField { Name = "Code", ColumnName = "Code", DataType = "Int32" },
            ],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { FieldName = "Name", Bounds = new(0, 0, 1170, 240),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 8, Bold = true, CanGrow = canGrow, HAlign = align } },
                new FieldObject { FieldName = "Code", Bounds = new(nextColumnLeft, 0, 720, 240) }] }]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Details").Single().Descendants(ns + "Textbox")
            .Single(t => (string?)t.Element(ns + "Value") == "=Fields!Name.Value").Element(ns + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)style.Element(ns + "PaddingLeft"), Is.EqualTo(paddingLeft));
            Assert.That((string?)style.Element(ns + "PaddingRight"), Is.EqualTo(paddingRight));
        });
    }

    // So is a group band's cell: a label set at its own place in its column, which has the
    // label's own width to break in, and a text object queued into a free cell, which has
    // the column's.
    [Test]
    public void RdlConverter_GroupBandCells_AreGivenTheRoomCrystalBreaksTheirLinesIn()
    {
        var heading = new ObjectFormat { FontName = "Arial", FontSize = 8, Bold = true, CanGrow = true };
        var report = new ReportDefinition
        {
            Fields =
            [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0, Objects = [
                    new FieldObject { FieldName = "Region", Bounds = new(0, 0, 1440, 240) },
                    new TextObject { Name = "Label", Text = "Amount Still Owing", Bounds = new(1440, 0, 1170, 240), Format = heading }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { FieldName = "Region", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { FieldName = "Amount", Bounds = new(1440, 0, 1440, 240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0, Objects = [
                    new TextObject { Name = "Note", Text = "Amount Still Owing", Bounds = new(0, 0, 1170, 240), Format = heading },
                    new FieldObject { FieldName = "Amount", Bounds = new(1440, 0, 1440, 240) }] },
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        string? PaddingRight(string band) => doc.Descendants(ns + "TableGroup")
            .SelectMany(g => g.Elements(ns + band)).SelectMany(b => b.Descendants(ns + "Textbox"))
            .Single(t => (string?)t.Element(ns + "Value") == "Amount Still Owing")
            .Element(ns + "Style")?.Element(ns + "PaddingRight")?.Value;

        Assert.Multiple(() =>
        {
            Assert.That(PaddingRight("Header"), Is.EqualTo("9.7pt"));
            Assert.That(PaddingRight("Footer"), Is.EqualTo("23.2pt"));
        });
    }

    // A subreport is formatted on its main report's printer, whatever its own file says.
    [TestCase("Office Laser", "11.4pt")]
    [TestCase(null, "12pt")]
    public void RdlConverter_LineHeight_InASubreport_FollowsTheMainReportsPrinter(string? printer, string lineHeight)
    {
        var sub = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [
                new TextObject { Name = "Para", Text = "One\nTwo", Bounds = new(0, 0, 2880, 720),
                    Format = new ObjectFormat { FontName = "Arial", FontSize = 10 } }] }]
        };
        var root = new ReportDefinition { PrinterName = printer };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter { Root = root }.Convert(sub, "Main_"));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Para");

        Assert.That((string?)box.Element(ns + "Style")?.Element(ns + "LineHeight"), Is.EqualTo(lineHeight));
    }

    // RDL has no drop shadow, so Crystal's is drawn as the two strips that are actually
    // visible: one below the object shifted right, one to its right shifted down. Two strips
    // rather than one offset rectangle because a shadowed object normally sets no background
    // of its own, and a rectangle behind a transparent box would show through its interior
    // and fill it black instead of edging it.
    [Test]
    public void RdlConverter_DropShadow_BecomesTwoOffsetStrips()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Shadowed",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1800,
                    Objects = [new TextObject { Name = "Framed", Text = "In a box",
                        // 2in wide, 0.5in tall, half an inch in from the section's corner -
                        // off the origin, so the frame the shadow is cast from can extend
                        // outside the bounds in every direction.
                        Bounds = new(720, 720, 2880, 720),
                        Format = new ObjectFormat { DropShadow = true } }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var strips = doc.Descendants(ns + "Rectangle")
            .Where(r => (r.Attribute("Name")?.Value ?? "").StartsWith("shadow_"))
            .ToList();
        Assert.That(strips, Has.Count.EqualTo(2), "one below, one to the right");
        Assert.That(strips, Has.All.Matches<System.Xml.Linq.XElement>(
            r => r.Element(ns + "Style")?.Element(ns + "BackgroundColor")?.Value == "#000000"));

        // Cast from the frame, not the bounds: the frame's centreline is the bounds grown by
        // 30 twips - 690..3630 by 690..1470 here - and the shadow is that rectangle moved 62
        // twips right and down, both measured off the real engine's own render.
        var below = strips.Single(r => r.Element(ns + "Height")!.Value == "3.1pt");
        Assert.Multiple(() =>
        {
            Assert.That(below.Element(ns + "Left")!.Value, Is.EqualTo("37.6pt"), "shifted right");
            Assert.That(below.Element(ns + "Top")!.Value, Is.EqualTo("73.5pt"), "under the frame");
            Assert.That(below.Element(ns + "Width")!.Value, Is.EqualTo("147pt"), "as wide as it");
        });

        var right = strips.Single(r => r.Element(ns + "Width")!.Value == "3.1pt");
        Assert.Multiple(() =>
        {
            Assert.That(right.Element(ns + "Left")!.Value, Is.EqualTo("181.5pt"), "beside the frame");
            Assert.That(right.Element(ns + "Top")!.Value, Is.EqualTo("37.6pt"), "shifted down");
            Assert.That(right.Element(ns + "Height")!.Value, Is.EqualTo("39pt"), "as tall as it");
        });
    }

    // A report header holding a field needs a table to give it a data scope. With no details
    // table it shared the page header's table, placed below every report header, so a first
    // header printed under the ones after it - ConsolidatedBalanceSheet's title and logo came
    // after its liabilities. It is a table of its own where it starts.
    [Test]
    public void RdlConverter_RoutedReportHeader_PrintsAboveTheOnesAfterIt()
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new FieldObject { Name = "Printed", FieldName = "Print Date", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 600,
                    Objects = [new TextObject { Name = "Second", Text = "Assets", Bounds = new(0, 120, 2880, 360) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240 }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var tables = doc.Descendants(ns + "Table").ToList();
        var second = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Attribute("Name") == "Second");

        Assert.Multiple(() =>
        {
            Assert.That(tables.Select(t => (string?)t.Attribute("Name")), Is.EqualTo(new[] { "TableReportHeader" }));
            Assert.That(tables[0].Element(ns + "Top"), Is.Null, "at the top of the page");
            Assert.That(tables[0].Descendants(ns + "Textbox").Any(t => (string?)t.Attribute("Name") == "Printed"));
            Assert.That(second.Element(ns + "Top")!.Value, Is.EqualTo("78pt"), "below the first header");
        });
    }

    // Crystal draws a border 2pt outside an object's bounds, and per edge: an underline moves
    // down by 2pt but keeps its length. This engine centres a border on the item's edge, so
    // a bordered side is pushed out 30 twips (2pt less half a 1pt line) and padded back by
    // the same, and a side with no border stays exactly where the bounds put it.
    [TestCase((byte)0, (byte)1, "36pt", "36pt", "144pt", "37.5pt", null, "1.5pt",
        TestName = "RdlConverter_BottomBorder_MovesOnlyTheBottom")]
    [TestCase((byte)0, (byte)0, "36pt", "36pt", "144pt", "36pt", null, null,
        TestName = "RdlConverter_NoBorder_LeavesTheBoundsAlone")]
    [TestCase((byte)1, (byte)1, "34.5pt", "34.5pt", "147pt", "39pt", "1.5pt", "1.5pt",
        TestName = "RdlConverter_FullFrame_MovesEverySideOut")]
    public void RdlConverter_BorderSitsOutsideTheBounds(byte sides, byte bottom,
        string left, string top, string width, string height, string? padLeft, string? padBottom)
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1800,
                    Objects = [new TextObject { Name = "Bordered", Text = "Edged",
                        Bounds = new(720, 720, 2880, 720),
                        Format = new ObjectFormat { BorderLeft = sides, BorderRight = sides,
                            BorderTop = sides, BorderBottom = bottom, BorderWidthTwips = 20 } }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").First(tb => tb.Attribute("Name")?.Value == "Bordered");
        var style = box.Element(ns + "Style");

        Assert.Multiple(() =>
        {
            Assert.That(box.Element(ns + "Left")!.Value, Is.EqualTo(left));
            Assert.That(box.Element(ns + "Top")!.Value, Is.EqualTo(top));
            Assert.That(box.Element(ns + "Width")!.Value, Is.EqualTo(width));
            Assert.That(box.Element(ns + "Height")!.Value, Is.EqualTo(height));
            Assert.That(style?.Element(ns + "PaddingLeft")?.Value, Is.EqualTo(padLeft));
            Assert.That(style?.Element(ns + "PaddingBottom")?.Value, Is.EqualTo(padBottom));
        });
    }

    // Crystal puts an object's first baseline at its font's ascent below the object's top, at
    // the point size as the em, where this engine puts it the font's hhea ascent below at the
    // em it draws. A field's ascent is taken in twips; a text object's in the pixels of the
    // device it is laid out on, the printer's in a report set for one and the screen's in one
    // set for none. So a 10pt Tahoma field drops 10pt against the 8.3pt the engine puts it at,
    // a text object 9.95pt bound and 9.75pt unbound, an Arial field 8.95pt against 8.1pt, and
    // a Courier New one, whose ascent is short, sits within a quarter point of the engine's.
    // Every one is the runtime's own placement, measured off its PDF export.
    [TestCase("Tahoma", 10.0, false, null, "1.7pt", TestName = "RdlConverter_TahomaField_IsDroppedToCrystalsBaseline")]
    [TestCase("Tahoma", 10.0, true, "Office Laser", "1.65pt", TestName = "RdlConverter_TahomaText_InABoundReport_IsDroppedToThePrintersBaseline")]
    [TestCase("Tahoma", 10.0, true, null, "1.45pt", TestName = "RdlConverter_TahomaText_InAnUnboundReport_IsDroppedToTheScreensBaseline")]
    [TestCase("Verdana", 10.0, false, null, "1.7pt", TestName = "RdlConverter_VerdanaField_IsDroppedToCrystalsBaseline")]
    [TestCase("Verdana", 10.0, true, null, "1.45pt", TestName = "RdlConverter_VerdanaText_IsDroppedToCrystalsBaseline")]
    [TestCase("Arial", 10.0, false, null, "0.85pt", TestName = "RdlConverter_ArialField_IsDroppedToCrystalsBaseline")]
    [TestCase("Arial", 10.0, true, null, "1.65pt", TestName = "RdlConverter_ArialText_InAnUnboundReport_IsDroppedToTheScreensBaseline")]
    [TestCase("Courier New", 10.0, false, null, "0.2pt", TestName = "RdlConverter_CourierField_IsDroppedToCrystalsBaseline")]
    [TestCase("Courier New", 10.0, true, "Office Laser", null, TestName = "RdlConverter_CourierText_InABoundReport_IsWhereTheEngineSetsIt")]
    [TestCase("Garamond", 10.0, true, null, null, TestName = "RdlConverter_FamilyWithoutMetrics_IsNotMoved")]
    public void RdlConverter_BaselineDrop(string font, double size, bool textObject, string? printer, string? expectedPaddingTop)
    {
        var format = new ObjectFormat { FontName = font, FontSize = size, IsTextObject = textObject };
        ReportObject line = textObject
            ? new TextObject { Name = "Line", Text = "Some text", Bounds = new(720, 720, 2880, 400), Format = format }
            : new FieldObject { Name = "Line", FieldName = "ID", Bounds = new(720, 720, 2880, 400), Format = format };
        var report = new ReportDefinition
        {
            PrinterName = printer,
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1800, Objects = [line] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var box = doc.Descendants(ns + "Textbox").First(tb => tb.Attribute("Name")?.Value == "Line");

        Assert.That(box.Element(ns + "Style")?.Element(ns + "PaddingTop")?.Value, Is.EqualTo(expectedPaddingTop));
    }

    // A text in two fonts is drawn all in its last run's, and its lines placed for the font
    // most of it is in: here 13 screen pixels of Tahoma 10 (195 twips) below the top, against
    // the hhea ascent, three quarters of the em, of the Calibri it is drawn in.
    [Test]
    public void RdlConverter_TextInTwoFonts_IsPlacedForTheFontMostOfItIsIn()
    {
        var format = new ObjectFormat
        {
            FontName = "Calibri", FontSize = 11, IsTextObject = true,
            MainRun = new TextRun("A long first paragraph", "Tahoma", 10, false, false, false),
        };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1800,
                    Objects = [new TextObject { Name = "Notice", Text = "A long first paragraph. Short.",
                        Bounds = new(720, 720, 2880, 400), Format = format }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var style = doc.Descendants(ns + "Textbox").First(tb => tb.Attribute("Name")?.Value == "Notice").Element(ns + "Style")!;
        int drop = 195 - (int)Math.Round(RdlConverter.EmPointsFor("Calibri", 11) * 20 * 0.75);

        Assert.That(style.Element(ns + "PaddingTop")?.Value, Is.EqualTo($"{drop / 20.0:0.##}pt"));
    }

    // The lines a box that may not grow prints, as the runtime prints them: the first, and
    // each after it that starts within the box at a fifth more than the pitch or ends within
    // it. Arial 10 at the screen's 12pt pitch and the printer's 11.4pt, measured on four- and
    // twelve-line text objects at 10- and 20-twip steps of height.
    [TestCase(280, 240, 1)]
    [TestCase(290, 240, 2)]
    [TestCase(570, 240, 2)]
    [TestCase(580, 240, 3)]
    [TestCase(860, 240, 3)]
    [TestCase(870, 240, 4)]
    [TestCase(1660, 240, 6)]
    [TestCase(1680, 240, 7)]
    [TestCase(270, 228, 1)]
    [TestCase(280, 228, 2)]
    [TestCase(1580, 228, 6)]
    [TestCase(1600, 228, 7)]
    public void RdlConverter_CrystalLinesIn_IsTheRuntimes(int heightTwips, int pitchTwips, int lines)
    {
        Assert.That(RdlConverter.CrystalLinesIn(heightTwips, pitchTwips), Is.EqualTo(lines));
    }

    // The depth of the first baseline below the object's top in twips, as the runtime places
    // it in its PDF export: fields at the same depth bound or not, text objects at the
    // printer's pixels bound and the screen's unbound.
    [TestCase("Arial", 10.0, false, false, false, 179)]
    [TestCase("Arial", 10.0, false, false, true, 179)]
    [TestCase("Arial", 10.0, false, true, true, 178)]
    [TestCase("Arial", 10.0, false, true, false, 195)]
    [TestCase("Arial", 8.0, true, true, true, 149)]
    [TestCase("Arial", 8.0, true, true, false, 165)]
    [TestCase("Courier New", 10.0, false, false, false, 160)]
    [TestCase("Courier New", 10.0, false, true, true, 156)]
    [TestCase("Courier New", 10.0, false, true, false, 180)]
    [TestCase("Tahoma", 8.0, false, true, true, 161)]
    [TestCase("Verdana", 10.0, true, true, true, 199)]
    [TestCase("Calibri", 6.0, false, true, true, 115)]
    [TestCase("Calibri", 6.0, false, true, false, 120)]
    [TestCase("Times New Roman", 18.0, true, false, false, 321)]
    [TestCase("Times New Roman", 18.0, true, true, true, 329)]
    [TestCase("Times New Roman", 18.0, true, true, false, 315)]
    public void RdlConverter_FirstBaseline_IsTheRuntimes(string family, double size, bool bold,
        bool textObject, bool printerBound, int twips)
    {
        Assert.That(RdlConverter.FirstBaselineTwips(family, size, bold, false, textObject, printerBound), Is.EqualTo(twips));
    }

    // An empty page footer still takes its height off every page: Crystal breaks
    // TenPct-DiscountDays' pages 30pt above the bottom margin for a 30pt footer holding
    // nothing. So it is written with its height and no ReportItems (an empty ReportItems is
    // fatal to the engine). A suppressed one is left out, as before.
    [TestCase(false, true, TestName = "RdlConverter_EmptyShownPageFooter_StillReservesItsHeight")]
    [TestCase(true, false, TestName = "RdlConverter_EmptySuppressedPageFooter_IsLeftOut")]
    public void RdlConverter_EmptyPageFooter(bool suppressed, bool expectFooter)
    {
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.PageFooter, HeightTwips = 600, Suppress = suppressed },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var footer = doc.Root.Element(ns + "PageFooter");

        if (!expectFooter)
        {
            Assert.That(footer, Is.Null);
            return;
        }
        Assert.That(footer, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(footer!.Element(ns + "Height")!.Value, Is.EqualTo("30pt"));
            Assert.That(footer.Element(ns + "ReportItems"), Is.Null, "no empty ReportItems");
        });
    }

    // An object without the flag gets no strips, which is most of them.
    [Test]
    public void RdlConverter_NoDropShadow_EmitsNoStrips()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Plain",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 480,
                    Objects = [new TextObject { Name = "Plain", Text = "No box",
                        Bounds = new(0, 0, 2880, 720) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        Assert.That(doc.Descendants(ns + "Rectangle")
            .Any(r => (r.Attribute("Name")?.Value ?? "").StartsWith("shadow_")), Is.False);
    }

    // A selection formula that tests a report parameter cannot select anything when no value
    // for it arrives, and parameters are a render-time concern here so the converter cannot
    // know whether one will. Left as a bare comparison the filter is false for every row and
    // the report renders an empty page - which is what three of the visual suite's
    // fixture-backed reports did. An unanswered parameter now makes the filter inert; a
    // supplied one still filters.
    [Test]
    public void RdlConverter_SelectionOnAParameter_IsInertWhenNoValueArrives()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Ranged",
            RecordSelectionFormula = "{Orders.Order Amount} = {?Amt_Range}",
            Fields =
            [
                new DatabaseField { Name = "Order Amount", ColumnName = "Order Amount", DataType = "Currency" },
                new ParameterField { Name = "Amt_Range", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Order Amount", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var expr = doc.Descendants(ns + "FilterExpression").Single().Value;

        Assert.That(expr, Does.Contain("IsNothing(Parameters!Amt_Range.Value)"),
            "an unanswered parameter short-circuits the comparison");
        Assert.That(expr, Does.Contain("OrElse"));
        Assert.That(expr, Does.Contain("Fields!Order_Amount.Value = Parameters!Amt_Range.Value"),
            "and the comparison itself is still there, so a supplied value still filters");
    }

    // The guard is only for parameters. A selection formula comparing a field to a literal
    // means what it says whether or not anyone supplies anything, and must keep filtering.
    [Test]
    public void RdlConverter_SelectionOnALiteral_IsLeftAlone()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Literal",
            RecordSelectionFormula = "{Orders.Order Amount} > 1000",
            Fields = [new DatabaseField { Name = "Order Amount", ColumnName = "Order Amount", DataType = "Currency" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Order Amount", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var expr = doc.Descendants(ns + "FilterExpression").Single().Value;
        Assert.That(expr, Does.Not.Contain("IsNothing"));
    }

    [Test]
    public void RdlConverter_AtFormula_FieldObject_ResolvesToFormulaField()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Formula Test",
            Fields =
            [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new FormulaField { Name = "DiscountedAmount", FormulaText = "{Orders.Amount} * 0.9", Syntax = FormulaSyntax.Crystal }
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "@DiscountedAmount", Bounds = new(1440, 0, 2880, 240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("DiscountedAmount"), "Formula field should appear in DataSet");
        Assert.That(rdl, Does.Not.Contain("[@DiscountedAmount]"),
            "@-prefixed formula FieldObject should resolve to Fields!DiscountedAmount.Value, not a placeholder");
        Assert.That(rdl, Does.Contain("Fields!DiscountedAmount.Value"),
            "FieldObject with @FormulaName should emit =Fields!DiscountedAmount.Value");
    }

    [Test]
    public void RdlConverter_HashRunningTotal_FieldObject_EmitsEmptyNotBrokenRef()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Running Total Test",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "#RunTotal", Bounds = new(1440, 0, 2880, 240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Not.Contain("Fields!_RunTotal.Value"),
            "#-prefixed running total should not emit a _-prefixed field reference");
        Assert.That(rdl, Does.Not.Contain("Fields!RunTotal.Value"),
            "Running total with no DataSet entry should not emit a Fields! reference");
    }

    [Test]
    public void RdlConverter_ReportComments_EmptyWhenNoComments()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Comments Test",
            ReportComments = "",     // no comments set
            Sections =
            [
                new Section
                {
                    Type = SectionType.PageHeader, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { Name = "CommentsField", FieldName = "Report Comments", Bounds = new(0,0,1440,240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Not.Contain("[Report Comments]"),
            "Empty ReportComments should not emit a placeholder");
        // An expression for the empty string. A bare "" is a literal, and the engine printed
        // the two quote marks themselves: Top5USA-piechart showed "" under its description.
        Assert.That(rdl, Does.Contain("<Value>=\"\"</Value>"),
            "Empty ReportComments should emit an empty-string expression");
    }

    // A running total that adds only the records its formula picks, and starts again with each
    // group, runs over those records within the group.
    [Test]
    public void RdlConverter_RunningTotal_KeepsItsConditionAndItsGroup()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Running Total Conditions",
            Fields =
            [
                new DatabaseField { Name = "Lines.Account", TableName = "Lines", ColumnName = "Account", DataType = "String" },
                new DatabaseField { Name = "Lines.Kind", TableName = "Lines", ColumnName = "Kind", DataType = "String" },
                new DatabaseField { Name = "Lines.Amount", TableName = "Lines", ColumnName = "Amount", DataType = "Float64" },
                new RunningTotalField { Name = "Fees", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum,
                    EvaluateOn = RunningTotalCondition.OnFormula, EvaluateCondition = "{Lines.Kind} = 'Fee'",
                    ResetOn = RunningTotalCondition.OnChangeOfGroup, ResetGroup = 1 },
                new RunningTotalField { Name = "Lines", SummarizedFieldName = "Amount", Function = AggregateFunction.Count,
                    EvaluateOn = RunningTotalCondition.OnFormula, EvaluateCondition = "{Lines.Amount} > 0" },
                // Not true or false: every record is added, as before.
                new RunningTotalField { Name = "Everything", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum,
                    EvaluateOn = RunningTotalCondition.OnFormula, EvaluateCondition = "{Lines.Amount}" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Account", TableName = "Lines" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Lines.Account", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Lines.Amount", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.GroupFooter, GroupLevel = 0, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "#Fees", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain(
            "<Value>=RunningValue(IIf((RTrim(Fields!Kind.Value) = \"Fee\"), Fields!Amount.Value, 0), Sum, \"Group1\")</Value>"));
        Assert.That(rdl, Does.Contain(
            "<Value>=RunningValue(IIf((Fields!Amount.Value &gt; 0), Fields!Amount.Value, Nothing), Count, Nothing)</Value>"));
        Assert.That(rdl, Does.Contain("<Value>=RunningValue(Fields!Amount.Value, Sum, Nothing)</Value>"));
    }

    // A running total that starts again when a field changes runs within the group on that
    // field, and so does one whose reset formula only asks whether fields changed from the
    // record before. A field the report does not group on each value of, or any other formula,
    // has no group to run within, and the total runs over the whole report as before.
    [TestCase("OnChangeOfField", "Lines.Account", "\"Group2\"")]
    [TestCase("OnChangeOfField", "{Lines.Region}", "\"Group1\"")]
    [TestCase("OnFormula", "{Lines.Account} <> Previous ({Lines.Account})", "\"Group2\"")]
    [TestCase("OnFormula", "Previous({Lines.Region}) <> {Lines.Region} or ({Lines.Account} <> Previous({Lines.Account}))", "\"Group2\"")]
    [TestCase("OnChangeOfField", "Lines.Kind", "Nothing")]
    [TestCase("OnChangeOfField", "Lines.Posted", "Nothing")]
    [TestCase("OnFormula", "{Lines.Kind} <> Previous ({Lines.Kind})", "Nothing")]
    [TestCase("OnFormula", "{Lines.Account} > Previous ({Lines.Account})", "Nothing")]
    [TestCase("OnFormula", "{Lines.Amount} > 100 or {Lines.Account} <> Previous ({Lines.Account})", "Nothing")]
    public void RdlConverter_RunningTotal_ResetOnAFieldRunsWithinTheGroupOnIt(string resetOn, string condition, string scope)
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Running Total Resets",
            Fields =
            [
                new DatabaseField { Name = "Lines.Region", TableName = "Lines", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Lines.Account", TableName = "Lines", ColumnName = "Account", DataType = "String" },
                new DatabaseField { Name = "Lines.Kind", TableName = "Lines", ColumnName = "Kind", DataType = "String" },
                new DatabaseField { Name = "Lines.Posted", TableName = "Lines", ColumnName = "Posted", DataType = "DateTime" },
                new DatabaseField { Name = "Lines.Amount", TableName = "Lines", ColumnName = "Amount", DataType = "Float64" },
                new RunningTotalField { Name = "Run", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum,
                    ResetOn = Enum.Parse<RunningTotalCondition>(resetOn), ResetCondition = condition }
            ],
            Groups =
            [
                new GroupDefinition { Level = 0, FieldName = "Region", TableName = "Lines" },
                new GroupDefinition { Level = 1, FieldName = "Account", TableName = "Lines" },
                new GroupDefinition { Level = 2, FieldName = "Posted", TableName = "Lines", Condition = GroupCondition.Monthly }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "#Run", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain($"<Value>=RunningValue(Fields!Amount.Value, Sum, {scope})</Value>"));
    }

    private static ReportDefinition EvaluatedOnGroupReport(params RunningTotalField[] totals) => new()
    {
        ReportTitle = "Running Total Evaluation",
        Fields =
        [
            new DatabaseField { Name = "Lines.Region", TableName = "Lines", ColumnName = "Region", DataType = "String" },
            new DatabaseField { Name = "Lines.Posted", TableName = "Lines", ColumnName = "Posted", DataType = "DateTime" },
            new DatabaseField { Name = "Lines.Account", TableName = "Lines", ColumnName = "Account", DataType = "String" },
            new DatabaseField { Name = "Lines.Amount", TableName = "Lines", ColumnName = "Amount", DataType = "Float64" },
            .. totals
        ],
        Groups =
        [
            new GroupDefinition { Level = 0, FieldName = "Region", TableName = "Lines" },
            new GroupDefinition { Level = 1, FieldName = "Posted", TableName = "Lines", Condition = GroupCondition.Monthly },
            new GroupDefinition { Level = 2, FieldName = "Account", TableName = "Lines" }
        ],
        Sections =
        [
            new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [.. totals.Select((t, i) => (ReportObject)new FieldObject { FieldName = "#" + t.Name, Bounds = new(i * 1440, 0, 1440, 240) })] }
        ]
    };

    private static RunningTotalField EvaluatedOnGroup(string name, AggregateFunction function, int group, int reset) => new()
    {
        Name = name, SummarizedFieldName = "Amount", Function = function,
        EvaluateOn = RunningTotalCondition.OnChangeOfGroup, EvaluateGroup = group,
        ResetOn = reset > 0 ? RunningTotalCondition.OnChangeOfGroup : RunningTotalCondition.None, ResetGroup = reset
    };

    // A running total evaluated on a change of group adds the first record of each instance of
    // the group: within the group it starts again in, the group's first record, or one where the
    // value of a group inside that one (a date group's month) differs from the record before's.
    // Within the group it is evaluated on, only the first record is added.
    [Test]
    public void RdlConverter_RunningTotal_EvaluatedOnAGroupAddsEachInstancesFirstRecord()
    {
        var report = EvaluatedOnGroupReport(
            EvaluatedOnGroup("PerRegion", AggregateFunction.Sum, 3, 1),
            EvaluatedOnGroup("PerAccount", AggregateFunction.Sum, 3, 3),
            EvaluatedOnGroup("Accounts", AggregateFunction.Count, 3, 1),
            EvaluatedOnGroup("Mean", AggregateFunction.Average, 3, 1));

        string rdl = new RdlConverter().Convert(report);

        const string month = "IIf(IsNothing(DateSerial(Year(Fields!Posted.Value), Month(Fields!Posted.Value), 1)), \"N\", "
            + "\"V\" &amp; CStr(DateSerial(Year(Fields!Posted.Value), Month(Fields!Posted.Value), 1)))";
        const string account = "IIf(IsNothing(Fields!Account.Value), \"N\", \"V\" &amp; CStr(Fields!Account.Value))";
        string inner = $"{month} &amp; ChrW(30) &amp; {account}";
        Assert.That(rdl, Does.Contain($"<Value>=RowNumber(\"Group1\") = 1 OrElse {inner} &lt;&gt; Previous({inner}, \"Group1\")</Value>"));
        Assert.That(rdl, Does.Contain("<Value>=RowNumber(\"Group3\") = 1</Value>"));
        Assert.That(rdl, Does.Contain(
            "<Value>=RunningValue(IIf(Fields!CrystalGroup3StartIn1.Value, Fields!Amount.Value, 0), Sum, \"Group1\")</Value>"));
        Assert.That(rdl, Does.Contain(
            "<Value>=RunningValue(IIf(Fields!CrystalGroup3StartIn3.Value, Fields!Amount.Value, 0), Sum, \"Group3\")</Value>"));
        string count = "RunningValue(IIf(Not Fields!CrystalGroup3StartIn1.Value, Nothing, Fields!Amount.Value), Count, \"Group1\")";
        Assert.That(rdl, Does.Contain($"<Value>={count}</Value>"));
        Assert.That(rdl, Does.Contain(
            $"<Value>=RunningValue(IIf(Fields!CrystalGroup3StartIn1.Value, Fields!Amount.Value, 0), Sum, \"Group1\") / IIf({count} &gt; 0, {count}, 1)</Value>"));
        // One field for each group and the group the total starts again in, however many read it.
        foreach (string field in new[] { "CrystalGroup3StartIn1", "CrystalGroup3StartIn3" })
            Assert.That(System.Text.RegularExpressions.Regex.Matches(rdl, $"<Field Name=\"{field}\">").Count, Is.EqualTo(1), field);
    }

    // A total that cannot be told from its group's records affordably or at all adds every
    // record, as before: one over the whole report, a maximum (the engine's running maximum
    // keeps only the row it was last read at), one that starts again inside the group it is
    // evaluated on, and one on a group in a specified order, whose named groups are not printed.
    [TestCase("Sum", 0, false)]
    [TestCase("Count", 0, false)]
    [TestCase("Maximum", 1, false)]
    [TestCase("Sum", 3, false)]
    [TestCase("Sum", 1, true)]
    public void RdlConverter_RunningTotal_EvaluatedOnAGroupItCannotTellAddsEveryRecord(string function, int reset, bool specified)
    {
        var report = EvaluatedOnGroupReport(EvaluatedOnGroup("Run", Enum.Parse<AggregateFunction>(function), 2, reset));
        if (specified)
            report.Groups[1] = new GroupDefinition { Level = 1, FieldName = "Posted", TableName = "Lines", SortOrder = GroupSortOrder.Specified };

        string rdl = new RdlConverter().Convert(report);

        string fn = function switch { "Maximum" => "Max", "Count" => "Count", _ => "Sum" };
        string scope = reset > 0 ? $"\"Group{reset}\"" : "Nothing";
        Assert.That(rdl, Does.Contain($"<Value>=RunningValue(Fields!Amount.Value, {fn}, {scope})</Value>"));
        Assert.That(rdl, Does.Not.Contain("CrystalGroup2Start"));
    }

    [Test]
    public void RdlConverter_RunningTotalField_EmitsDataSetFieldWithRunningValue()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Running Total Test",
            Fields =
            [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new RunningTotalField { Name = "RunTotal", SummarizedFieldName = "Amount", Function = AggregateFunction.Sum }
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "#RunTotal", Bounds = new(1440,0,2880,240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("RunTotal"), "RunningTotalField should appear in DataSet as Field Name='RunTotal'");
        Assert.That(rdl, Does.Contain("RunningValue"), "RunningTotalField should emit RunningValue() SSRS expression");
        Assert.That(rdl, Does.Contain("Fields!RunTotal.Value"), "#RunTotal FieldObject should resolve to Fields!RunTotal.Value");
        Assert.That(rdl, Does.Not.Contain("Fields!_RunTotal.Value"), "Should not emit _-prefixed bad reference");
    }

    [Test]
    public void RdlConverter_TextObjectTableDotColumn_ResolvesToFieldValue()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Dot Ref Test",
            Fields = [new DatabaseField { Name = "Customer Name", ColumnName = "Customer Name", DataType = "String" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.ReportHeader, HeightTwips = 240,
                    Objects =
                    [
                        new TextObject { Name = "T1", Text = "Name: {Customer.Customer Name}", Bounds = new(0,0,2880,240) }
                    ]
                }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("Fields!Customer_Name.Value"),
            "{Table.Column} ref in TextObject should resolve to the column's DataSet field");
        Assert.That(rdl, Does.Not.Contain("{Customer.Customer Name}"),
            "Unresolved brace ref should not appear as a literal in the output");
    }

    [Test]
    public void FormulaTranspiler_FieldRefWithSpaces_SanitizesIdentifier()
    {
        // Crystal field names with spaces/parens (e.g. "Price (SRP)", "Order Amount")
        // must be sanitized in transpiled RDL so Fields! refs don't have spaces
        var formula = new FormulaField
        {
            Name = "Discounted",
            FormulaText = "{Product.Price (SRP)} * 0.90",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Not.Contain("Fields!Price (SRP).Value"),
            "Field name with parentheses must be sanitized");
        Assert.That(result, Does.Contain("Fields!Price__SRP_.Value"),
            "Sanitized name replaces non-alphanumeric chars with underscores");
    }

    [Test]
    public void FormulaTranspiler_FieldRefWithSpacesInColumnName_SanitizesIdentifier()
    {
        var formula = new FormulaField
        {
            Name = "Expr",
            FormulaText = "{Orders.Order Amount} + 1",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Not.Contain("Fields!Order Amount.Value"),
            "Field name with space must be sanitized");
        Assert.That(result, Does.Contain("Fields!Order_Amount.Value"));
    }

    [Test]
    public void FormulaTranspiler_CrystalColorConstants_MappedToCssStrings()
    {
        var formula = new FormulaField
        {
            Name = "Color",
            FormulaText = "If {Orders.Amount} < 1000 Then crRed Else crBlack",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Contain("\"Red\""), "crRed should map to the CSS color string \"Red\"");
        Assert.That(result, Does.Contain("\"Black\""), "crBlack should map to the CSS color string \"Black\"");
        Assert.That(result, Does.Not.Contain("crRed"), "Raw Crystal constant should not appear in output");
    }

    [Test]
    public void SanitizeName_DigitLeadingColumnName_GetsUnderscorePrefix()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "T",
            Fields = [new DatabaseField { Name = "1stQ", ColumnName = "1stQuarter", DataType = "Float64" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "1stQuarter", Bounds = new(0, 0, 1440, 240) }]
                }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("Name=\"1stQuarter\""),
            "Digit-leading name must not appear as XML attribute — invalid NCName");
        Assert.That(rdl, Does.Contain("Name=\"_1stQuarter\""),
            "Digit-leading name must be prefixed with underscore");
    }

    [Test]
    public void ResolveTextWithFieldRefs_ReportCommentsNonEmpty_EmitsLiteralValue()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "T",
            ReportComments = "Annual Summary",
            Fields = [],
            Sections =
            [
                new Section
                {
                    Type = SectionType.ReportHeader, HeightTwips = 240,
                    Objects = [new TextObject { Name = "T1", Text = "Report: {report comments}", Bounds = new(0, 0, 2880, 240) }]
                }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("Annual Summary"),
            "ReportComments value should appear in RDL output");
        Assert.That(rdl, Does.Not.Contain("{report comments}"),
            "Raw brace reference should not appear in output");
    }

    [Test]
    public void FormulaTranspiler_PiFunction_EmitsMathPIWithoutParens()
    {
        var formula = new FormulaField
        {
            Name = "CircleArea",
            FormulaText = "Pi() * 2",
            Syntax = FormulaSyntax.Crystal
        };

        string result = FormulaTranspiler.ToRdlExpression(formula);

        Assert.That(result, Does.Not.Contain("Math.PI()"),
            "Pi() must not emit Math.PI() — Math.PI is a property, not a method");
        Assert.That(result, Does.Contain("Math.PI"),
            "Pi() should emit Math.PI (no parentheses)");
    }

    [Test]
    public void RptParser_Parse_NonExistentFile_ReturnsFailed()
    {
        var result = RptParser.Parse("/nonexistent/path/to/file.rpt");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Errors, Is.Not.Empty);
    }

    [Test]
    public void RptParser_Parse_EmptyStream_ReturnsFailed()
    {
        using var ms = new System.IO.MemoryStream(Array.Empty<byte>());
        var result = RptParser.Parse(ms);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Errors, Is.Not.Empty);
    }

    [Test]
    public void RdlConverter_Convert_ProducesDeterministicOutput()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "T",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Integer" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }]
                }
            ]
        };

        string rdl1 = new RdlConverter().Convert(report);
        string rdl2 = new RdlConverter().Convert(report);

        Assert.That(rdl1, Is.EqualTo(rdl2), "Convert must produce identical output for identical input");
    }

    [Test]
    public void RdlConverter_DuplicateColumnNamesAcrossTables_DoesNotThrow()
    {
        // Real-world reports frequently join tables that expose identically named
        // columns (e.g. Customer.Prov and Vendor.Prov); the group-footer field map
        // must tolerate the duplicates instead of throwing on dictionary insert.
        var report = new ReportDefinition
        {
            ReportTitle = "Duplicate Columns",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Customer.Prov", TableName = "Customer", ColumnName = "Prov", DataType = "String" },
                new DatabaseField { Name = "Vendor.Prov", TableName = "Vendor", ColumnName = "Prov", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Customer", Bounds = new(0,0,2880,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }] }
            ]
        };

        string rdl = string.Empty;
        Assert.DoesNotThrow(() => rdl = new RdlConverter().Convert(report));
        Assert.That(rdl, Does.Contain("Sum(Fields!Amount.Value)"));
    }

    [Test]
    public void FormulaTranspiler_IsThreadSafe_UnderParallelUse()
    {
        // The Irony Parser carries per-parse mutable state; concurrent use of the
        // shared CrystalFormulaParser previously produced NullReferenceExceptions.
        string[] formulas =
        [
            "{Customer.FirstName} & ' ' & {Customer.LastName}",
            "If {Orders.Amount} > 1000 Then 'High' Else 'Low'",
            "ToText({Orders.Amount}, 2) & ' on ' & ToText(CurrentDate, 'yyyy-MM-dd')",
            "Sum({Orders.Amount}) / Count({Orders.OrderID})",
        ];

        Assert.DoesNotThrow(() =>
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var formula = new FormulaField
                {
                    Name = $"F{i}",
                    FormulaText = formulas[i % formulas.Length],
                    Syntax = FormulaSyntax.Crystal
                };
                string result = FormulaTranspiler.ToRdlExpression(formula);
                Assert.That(result, Is.Not.Empty);
            }));
    }

    [Test]
    public void RdlConverter_EmbeddedImage_EmitsEmbeddedImagesAndImageItem()
    {
        byte[] fakeBmp = [0x42, 0x4D, 0x01, 0x02, 0x03, 0x04];
        var report = new ReportDefinition
        {
            ReportTitle = "Logo Report",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section
                {
                    Type = SectionType.PageHeader, HeightTwips = 720,
                    Objects =
                    [
                        new ImageObject
                        {
                            Name = "Logo",
                            Source = ImageSourceKind.Embedded,
                            EmbeddingIndex = 1,
                            ImageData = fakeBmp,
                            MimeType = "image/bmp",
                            Bounds = new(0, 0, 1440, 720)
                        }
                    ]
                },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<EmbeddedImages>"));
        Assert.That(rdl, Does.Contain("EmbeddedImage1"));
        Assert.That(rdl, Does.Contain(System.Convert.ToBase64String(fakeBmp)));
        Assert.That(rdl, Does.Contain("<MIMEType>image/bmp</MIMEType>"));
        Assert.That(rdl, Does.Contain("<Source>Embedded</Source>"));
        Assert.That(rdl, Does.Contain("<Sizing>FitProportional</Sizing>"));
    }

    [Test]
    public void RdlConverter_DatabaseImageInDetails_EmitsImageTableCell()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Barcode Labels",
            Fields = [
                new DatabaseField { Name = "Code", ColumnName = "Code", DataType = "String" },
                new DatabaseField { Name = "barCode", ColumnName = "barCode", DataType = "String" }
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 720,
                    Objects =
                    [
                        new FieldObject { FieldName = "Code", Bounds = new(0, 0, 1440, 240) },
                        new ImageObject
                        {
                            Source = ImageSourceKind.Database,
                            FieldName = "barCode",
                            Bounds = new(1440, 0, 2880, 720)
                        }
                    ]
                }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Source>Database</Source>"));
        Assert.That(rdl, Does.Contain("=Fields!barCode.Value"));
        Assert.That(rdl, Does.Not.Contain("<EmbeddedImages>"),
            "Database-sourced images must not produce an EmbeddedImages block");

        // Cell count must match column count: 1 field column + 1 image column everywhere
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        int columnCount = doc.Descendants(ns + "TableColumn").Count();
        foreach (var cells in doc.Descendants(ns + "TableCells"))
            Assert.That(cells.Elements(ns + "TableCell").Count(), Is.EqualTo(columnCount),
                "every table row must have one cell per column");
    }

    [Test]
    public void RdlConverter_SubreportObject_EmitsSubreportWithPrefixedReportName()
    {
        var inner = new ReportDefinition
        {
            ReportTitle = "Inner",
            Fields = [new DatabaseField { Name = "X", ColumnName = "X", DataType = "String" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "X", Bounds = new(0, 0, 1440, 240) }] }]
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Sections =
            [
                new Section
                {
                    Type = SectionType.ReportFooter, HeightTwips = 720,
                    Objects =
                    [
                        new SubreportObject
                        {
                            Name = "Subreport1",
                            SubreportName = "Subreport1",
                            SubdocumentIndex = 1,
                            Report = inner,
                            Bounds = new(0, 0, 5760, 720)
                        }
                    ]
                }
            ]
        };

        string rdl = new RdlConverter().Convert(report, "Parent_");

        Assert.That(rdl, Does.Contain("<Subreport Name=\"Subreport1\">"));
        Assert.That(rdl, Does.Contain("<ReportName>Parent_Subreport1</ReportName>"),
            "ReportName must carry the companion-file prefix");

        // An unparsed subreport (Report == null) must be skipped, not crash
        var broken = new ReportDefinition
        {
            ReportTitle = "Parent2",
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 240,
                Objects = [new SubreportObject { SubreportName = "S", SubdocumentIndex = 9 }] }]
        };
        string rdl2 = string.Empty;
        Assert.DoesNotThrow(() => rdl2 = new RdlConverter().Convert(broken));
        Assert.That(rdl2, Does.Not.Contain("<Subreport"));
    }

    [Test]
    public void RdlConverter_SummaryFieldObjects_EmitTheirAggregateFunction()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Aggregates",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Location", ColumnName = "Location", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                // Crystal-style: Count summary placed in the group header
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Location",
                        SummaryFunction = AggregateFunction.Count, Bounds = new(1440,0,1440,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) },
                        new FieldObject { FieldName = "Location", Bounds = new(2880,0,1440,240) }
                    ]
                },
                // Non-Sum aggregate in the group footer over a string column
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Location",
                            SummaryFunction = AggregateFunction.DistinctCount, Bounds = new(1440,0,1440,240) },
                        new FieldObject { FieldName = "Amount",
                            SummaryFunction = AggregateFunction.Maximum, Bounds = new(2880,0,1440,240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("=CountDistinct(Fields!Location.Value)"),
            "group footer DistinctCount summary must not fall back to Sum");
        Assert.That(rdl, Does.Contain("=Max(Fields!Amount.Value)"));
        Assert.That(rdl, Does.Contain("=Count(Fields!Location.Value)"),
            "group header Count summary should fill the matching header column");
        Assert.That(rdl, Does.Not.Contain("=Sum(Fields!Location.Value)"),
            "a string column with an explicit function must never be Summed");
    }

    [Test]
    public void RdlConverter_PercentageSummary_EmitsDivisionByDataSetTotal()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Percentages",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.ReportFooter, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount",
                        SummaryFunction = AggregateFunction.Percentage, Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain(
            "=Sum(Fields!Amount.Value) / Sum(Fields!Amount.Value, \"DataSet1\") * 100"));
    }

    [Test]
    public void RdlConverter_PercentageSummary_SharingColumnWithPlainSummary_IsNotDropped()
    {
        // Crystal allows two summaries of the same column side by side in one group
        // footer (e.g. a Sum and a Percentage-of-total) — the table-column model only
        // has one cell per column name, so the second FieldObject can't be placed there
        // and must fall back to the same "leftover positioned item" mechanism already
        // used for subreports/images/charts that don't fit.
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }
                    ] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 480, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "Amount", SummaryFunction = AggregateFunction.Sum, Bounds = new(1440,0,1440,240) },
                        new FieldObject { FieldName = "Amount", SummaryFunction = AggregateFunction.Percentage, Bounds = new(0,240,1440,240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("=Sum(Fields!Amount.Value)</Value>"),
            "the plain Sum summary must still be written");
        Assert.That(rdl, Does.Contain(
            "=Sum(Fields!Amount.Value) / Sum(Fields!Amount.Value, \"DataSet1\") * 100"),
            "the Percentage summary must not be silently dropped");

        // No corruption from the band having more objects than cells.
        AssertEveryRowSpansAllColumns(System.Xml.Linq.XDocument.Parse(rdl));
    }

    [Test]
    public void RdlConverter_FormulaFieldText_IsMutable_AndReflectedOnNextConvert()
    {
        // FormulaField.FormulaText/Syntax used to be init-only; a runtime-override caller
        // (e.g. Crystal's DataDefinition.FormulaFields[x].Text = ...) needs to mutate an
        // already-parsed model in place before a later Convert() call.
        var formula = new FormulaField { Name = "Greeting", FormulaText = "\"Hello\"" };
        var report = new ReportDefinition
        {
            ReportTitle = "Formulas",
            Fields = [formula],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "@Greeting", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string before = new RdlConverter().Convert(report);
        Assert.That(before, Does.Contain("<Value>=\"Hello\"</Value>"));

        formula.FormulaText = "\"Goodbye\"";
        string after = new RdlConverter().Convert(report);
        Assert.That(after, Does.Contain("<Value>=\"Goodbye\"</Value>"));
        Assert.That(after, Does.Not.Contain("Hello"));
    }

    [Test]
    public void RdlConverter_SortFields_EmitDetailsSortExpressions()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Sorted",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            SortFields = [new SortField { FieldName = "Amount", Direction = SortDirection.Descending }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<SortExpression>=Fields!Amount.Value</SortExpression>"));
        Assert.That(rdl, Does.Contain("<Direction>Descending</Direction>"));
    }

    [Test]
    public void RdlConverter_SortFields_Empty_EmitsNoSortingElement()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Unsorted",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("<Sorting>"));
    }

    [Test]
    public void RdlConverter_ObjectSuppressOverride_TrueHidesItem_FalseUnhidesEvenWhenSectionSuppressed()
    {
        var hiddenText = new TextObject { Name = "Watermark", Text = "DRAFT",
            Bounds = new(0, 0, 1440, 240), SuppressOverride = true };
        var forcedVisibleText = new TextObject { Name = "Notice", Text = "Always shown",
            Bounds = new(0, 240, 1440, 240), SuppressOverride = false };
        var report = new ReportDefinition
        {
            ReportTitle = "Overrides",
            Sections = [new Section
            {
                Type = SectionType.ReportHeader, HeightTwips = 480,
                // The section itself is statically suppressed; a false override must still win.
                Suppress = true,
                Objects = [hiddenText, forcedVisibleText]
            }]
        };

        string rdl = new RdlConverter().Convert(report);
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;

        var textboxes = doc.Descendants(ns + "Textbox").ToList();
        var visibleBox = textboxes.Single(t => (string?)t.Attribute("Name") == "Notice");

        // Suppressed whatever the data, it is not written at all, beside an object that prints.
        Assert.That(textboxes.Where(t => (string?)t.Attribute("Name") == "Watermark"), Is.Empty);
        Assert.That(visibleBox.Descendants(ns + "Hidden").Single().Value, Is.EqualTo("false"));
    }

    [Test]
    public void RdlConverter_SubreportLookup_FindsNestedSubreportByName()
    {
        var innermost = new ReportDefinition { ReportTitle = "Innermost", Sections = [] };
        var middle = new ReportDefinition
        {
            ReportTitle = "Middle",
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new SubreportObject { SubreportName = "Innermost", Report = innermost }] }]
        };
        var outer = new ReportDefinition
        {
            ReportTitle = "Outer",
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new SubreportObject { SubreportName = "Middle", Report = middle }] }]
        };

        Assert.That(outer.FindSubreport("Middle"), Is.SameAs(middle));
        Assert.That(outer.FindSubreport("Innermost"), Is.SameAs(innermost),
            "lookup must recurse into a subreport's own nested subreports");
        Assert.That(outer.FindSubreport("DoesNotExist"), Is.Null);
    }

    [Test]
    public void RdlConverter_TableGroup_EmitsSortingElement_NotSortExpressions()
    {
        // Found via real-engine render verification: TableGroup's own sort key used to be
        // emitted as a bare <SortExpressions> directly under <TableGroup>, which isn't a
        // schema element the engine's TableGroup parser recognizes at all (only Grouping/
        // Sorting/Header/Footer/Visibility are) — it was silently ignored as an "unknown
        // element" warning (Severity 4, not Error/Fatal, so no existing test ever caught
        // it), meaning every grouped report's sort direction was dropped at render time.
        var report = new ReportDefinition
        {
            ReportTitle = "GroupSort",
            Fields = [new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Descending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Region", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("<SortExpressions>"),
            "TableGroup has no such element; only <Sorting><SortBy> is schema-valid there");
        Assert.That(rdl, Does.Contain("<SortExpression>=Fields!Region.Value</SortExpression>"));
        Assert.That(rdl, Does.Contain("<Direction>Descending</Direction>"));

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var tableGroup = doc.Descendants(ns + "TableGroup").Single();
        Assert.That(tableGroup.Element(ns + "Sorting"), Is.Not.Null,
            "sorting must be a direct child of TableGroup, not floating loose");
    }

    [Test]
    public void RdlConverter_SuppressFormula_EmitsHiddenExpression_AndOverridesStaticFlag()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Conditional",
            Fields = [
                new DatabaseField { Name = "Comments", ColumnName = "Comments", DataType = "String" }
            ],
            Sections =
            [
                new Section
                {
                    Type = SectionType.Details, HeightTwips = 240,
                    // Crystal keeps the stale static bit set when a formula is attached —
                    // the formula must win, otherwise the row would be permanently hidden.
                    Suppress = true,
                    SuppressFormula = "{JournalEntry.Comments} = ''",
                    Objects = [new FieldObject { FieldName = "Comments", Bounds = new(0, 0, 2880, 240) }]
                }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        // A null field compares as '' in Crystal, so the test is "null or empty".
        Assert.That(rdl, Does.Contain("<Hidden>=(IsNothing(Fields!Comments.Value) Or RTrim(Fields!Comments.Value) = \"\")</Hidden>"));
        Assert.That(rdl[..rdl.IndexOf("</Details>", StringComparison.Ordinal)], Does.Not.Contain("<Hidden>true</Hidden>"),
            "the suppress formula must supersede the static flag");
    }

    [Test]
    public void RdlConverter_SubreportParameterLinks_BindByNamingConvention()
    {
        var inner = new ReportDefinition
        {
            ReportTitle = "Inner",
            Fields = [
                new ParameterField { Name = "@Location", DataType = "String" },
                new ParameterField { Name = "Pm-Orders.Customer", DataType = "String" },
                new ParameterField { Name = "CompletelyUnknown", DataType = "String" }
            ],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }]
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Fields = [
                new FormulaField { Name = "Location", FormulaText = "'x'", Syntax = FormulaSyntax.Crystal },
                new DatabaseField { Name = "Orders.Customer", TableName = "Orders", ColumnName = "Customer", DataType = "String" }
            ],
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 720,
                Objects = [new SubreportObject { Name = "Sub1", SubreportName = "Sub1",
                    SubdocumentIndex = 1, Report = inner, Bounds = new(0, 0, 5760, 720) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Parameter Name=\"_Location\">"),
            "@-named child parameter binds to the same-named parent formula field");
        Assert.That(rdl, Does.Contain("<Value>=Fields!Location.Value</Value>"));
        Assert.That(rdl, Does.Contain("<Parameter Name=\"Pm_Orders_Customer\">"),
            "Pm- prefixed child parameter binds to the parent column it names");
        Assert.That(rdl, Does.Contain("<Value>=Fields!Customer.Value</Value>"));
        Assert.That(rdl, Does.Not.Contain("CompletelyUnknown"),
            "unresolvable child parameters stay promptable (no binding emitted)");
    }

    // A subreport's parameter takes the parent's parameter of its name, or the one a
    // "Pm-?" link names, before a column or a formula that shares the name.
    [Test]
    public void RdlConverter_SubreportParameter_TakesTheParentsParameterOfItsName()
    {
        var inner = new ReportDefinition
        {
            ReportTitle = "Inner",
            Fields = [
                new ParameterField { Name = "@Invoice", DataType = "Boolean" },
                new ParameterField { Name = "Pm-?Period End", DataType = "DateTime" }
            ],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }]
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Fields = [
                new ParameterField { Name = "@Invoice", DataType = "Boolean" },
                new ParameterField { Name = "Period End", DataType = "DateTime" },
                new DatabaseField { Name = "List.Invoice", TableName = "List", ColumnName = "Invoice", DataType = "String" },
                new FormulaField { Name = "Period End", FormulaText = "'Ending ' + ToText({?Period End})", Syntax = FormulaSyntax.Crystal }
            ],
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 720,
                Objects = [new SubreportObject { Name = "Sub1", SubreportName = "Sub1",
                    SubdocumentIndex = 1, Report = inner, Bounds = new(0, 0, 5760, 720) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Match(@"<Parameter Name=""_Invoice"">\s*<Value>=Parameters!_Invoice\.Value</Value>"));
        Assert.That(rdl, Does.Match(@"<Parameter Name=""Pm__Period_End"">\s*<Value>=Parameters!Period_End\.Value</Value>"));
    }

    // Matched by name alone, a column feeds a parameter of its own kind only: a Boolean
    // @Account is left to its default, not given the parent's account numbers.
    [Test]
    public void RdlConverter_SubreportParameter_IsNotFedAColumnOfAnotherKind()
    {
        var inner = new ReportDefinition
        {
            ReportTitle = "Inner",
            Fields = [new ParameterField { Name = "@Account", DataType = "Boolean" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }]
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            Fields = [new DatabaseField { Name = "GL.Account", TableName = "GL", ColumnName = "Account", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 720,
                Objects = [new SubreportObject { Name = "Sub1", SubreportName = "Sub1",
                    SubdocumentIndex = 1, Report = inner, Bounds = new(0, 0, 5760, 720) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("<Parameter Name=\"_Account\">"));
    }

    [Test]
    public void RdlConverter_RepeatGroupHeader_EmitsTrueOnGroupHeaderRow()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    RepeatGroupHeader = true }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<RepeatOnNewPage>true</RepeatOnNewPage>"));
    }

    // Keep Group Together moves a group that does not fit in what is left of the page to the
    // next one, which the engine does for a TableGroup marked KeepTogether: on the details
    // table's groups, and on the groups of a report with nothing in its details.
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void RdlConverter_KeepGroupTogether_MarksTheTableGroup(bool keepTogether, bool details)
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Kept",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending,
                KeepTogether = keepTogether }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = details ? [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] : [] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var group = doc.Descendants(ns + "TableGroup").Single();
        Assert.That(group.Element(ns + "KeepTogether")?.Value, Is.EqualTo(keepTogether ? "true" : null));
    }

    [Test]
    public void RdlConverter_NewPageBeforeFormula_EmitsPageBreakConditionOnGrouping()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    // Crystal keeps the stale static bit set alongside an attached formula —
                    // same precedence rule as section suppression.
                    NewPageBefore = true,
                    NewPageBeforeFormula = "{Region.Code} = 'US'" }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var grouping = doc.Descendants(ns + "TableGroup").First().Element(ns + "Grouping")!;

        Assert.That(grouping.Element(ns + "PageBreakAtStart")?.Value, Is.EqualTo("true"));
        Assert.That(grouping.Element(ns + "PageBreakCondition")?.Value,
            Is.EqualTo("=(RTrim(Fields!Code.Value) = \"US\")"));
    }

    [Test]
    public void RdlConverter_SubreportInGroupFooterOfTabularReport_IsNotDropped()
    {
        var inner = new ReportDefinition
        {
            ReportTitle = "Inner",
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }]
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 480, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Amount", SummaryFunction = AggregateFunction.Sum, Bounds = new(0,0,1440,240) },
                        new SubreportObject { Name = "Detail_Sub", SubreportName = "Detail_Sub",
                            SubdocumentIndex = 1, Report = inner, Bounds = new(0,240,5760,240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Subreport Name=\"Detail_Sub\">"),
            "a subreport in a group footer of a tabular report must not be dropped");

        // Every table row must still have one cell per column
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        int columnCount = doc.Descendants(ns + "TableColumn").Count();
        foreach (var cells in doc.Descendants(ns + "TableCells"))
            Assert.That(cells.Elements(ns + "TableCell").Count(), Is.EqualTo(columnCount));
    }

    [Test]
    public void RdlConverter_ChartInGroupFooterWithEmptyCell_PlacedInCell()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 480, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Amount", SummaryFunction = AggregateFunction.Sum, Bounds = new(0,0,1440,240) },
                        new ChartObject { Name = "Chart1", Kind = ChartKind.Pie,
                            CategoryFields = ["Region"], SeriesField = "Amount",
                            SeriesFunction = AggregateFunction.Sum, Bounds = new(0,240,5760,240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Chart Name=\"Chart1\">"),
            "a chart in a group footer of a tabular report must not be dropped");

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        int columnCount = doc.Descendants(ns + "TableColumn").Count();
        foreach (var cells in doc.Descendants(ns + "TableCells"))
            Assert.That(cells.Elements(ns + "TableCell").Count(), Is.EqualTo(columnCount));
    }

    [Test]
    public void RdlConverter_ChartInGroupFooterAllCellsFilled_StaysInItsBand()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Tabular",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                // Every detail column is also present in the group footer, so no
                // empty cell exists for the chart to fall into — the band is written
                // free-form instead, with the chart at its own position in it.
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1440,0,1440,240) }
                    ] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 480, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) },
                        new FieldObject { FieldName = "Amount", SummaryFunction = AggregateFunction.Sum, Bounds = new(1440,0,1440,240) },
                        new ChartObject { Name = "Chart1", Kind = ChartKind.Column,
                            CategoryFields = ["Region"], SeriesField = "Amount",
                            SeriesFunction = AggregateFunction.Sum, Bounds = new(0,240,5760,480) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Chart Name=\"Chart1\">"),
            "a chart with no available empty cell must still be emitted, not dropped");

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var footer = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Footer")!;
        Assert.That(footer.Descendants(ns + "Chart").Count(), Is.EqualTo(1),
            "the chart belongs to the group footer's band");
        Assert.That(footer.Element(ns + "TableRows")!.Elements(ns + "TableRow").Count(), Is.EqualTo(1),
            "one band row, as tall as the section, not a second row repeating its height");
        AssertEveryRowSpansAllColumns(doc);
    }

    [Test]
    public void RdlConverter_GroupFooterWithMoreObjectsThanCells_IsOneRowOfItsOwnHeight()
    {
        // A form-style group footer: a one-column table (its details are a single field)
        // and a footer holding many labels and fields. Written cell by cell, every object
        // the one cell could not hold got a row of its own as tall as the whole section,
        // so the footer printed once per object instead of once per group.
        var footerObjects = new List<ReportObject>();
        for (int i = 0; i < 6; i++)
            footerObjects.Add(new TextObject { Name = $"Label{i}", Text = $"Label {i}",
                Bounds = new(0, i * 1440, 1440, 240) });
        footerObjects.Add(new FieldObject { Name = "RegionValue", FieldName = "Region",
            Bounds = new(2880, 0, 1440, 240) });
        var report = new ReportDefinition
        {
            ReportTitle = "Form",
            Fields = [new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" }],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Region", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Region", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 9000, GroupLevel = 0,
                    Objects = footerObjects }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var rows = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Footer")!
            .Element(ns + "TableRows")!.Elements(ns + "TableRow").ToList();

        Assert.That(rows, Has.Count.EqualTo(1), "one row for one section");
        Assert.That(rows[0].Element(ns + "Height")!.Value, Is.EqualTo("450pt"),
            "the row is as tall as the section, once");
        for (int i = 0; i < 6; i++)
            Assert.That(rows[0].ToString(), Does.Contain($"Label {i}"), $"Label {i} must not be dropped");
        Assert.That(rows[0].ToString(), Does.Contain("Fields!Region.Value"),
            "the footer's own field, which matches no column, must not be dropped");
        AssertEveryRowSpansAllColumns(doc);
    }

    [Test]
    public void RdlConverter_GroupLevelWithTwoHeaderSections_EachLevelKeepsItsOwnHeader()
    {
        // Level 0 has two header sections (a, b) and level 1 has one. Picking a level's
        // section by its place in the list gave level 1 the section "0b" and dropped "1".
        var report = new ReportDefinition
        {
            ReportTitle = "Nested",
            Fields = [
                new DatabaseField { Name = "Country", ColumnName = "Country", DataType = "String" },
                new DatabaseField { Name = "City", ColumnName = "City", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [
                new GroupDefinition { Level = 0, FieldName = "Country", SortOrder = GroupSortOrder.Ascending },
                new GroupDefinition { Level = 1, FieldName = "City", SortOrder = GroupSortOrder.Ascending }
            ],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240,
                    Objects = [new TextObject { Name = "H0a", Text = "Header 0a", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 300,
                    Objects = [new TextObject { Name = "H0b", Text = "Header 0b", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.GroupHeader, GroupLevel = 1, HeightTwips = 360,
                    Objects = [new TextObject { Name = "H1", Text = "Header 1", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "City", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1440, 0, 1440, 240) }
                    ] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var groups = doc.Descendants(ns + "TableGroup").ToList();
        Assert.That(groups, Has.Count.EqualTo(2));
        string level0 = groups[0].Element(ns + "Header")!.ToString();
        string level1 = groups[1].Element(ns + "Header")!.ToString();

        Assert.That(level0, Does.Contain("Header 0a").And.Contain("Header 0b"), "level 0 carries both of its sections");
        Assert.That(level1, Does.Contain("Header 1"), "level 1 carries its own section");
        Assert.That(level1, Does.Not.Contain("Header 0b"), "and not level 0's second one");
        AssertEveryRowSpansAllColumns(doc);
    }

    // A row's cells, counting their ColSpan, cover exactly the table's columns.
    private static void AssertEveryRowSpansAllColumns(System.Xml.Linq.XDocument doc)
    {
        var ns = doc.Root!.Name.Namespace;
        foreach (var table in doc.Descendants(ns + "Table"))
        {
            int columnCount = table.Element(ns + "TableColumns")!.Elements(ns + "TableColumn").Count();
            foreach (var cells in table.Descendants(ns + "TableCells")
                         .Where(c => c.Ancestors(ns + "Table").First() == table))
                Assert.That(cells.Elements(ns + "TableCell")
                        .Sum(c => (int?)c.Element(ns + "ColSpan") ?? 1), Is.EqualTo(columnCount));
        }
    }

    [Test]
    public void RdlConverter_SuppressedFreeFormSection_EmitsHiddenOnEachItem()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "HiddenHeader",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 240, Suppress = true,
                    Objects = [new TextObject { Name = "T1", Text = "Title", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        // Found by name rather than by where it sits: a page header lives in the table's
        // Header band now, not in RDL's PageHeader, and this test is about suppression.
        var headerTextbox = doc.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "T1");
        Assert.That(headerTextbox.Element(ns + "Visibility")?.Element(ns + "Hidden")?.Value,
            Is.EqualTo("true"), "items in a statically suppressed free-form section must be hidden");
    }

    [Test]
    public void RdlConverter_CrossTab_EmitsMatrixDataRegion()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pivot",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Product", ColumnName = "Product", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new CrossTabObject
                    {
                        Name = "CrossTab1",
                        Bounds = new(0, 0, 5760, 1440),
                        RowGroupFields = ["Region"],
                        ColumnGroupFields = ["Product"],
                        Cells = [new CrossTabCell("Amount", AggregateFunction.Sum)]
                    }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Matrix Name=\"CrossTab1\">"));
        Assert.That(rdl, Does.Contain("<GroupExpression>=Fields!Product.Value</GroupExpression>"));
        Assert.That(rdl, Does.Contain("<GroupExpression>=Fields!Region.Value</GroupExpression>"));
        // Zero where nothing was summed, as Crystal prints it - see CrossTabTests.
        Assert.That(rdl, Does.Contain("=IIf(Count(Fields!Amount.Value) &gt; 0, Sum(Fields!Amount.Value), 0)"));
        Assert.That(rdl, Does.Contain("<MatrixColumns>"));
    }

    [Test]
    public void RdlConverter_Chart_EmitsChartDataRegion()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pie",
            Fields = [
                new DatabaseField { Name = "CustomerName", ColumnName = "Customer Name", DataType = "String" },
                new DatabaseField { Name = "OrderAmount", ColumnName = "Order Amount", DataType = "Float64" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new ChartObject
                    {
                        Name = "Chart1",
                        Bounds = new(0, 0, 5760, 1440),
                        Title = "Top 5 Customers",
                        Kind = ChartKind.Pie,
                        CategoryFields = ["Customer Name"],
                        SeriesField = "Order Amount",
                        SeriesFunction = AggregateFunction.Sum
                    }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Chart Name=\"Chart1\">"));
        Assert.That(rdl, Does.Contain("<Type>Pie</Type>"));
        Assert.That(rdl, Does.Contain("<Caption>Top 5 Customers</Caption>"));
        Assert.That(rdl, Does.Contain("<GroupExpression>=Fields!Customer_Name.Value</GroupExpression>"));
        Assert.That(rdl, Does.Contain("=Sum(Fields!Order_Amount.Value)"));
        Assert.That(rdl, Does.Contain("<ChartData>"));
    }

    [Test]
    public void RdlConverter_CrossTabMultiAxisMultiCell_EmitsNestedGroupingsAndStaticColumns()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pivot",
            Fields = [
                new DatabaseField { Name = "Country", ColumnName = "Country", DataType = "String" },
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Year", ColumnName = "Year", DataType = "String" },
                new DatabaseField { Name = "Quarter", ColumnName = "Quarter", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new DatabaseField { Name = "Units", ColumnName = "Units", DataType = "Int32" }
            ],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new CrossTabObject
                    {
                        Name = "CrossTab1",
                        Bounds = new(0, 0, 5760, 1440),
                        RowGroupFields = ["Country", "Region"],
                        ColumnGroupFields = ["Year", "Quarter"],
                        Cells = [
                            new CrossTabCell("Amount", AggregateFunction.Sum),
                            new CrossTabCell("Units", AggregateFunction.Count)
                        ]
                    }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var matrix = doc.Descendants(ns + "Matrix").First();

        // 2 dynamic column levels (Year, Quarter) + 1 static level for the 2 cells
        Assert.That(matrix.Element(ns + "ColumnGroupings")!.Elements(ns + "ColumnGrouping").Count(), Is.EqualTo(3));
        // 2 dynamic row levels (Country, Region), no static row level
        Assert.That(matrix.Element(ns + "RowGroupings")!.Elements(ns + "RowGrouping").Count(), Is.EqualTo(2));
        Assert.That(rdl, Does.Contain("<StaticColumns>"));
        Assert.That(rdl, Does.Contain("Sum of Amount"));
        Assert.That(rdl, Does.Contain("Count of Units"));

        // engine rule: MatrixCells/MatrixColumns count must equal the static-column count (2)
        var matrixRow = matrix.Descendants(ns + "MatrixRow").Single();
        Assert.That(matrixRow.Element(ns + "MatrixCells")!.Elements(ns + "MatrixCell").Count(), Is.EqualTo(2));
        Assert.That(matrix.Element(ns + "MatrixColumns")!.Elements(ns + "MatrixColumn").Count(), Is.EqualTo(2));
        Assert.That(rdl, Does.Contain(", Sum(Fields!Amount.Value), 0)"));
        Assert.That(rdl, Does.Contain(", Count(Fields!Units.Value), 0)"));
    }

    [Test]
    public void RdlConverter_LineAndBoxObjects_EmitLineAndRectangle()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Shapes",
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 720,
                    Objects = [
                        new LineObject { Name = "Line1", Bounds = new(0, 700, 5760, 20) },
                        new BoxObject { Name = "Box1", Bounds = new(0, 0, 5760, 720) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Line Name=\"Line1\">"));
        Assert.That(rdl, Does.Contain("<Rectangle Name=\"Box1\">"));
        Assert.That(rdl, Does.Contain("<Default>Solid</Default>"));
    }

    // A parameter is declared as a ReportParameter and never appears in the DataSet, so
    // resolving a placed reference to one as Fields!X.Value names a column that does not
    // exist and the whole report fails to render.
    [Test]
    public void RdlConverter_PlacedParameterField_EmitsParametersReferenceNotField()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Parameter Report",
            Fields = [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new ParameterField { Name = "Start Page", DataType = "Number" }
            ],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Start Page", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("Parameters!Start_Page.Value"),
            "A placed parameter field should resolve to the declared ReportParameter");
        Assert.That(rdl, Does.Not.Contain("Fields!Start_Page.Value"),
            "A parameter is not a DataSet field and must never be emitted as one");
    }

    // A parameter named like one of Crystal's special fields is still the report's own
    // parameter — the declaration is direct evidence, the special-field list is a fallback.
    [Test]
    public void RdlConverter_ParameterNamedLikeASpecialField_PrefersTheParameter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Page Number Parameter",
            Fields = [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new ParameterField { Name = "Page Number", DataType = "Number" }
            ],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Page Number", Bounds = new(0,0,1440,240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("Parameters!Page_Number.Value"));
        Assert.That(rdl, Does.Not.Contain("Fields!Page_Number.Value"));
    }

    // A formula that cannot be translated degrades to a placeholder. When the only place
    // it is used as a number is a *section* formula — a table's suppress or page-break
    // hook — the empty-string placeholder breaks that arithmetic, so those hooks have to
    // count as reference sites when picking the placeholder.
    [Test]
    public void RdlConverter_FormulaUsedNumericallyInASectionFormula_DegradesToZero()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Row Count",
            Fields = [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                // Untranslatable body (a variable declaration) so it must degrade.
                new FormulaField { Name = "rowcount", FormulaText = "Shared rc as number" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    SuppressFormula = "{@rowcount} - 1 > 0",
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Field Name=\"rowcount\">"));
        Assert.That(rdl, Does.Contain("=0"),
            "a formula subtracted inside a section formula must degrade to 0, not an empty string");
    }

    // Crystal's Record Number special field, referenced bare inside a formula rather than
    // placed as a field, is written without the space.
    [Test]
    public void RdlConverter_BareRecordNumberIdentifier_BecomesRowNumber()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Record Number",
            Fields = [
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" },
                new FormulaField { Name = "pos", FormulaText = "recordnumber + 1" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("RowNumber(Nothing)"));
        Assert.That(rdl, Does.Not.Contain("recordnumber"));
    }

    // A template names its own database. A host rendering uploaded templates with pushed
    // data must never open a connection one asks for, so the converter can leave it out.
    [Test]
    public void RdlConverter_OmitConnections_WritesNoHostOrDsn()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Offline",
            DataSources = [
                new DataSource { Name = "Main", Kind = DataSourceKind.Native, ServerName = "db.example.test", DatabaseName = "Sales" },
                new DataSource { Name = "Aux", Kind = DataSourceKind.Odbc, OdbcDsn = "SalesDsn" }
            ],
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string withConnections = new RdlConverter().Convert(report);
        string offline = new RdlConverter { OmitConnections = true }.Convert(report);

        Assert.That(withConnections, Does.Contain("Data Source=db.example.test"),
            "the default keeps the template's connection for authored-RDL use");
        Assert.That(offline, Does.Not.Contain("db.example.test"));
        Assert.That(offline, Does.Not.Contain("SalesDsn"));
        Assert.That(offline, Does.Not.Contain("Data Source="));
        Assert.That(offline, Does.Not.Contain("DSN="));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(offline, "<ConnectString\\s*/>|<ConnectString></ConnectString>").Count,
            Is.EqualTo(2), "the element stays, empty: the engine requires it");
    }

    // A runtime value for a subreport's parameter is written as a literal, replacing the
    // parent-field link the converter would otherwise derive for that name.
    [Test]
    public void RdlConverter_SubreportParameterValueOverride_IsWrittenAsALiteral()
    {
        var sub = new SubreportObject
        {
            Name = "Sub1", SubreportName = "Sub1", Bounds = new(0, 240, 2880, 240),
            Report = new ReportDefinition
            {
                ReportTitle = "Child",
                Fields = [
                    new ParameterField { Name = "CustId", DataType = "String" },
                    new ParameterField { Name = "Limit", DataType = "Number" },
                    new ParameterField { Name = "When", DataType = "Date" }
                ],
                Sections = [new Section { Type = SectionType.Details, HeightTwips = 240 }]
            },
            ParameterValueOverrides = { ["CustId"] = "C\"1", ["Limit"] = 5, ["When"] = new DateTime(2020, 1, 2) }
        };
        var report = new ReportDefinition
        {
            ReportTitle = "Parent",
            // "CustId" is also a parent column, so the converter would link it; the value wins.
            Fields = [new DatabaseField { Name = "CustId", ColumnName = "CustId", TableName = "Customers", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportFooter, HeightTwips = 480, Objects = [sub] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Subreport"));
        Assert.That(rdl, Does.Match(@"<Parameter Name=""CustId"">\s*<Value>=""C""""1""</Value>"));
        Assert.That(rdl, Does.Not.Contain("Fields!CustId.Value</Value>"), "the literal replaces the derived link");
        Assert.That(rdl, Does.Match(@"<Parameter Name=""Limit"">\s*<Value>=5</Value>"));
        Assert.That(rdl, Does.Match(@"<Parameter Name=""When"">\s*<Value>=CDate\(""2020-01-02T00:00:00""\)</Value>"));
    }

    // From the conversion-bug batch (PR #24): a converted report cannot prompt, so every
    // parameter must accept an empty string as well as null.
    [Test]
    public void RdlConverter_EveryParameter_AllowsBlank()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Params",
            Fields = [
                new ParameterField { Name = "Region", DataType = "String" },
                new ParameterField { Name = "Year", DataType = "Float64" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(System.Text.RegularExpressions.Regex.Matches(rdl, "<AllowBlank>true</AllowBlank>").Count, Is.EqualTo(2));
    }

    // From the same batch: a parameter placed as its own report object carries Crystal's
    // "?@Name" field code, and the declared name keeps the "@"; only the "?" is syntax.
    // The fix is on the band path (headers and footers); a Details cell does not consult
    // the parameter map at all, which is a separate, older gap.
    [Test]
    public void RdlConverter_PlacedParameterObject_ResolvesToTheDeclaredParameter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Placed",
            Fields = [new ParameterField { Name = "@Driver", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240,
                Objects = [new FieldObject { FieldName = "?@Driver", Bounds = new(0, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("=Parameters!_Driver.Value"));
        Assert.That(rdl, Does.Not.Contain("?@Driver"));
    }

    // A section's background colour reaches every place the section is written: behind a
    // free-form report header, on each cell of a table row (an object's own colour wins),
    // and on the band a page header becomes.
    [Test]
    public void SectionBackColor_ReachesEveryWayASectionIsWritten()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Colours",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" },
                      new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "Orders", DataType = "String" }],
            Sections = [
                new Section { Type = SectionType.PageHeader, HeightTwips = 300, BackColor = "#FF8000", Objects = [
                    new TextObject { Name = "PH", Text = "Page header", Bounds = new(0, 0, 2000, 240) }] },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 400, BackColor = "#FF0000", Objects = [
                    new TextObject { Name = "Title", Text = "Title", Bounds = new(0, 0, 3000, 300) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, BackColor = "#00FF00", Objects = [
                    new FieldObject { Name = "A", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { Name = "R", FieldName = "Region", Bounds = new(1440, 0, 1440, 240),
                        Format = new ObjectFormat { BackColor = "#123456" } }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.Multiple(() =>
        {
            Assert.That(rdl, Does.Match(@"<Header>[\s\S]*?<Rectangle Name=""Rectangle_\d+"">\s*<Style>\s*<BackgroundColor>#FF8000</BackgroundColor>"),
                "the page header becomes the table's header band, whose Rectangle fills the row");
            Assert.That(rdl, Does.Match(@"<Rectangle Name=""Backdrop_ReportHeader_\d+"">[\s\S]*?<BackgroundColor>#FF0000</BackgroundColor>"), "behind the report header");
            Assert.That(System.Text.RegularExpressions.Regex.Matches(rdl[..rdl.IndexOf("</Details>", StringComparison.Ordinal)], "#00FF00").Count,
                Is.EqualTo(1), "the detail cell without a colour of its own");
            Assert.That(rdl, Does.Contain("#123456"), "an object's own colour wins over its band's");
        });
    }

    // Each row takes its own section's colour or none: a coloured page-header band written
    // first must not colour the uncoloured detail row written after it.
    [Test]
    public void SectionBackColor_DoesNotCarryIntoTheNextRow()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "No leak",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }],
            Sections = [
                new Section { Type = SectionType.PageHeader, HeightTwips = 300, BackColor = "#FF8000", Objects = [
                    new TextObject { Name = "PH", Text = "Page header", Bounds = new(0, 0, 2000, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "A", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);
        int details = rdl.IndexOf("<Details>", StringComparison.Ordinal);

        Assert.That(rdl[..details], Does.Contain("#FF8000"), "the header band has its colour");
        Assert.That(rdl[details..], Does.Not.Contain("BackgroundColor"), "and the detail row after it has none");
    }

    // Where a page footer stays RDL's own page band, the band carries the colour.
    [Test]
    public void SectionBackColor_OnTheRdlPageFooter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Footer colour",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Orders", DataType = "Float64" }],
            Sections = [
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "A", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.PageFooter, HeightTwips = 300, BackColor = "#FF00FF", Objects = [
                    new TextObject { Name = "PF", Text = "Footer", Bounds = new(0, 0, 2000, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Match(@"<PageFooter>[\s\S]*?<Style>\s*<BackgroundColor>#FF00FF</BackgroundColor>\s*</Style>[\s\S]*?</PageFooter>"));
    }

    private static ReportDefinition DetailBreakReport(Section details) => new()
    {
        ReportTitle = "Detail Break",
        Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
        Sections = [details]
    };

    private static Section DetailsWithAmount(bool before = false, bool after = false,
        string? beforeFormula = null, string? afterFormula = null, bool suppress = false) => new()
    {
        Type = SectionType.Details, HeightTwips = 240, Suppress = suppress,
        NewPageBefore = before, NewPageAfter = after,
        NewPageBeforeFormula = beforeFormula, NewPageAfterFormula = afterFormula,
        Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }]
    };

    // A Details grouping collapses rows that share its expression, so a per-record page
    // break needs a key that is different on every row.
    [Test]
    public void RdlConverter_DetailsNewPageAfter_BreaksOnEveryRecord()
    {
        string rdl = new RdlConverter().Convert(DetailBreakReport(DetailsWithAmount(after: true)));

        Assert.That(rdl, Does.Match(@"<Field Name=""CrystalDetailRow"">\s*<Value>=RowNumber\(Nothing\)</Value>\s*</Field>"));
        Assert.That(rdl, Does.Match(
            @"<Grouping Name=""CrystalDetailRecord"">\s*<GroupExpressions>\s*<GroupExpression>=Fields!CrystalDetailRow.Value</GroupExpression>\s*</GroupExpressions>\s*<PageBreakAtEnd>true</PageBreakAtEnd>"));
        Assert.That(rdl, Does.Not.Contain("<PageBreakAtStart>true</PageBreakAtStart>"));
    }

    [Test]
    public void RdlConverter_DetailsNewPageBefore_BreaksAtTheStartOfEveryRecord()
    {
        string rdl = new RdlConverter().Convert(DetailBreakReport(DetailsWithAmount(before: true)));

        Assert.That(rdl, Does.Match(
            @"<Grouping Name=""CrystalDetailRecord"">[\s\S]*?<PageBreakAtStart>true</PageBreakAtStart>[\s\S]*?</Grouping>"));
        Assert.That(rdl, Does.Not.Contain("<PageBreakAtEnd>true</PageBreakAtEnd>"));
    }

    // The engine reads PageBreakCondition on a Details grouping only for a break at its start,
    // so a conditional break after a record can't be expressed; breaking on every record would
    // be worse than not breaking.
    [Test]
    public void RdlConverter_DetailsFormulaDrivenNewPage_IsNotExpressed()
    {
        string rdl = new RdlConverter().Convert(DetailBreakReport(
            DetailsWithAmount(after: true, afterFormula: "{Amount} > 10")));

        Assert.That(rdl, Does.Not.Contain("CrystalDetailRecord"));
        Assert.That(rdl, Does.Not.Contain("CrystalDetailRow"));
    }

    [Test]
    public void RdlConverter_SuppressedDetailsNewPage_IsNotApplied()
    {
        string rdl = new RdlConverter().Convert(DetailBreakReport(
            DetailsWithAmount(after: true, suppress: true)));

        Assert.That(rdl, Does.Not.Contain("CrystalDetailRecord"));
    }

    [Test]
    public void RdlConverter_DetailsWithoutNewPage_HasNoDetailGrouping()
    {
        string rdl = new RdlConverter().Convert(DetailBreakReport(DetailsWithAmount()));

        Assert.That(rdl, Does.Not.Contain("CrystalDetailRecord"));
        Assert.That(rdl, Does.Not.Contain("CrystalDetailRow"));
    }

    // Crystal types plenty of numeric-looking columns as text. Retyping the column was
    // tried and regressed both corpora, so the coercion goes at the reference site.
    [Test]
    public void RdlConverter_StringColumnsInArithmetic_AreCoercedAtTheReference()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Text Amounts",
            Fields = [
                new DatabaseField { Name = "Balance", ColumnName = "Balance", DataType = "String" },
                new DatabaseField { Name = "Payment", ColumnName = "Payment", DataType = "String" },
                new FormulaField { Name = "Owing", FormulaText = "{T.Balance} - {T.Payment}" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Balance", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("Val(Fields!Balance.Value)"));
        Assert.That(rdl, Does.Contain("Val(Fields!Payment.Value)"));
    }

    // The other half of the rule: Crystal's "+" is concatenation whenever a string is in
    // reach, so an expression holding a literal or an "&" must be left alone entirely.
    [Test]
    public void RdlConverter_StringColumnsBeingConcatenated_AreNotCoerced()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Text Join",
            Fields = [
                new DatabaseField { Name = "First", ColumnName = "First", DataType = "String" },
                new DatabaseField { Name = "Last", ColumnName = "Last", DataType = "String" },
                // Contains a literal, so the arithmetic rule must not fire even though the
                // separator "-" is an arithmetic operator character.
                new FormulaField { Name = "FullName", FormulaText = "{T.First} + \"-\" + {T.Last}" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "First", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("Val(Fields!First.Value)"),
            "a concatenation must not have its operands coerced to numbers");
        Assert.That(rdl, Does.Not.Contain("Val(Fields!Last.Value)"));
    }

    // Crystal negates a flag column directly. The column arrives untyped, which the
    // engine treats as text and rejects outright ("NOT requires boolean expression"),
    // failing the whole expression rather than the one reference - so the operand is
    // coerced where it is used, the same way an arithmetic one is.
    [Test]
    public void RdlConverter_NegatedStringColumn_IsCoercedToBoolean()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Flags",
            Fields = [
                new DatabaseField { Name = "Active", ColumnName = "Active", DataType = "String" },
                new FormulaField { Name = "Hidden", FormulaText = "Not {T.Active}" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Active", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("CBool(Fields!Active.Value)"));
    }

    // Numeric use carries down a reference at a time. Here only "Net" is subtracted
    // from; "Gross" is merely added, and "+" alone is no evidence because Crystal also
    // concatenates with it. Gross is reached through Net, whose body holds no string
    // literal, so it is numeric too - and a formula that degrades to nothing has to
    // degrade to 0 rather than "", or the subtraction it feeds is rejected.
    [Test]
    public void RdlConverter_NumericUse_PropagatesThroughReferencingFormula()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Ledger",
            Fields = [
                // Gross refers to a column no DataSet declares, so it degrades.
                new FormulaField { Name = "Gross", FormulaText = "{Absent.Amount}" },
                new FormulaField { Name = "Net", FormulaText = "{@Gross} + {@Gross}" },
                new FormulaField { Name = "Owing", FormulaText = "{@Net} - 1" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new TextObject { Text = "x", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Field Name=\"Gross\">"));
        Assert.That(rdl, Does.Not.Contain("<Value>=\"\"</Value>"),
            "a degraded field feeding arithmetic must become 0, not an empty string");
    }

    // "Group #N Name" is a Crystal built-in, not a column. A section formula is
    // transpiled straight from Crystal text, so without substitution it emits a
    // reference no DataSet declares and the engine rejects the whole expression.
    [Test]
    public void RdlConverter_GroupNameInSuppressFormula_ResolvesToTheGroupField()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Grouped",
            Fields = [
                new DatabaseField { Name = "Region", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { FieldName = "Region" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    SuppressFormula = "{Group #1 Name} = \"\"",
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("Group__1_Name"),
            "the built-in group-name field must not survive as a DataSet field reference");
        Assert.That(rdl, Does.Contain("Fields!Region.Value"));
    }

    // A converted report has no way to prompt, so every parameter has to be renderable
    // without a value - which the engine only allows when the parameter says so.
    [Test]
    public void RdlConverter_Parameters_AreDeclaredNullable()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Prompted",
            Fields = [new ParameterField { Name = "Year", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new TextObject { Text = "x", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Nullable>true</Nullable>"));
    }

    // RDL has no duplicate <Field Name>. The engine reports one as "Field X has
    // duplicates" and then keeps only the first, so emitting two is silent data loss.
    // Columns from different tables that differ only in case collide here, and the
    // engine matches names case-insensitively too, so it cannot tell them apart either.
    [Test]
    public void RdlConverter_ColumnsDifferingOnlyInCase_EmitOneField()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Addresses",
            Fields = [
                new DatabaseField { Name = "Header.prov", TableName = "Header", ColumnName = "prov", DataType = "String" },
                new DatabaseField { Name = "Detail.Prov", TableName = "Detail", ColumnName = "Prov", DataType = "String" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "prov", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        int fields = System.Text.RegularExpressions.Regex
            .Matches(rdl, "<Field Name=\"[Pp]rov\">").Count;
        Assert.That(fields, Is.EqualTo(1),
            "two columns whose names differ only in case must not both be declared");
    }

    // Without a report Width the engine warns and assumes the whole page, so body content
    // is allowed to run underneath the margins. Crystal has no equivalent field - its
    // sections are the page less its margins by construction.
    [Test]
    public void RdlConverter_ReportWidth_IsPageWidthLessMargins()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Sized",
            Page = new PageLayout
            {
                WidthTwips = 12240,        // 8.5in
                HeightTwips = 15840,       // 11in
                LeftMarginTwips = 720,     // 0.5in
                RightMarginTwips = 720,
                TopMarginTwips = 720,
                BottomMarginTwips = 720
            },
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new TextObject { Text = "x", Bounds = new(0,0,1440,240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<Width>540pt</Width>"),
            "8.5in page less two 0.5in margins is a 7.5in body");
    }

    // A report header holding a field reference needs a data region to resolve it, but the
    // details table is the wrong one: a header band is only as wide as its table and starts
    // where that table starts, so a page-spanning title comes out pushed right and running
    // off the end. It gets a full-width table of its own instead.
    [Test]
    public void RdlConverter_FieldBoundReportHeader_GetsAFullWidthTableOfItsOwn()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Wide",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Page = new PageLayout
            {
                WidthTwips = 12240, HeightTwips = 15840,
                LeftMarginTwips = 240, RightMarginTwips = 240,
                TopMarginTwips = 240, BottomMarginTwips = 240
            },
            Sections =
            [
                // A title that spans the page, in a report whose detail column is narrow
                // and indented — exactly the shape that used to clip it.
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(40, 60, 11340, 1440) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(3000, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var tables = doc.Descendants(ns + "Table").ToList();
        Assert.That(tables.Count, Is.EqualTo(2), "the report header needs a host of its own");

        var host = tables.First(tbl => tbl.Descendants(ns + "Header").Any());
        Assert.That(host.Element(ns + "Left"), Is.Null,
            "it starts at the body's edge, not at the details table's first column");

        double hostWidth = Inches(host.Element(ns + "Width")?.Value);
        double item = host.Descendants(ns + "Header").First()
            .Descendants(ns + "Textbox")
            .Max(tb => Inches(tb.Element(ns + "Left")?.Value) + Inches(tb.Element(ns + "Width")?.Value));
        Assert.That(item, Is.LessThanOrEqualTo(hostWidth + 0.01),
            "nothing in the header may run off the end of the table holding it");

        static double Inches(string? v) =>
            v is not null && v.EndsWith("in")
                ? double.Parse(v[..^2], System.Globalization.CultureInfo.InvariantCulture)
                : 0;
    }

    // The host table's Details band exists only because RDL demands one row. It is emitted
    // once per row of the DataSet, so on a report with a couple of thousand rows an unhidden
    // placeholder lays down a couple of thousand blank rows.
    [Test]
    public void RdlConverter_HeaderHostTable_HidesItsPlaceholderRow()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Placeholder",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 11340, 1440) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Amount", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var host = doc.Descendants(ns + "Table").First(tbl => tbl.Descendants(ns + "Header").Any());
        var placeholder = host.Element(ns + "Details")!.Element(ns + "TableRows")!
            .Elements(ns + "TableRow").Single();
        Assert.That(placeholder.Element(ns + "Visibility")?.Element(ns + "Hidden")?.Value,
            Is.EqualTo("true"));
    }

    // A label placed in a group-header cell is not a leftover. It used to stay in the
    // leftovers queue as well, so the labels came out twice: once in their own columns and
    // once again on an extra row underneath, shifted a column to the left.
    [Test]
    public void RdlConverter_GroupHeaderLabels_AreNotAlsoEmittedAsALeftoverRow()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Grouped",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [
                        new FieldObject { FieldName = "Customer", Bounds = new(0, 0, 1440, 240) },
                        new TextObject { Name = "L1", Text = "Amount", Bounds = new(1500, 0, 1440, 240) }
                    ] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Customer", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1500, 0, 1440, 240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        // Not counting the copy of the group header that prints when there are no rows.
        int labelCells = doc.Descendants(ns + "Textbox")
            .Where(tb => tb.Attribute("Name")?.Value.EndsWith("_NoRows") != true)
            .Count(tb => tb.Element(ns + "Value")?.Value == "Amount");
        Assert.That(labelCells, Is.EqualTo(1),
            "the group header's label belongs in exactly one cell");
    }

    // With no rows Crystal prints each group section and the Details once. A grouped table's
    // footer opens with a copy of those rows, under names of their own, shown only when the
    // dataset is empty, and a row that can shrink so that the engine does not count the
    // hidden ones when it fits the footer on a page.
    [Test]
    public void RdlConverter_GroupedTable_CarriesThePassCrystalMakesWithNoRows()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Grouped",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new TextObject { Name = "Head", Text = "Customer {Customer}", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { Name = "Amt", FieldName = "Amount", Bounds = new(1500, 0, 1440, 240) }] },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new FieldObject { Name = "Total", FieldName = "Amount", SummaryFunction = AggregateFunction.Sum,
                        Bounds = new(1500, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var table = doc.Descendants(ns + "Table").Single();
        var footerRows = table.Element(ns + "Footer")!.Element(ns + "TableRows")!.Elements(ns + "TableRow").ToList();
        var names = doc.Descendants().Where(e => e.Name.LocalName is "Textbox" or "Rectangle")
            .Select(e => e.Attribute("Name")!.Value).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(names, Is.Unique);
            // Group header, Details, group footer, then the row that can shrink.
            Assert.That(footerRows, Has.Count.EqualTo(4));
            Assert.That(footerRows.Take(3).Select(r => r.Element(ns + "Visibility")!.Element(ns + "Hidden")!.Value),
                Has.All.EqualTo("=CountRows(\"DataSet1\") > 0"));
            Assert.That(footerRows[3].Descendants(ns + "CanShrink").Single().Value, Is.EqualTo("true"));
            // The text around a field that has no value still prints; a summary over no
            // rows prints nothing.
            Assert.That(footerRows[0].Descendants(ns + "Value").Select(v => v.Value),
                Has.Some.Contains("IIf(IsNothing(Nothing), \"\""));
            Assert.That(footerRows[2].Descendants(ns + "Value").Select(v => v.Value), Has.None.Contains("Sum("));
        });
    }

    // An ungrouped table makes the same pass over its Details, and so does a subreport's
    // table: the Crystal runtime prints a subreport given no rows with its group sections
    // and Details once.
    [TestCase(false)]
    [TestCase(true)]
    public void RdlConverter_UngroupedTable_CarriesThePassCrystalMakesWithNoRows(bool asSubreport)
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Ungrouped",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new TextObject { Name = "Label", Text = "Line", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { Name = "Amt", FieldName = "Amount", Bounds = new(1500, 0, 1440, 240) }] }
            ]
        };

        var converter = asSubreport ? new RdlConverter { Root = new ReportDefinition(), RowSentinel = true } : new RdlConverter();
        var doc = System.Xml.Linq.XDocument.Parse(converter.Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var footerRows = doc.Descendants(ns + "Table").Single().Element(ns + "Footer")!
            .Element(ns + "TableRows")!.Elements(ns + "TableRow").ToList();

        Assert.Multiple(() =>
        {
            // Details, then the row that can shrink.
            Assert.That(footerRows, Has.Count.EqualTo(2));
            Assert.That(footerRows[0].Element(ns + "Visibility")!.Element(ns + "Hidden")!.Value,
                Is.EqualTo("=CountRows(\"DataSet1\") > 0"));
            Assert.That(footerRows[0].Descendants(ns + "Value").Select(v => v.Value), Has.Some.EqualTo("Line"));
        });
    }

    // In that pass a formula that reads the data prints nothing, not what it makes of empty
    // fields, and a colour formula on the record number colours nothing, as the Crystal
    // runtime prints a pick list with no rows. A formula of parameters still prints.
    [Test]
    public void RdlConverter_PassWithNoRows_PrintsNoFormulaOfTheData()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Picking",
            Fields = [
                new DatabaseField { Name = "Kind", ColumnName = "Kind", DataType = "Int32" },
                new ParameterField { Name = "Lang", DataType = "String" },
                new FormulaField { Name = "KindName", FormulaText = "if {Kind} = 17 then 'Order' else 'Unknown'" },
                new FormulaField { Name = "Caption", FormulaText = "if {?Lang} = 'DK' then 'Antal' else 'Count'" }
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    BackColorFormula = "if RecordNumber mod 2 = 0 then crSilver else crNoColor",
                    Objects = [
                        new FieldObject { Name = "KindField", FieldName = "@KindName", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { Name = "CaptionField", FieldName = "@Caption", Bounds = new(1500, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var pass = doc.Descendants(ns + "Table").Single().Element(ns + "Footer")!.Descendants(ns + "TableRow").First();
        var values = pass.Descendants(ns + "Value").Select(v => v.Value).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(values, Has.None.Contains("Fields!KindName"));
            Assert.That(values, Has.Some.Contains("Fields!Caption.Value"));
            Assert.That(doc.Descendants(ns + "Details").Single().Descendants(ns + "BackgroundColor").Select(c => c.Value),
                Has.Some.Contains("RowNumber"), "the band with rows keeps its colour formula");
            Assert.That(pass.Descendants(ns + "BackgroundColor").Select(c => c.Value), Has.None.Contains("RowNumber"));
        });
    }

    // A group section written free-form, outside any table group, prints once. With no rows
    // Crystal prints the group sections once too, so it is not hidden then.
    [Test]
    public void RdlConverter_GroupSectionWrittenFreeForm_IsNotHiddenWithNoRows()
    {
        // A group footer whose group is not in the report's list of groups, so there is no
        // table group to put it in.
        var report = new ReportDefinition
        {
            ReportTitle = "FreeForm",
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240 },
                new Section { Type = SectionType.GroupFooter, HeightTwips = 240, GroupLevel = 0,
                    Objects = [new TextObject { Name = "Closing", Text = "Yours truly", Bounds = new(0, 0, 2880, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var closing = doc.Root.Element(ns + "Body")!.Element(ns + "ReportItems")!.Elements(ns + "Textbox")
            .Single(t => t.Element(ns + "Value")?.Value == "Yours truly");

        Assert.That(closing.Element(ns + "Visibility")?.Element(ns + "Hidden")?.Value ?? "", Does.Not.Contain("CountRows"));
    }

    // A group header holding only a picture prints no group name: the caption is an object
    // of the section's own, and there is none.
    [Test]
    public void RdlConverter_GroupHeaderWithOnlyAnImage_PrintsNoGroupName()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Pictured",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 1440, GroupLevel = 0,
                    Objects = [new ImageObject { Name = "Logo", ImageData = [1, 2, 3], MimeType = "image/png",
                        Bounds = new(0, 0, 1440, 1440) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { Name = "Amt", FieldName = "Amount", Bounds = new(1500, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var header = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Header")!;

        Assert.That(header.Descendants(ns + "Value").Select(v => v.Value), Has.None.Contains("Customer"));
    }

    // A group header of no height with nothing in it prints nothing and takes no space, so
    // the first record prints where the band above ends.
    [Test]
    public void RdlConverter_EmptyGroupHeaderOfNoHeight_TakesNoSpace()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Empty",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer" }],
            Sections =
            [
                new Section { Type = SectionType.GroupHeader, HeightTwips = 0, GroupLevel = 0 },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { Name = "Amt", FieldName = "Amount", Bounds = new(1500, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;
        var row = doc.Descendants(ns + "TableGroup").Single().Element(ns + "Header")!.Descendants(ns + "TableRow").Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Element(ns + "Height")!.Value, Is.EqualTo("0pt"));
            Assert.That(row.Descendants(ns + "Value").Select(v => v.Value), Has.None.Contains("Customer"));
        });
    }

    // A field's format has to survive into the detail cell, which is the one place it
    // matters and the one place the style was being rebuilt field by field.
    [Test]
    public void RdlConverter_FieldFormat_ReachesTheDetailCell()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Dated",
            Fields = [new DatabaseField { Name = "When", ColumnName = "When", DataType = "DateTime" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "When", Bounds = new(0, 0, 1440, 240),
                        Format = new ObjectFormat { FormatString = "MM'/'dd'/'yyyy" } }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var cell = doc.Descendants(ns + "Details").First()
            .Descendants(ns + "Textbox")
            .First(tb => tb.Element(ns + "Value")?.Value == "=Fields!When.Value");
        Assert.That(cell.Element(ns + "Style")?.Element(ns + "Format")?.Value,
            Is.EqualTo("MM'/'dd'/'yyyy"));
    }

    // Crystal prints the Report Header above the Page Header on page one. RDL's PageHeader
    // is pinned to the very top of every page, so a page header left there comes out above
    // the report header instead of below it, and the whole header block sits wrong.
    [Test]
    public void RdlConverter_PageHeader_RendersBelowTheReportHeaderNotAboveIt()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Ordered",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 1440,
                    Objects = [new TextObject { Name = "Title", Text = "The Title",
                        Bounds = new(0, 0, 5000, 480) }] },
                new Section { Type = SectionType.PageHeader, HeightTwips = 480,
                    Objects = [new TextObject { Name = "Label", Text = "ID Column",
                        Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "PageHeader").Any(), Is.False,
            "nothing may be left in RDL's PageHeader, which would print above the title");

        var label = doc.Descendants(ns + "Textbox")
            .FirstOrDefault(tb => tb.Attribute("Name")?.Value == "Label");
        Assert.That(label, Is.Not.Null, "the page header's content must survive somewhere");

        var header = label!.Ancestors(ns + "Header").FirstOrDefault();
        Assert.That(header, Is.Not.Null, "it belongs in the table's own Header band");
        Assert.That(header!.Element(ns + "RepeatOnNewPage")?.Value, Is.EqualTo("true"),
            "which is what gives back the every-page half of Crystal's behaviour");
    }

    // A table starts at its first detail column, not at the page's left edge, and objects
    // in its header band carry absolute page positions. Left untranslated they come out
    // that offset too far right, and anything reaching the page's edge runs off the end of
    // the table.
    [Test]
    public void RdlConverter_TableHeaderContent_IsPositionedAgainstTheTableNotThePage()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Indented",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 480,
                    Objects = [new TextObject { Name = "Label", Text = "ID Column",
                        Bounds = new(2160, 0, 1440, 240) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    // The table's only column starts an inch and a half in.
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(2160, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;

        var table = doc.Descendants(ns + "Table")
            .First(tbl => tbl.Descendants(ns + "Textbox")
                .Any(tb => tb.Attribute("Name")?.Value == "Label"));
        Assert.That(table.Element(ns + "Left")?.Value, Is.EqualTo("108pt"),
            "the table itself is where its first column is");

        var label = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Label");
        Assert.That(label.Element(ns + "Left")?.Value, Is.EqualTo("0pt"),
            "so a label drawn at 1.5in on the page sits at the table's own left edge");
    }

    // The other half of that: a band object further LEFT than the table's first column.
    // Its offset from the table is negative, RDL cannot say so, and clamping it to zero
    // printed it on top of whatever the first column's heading is - a print date at the
    // page margin landed on the first column label. The table starts at the leftmost thing
    // in it instead, and a spacer column holds the data columns where the report drew them.
    [Test]
    public void RdlConverter_BandContentLeftOfTheFirstColumn_MovesTheTableInsteadOfClamping()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Indented",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 480,
                    Objects =
                    [
                        // At the page's left edge, two inches left of the only column.
                        new TextObject { Name = "PrintDate", Text = "2026-08-28",
                            Bounds = new(0, 0, 1440, 240) },
                        new TextObject { Name = "Label", Text = "ID",
                            Bounds = new(2880, 0, 1440, 240) }
                    ] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(2880, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;

        var table = doc.Descendants(ns + "Table")
            .First(tbl => tbl.Descendants(ns + "Textbox")
                .Any(tb => tb.Attribute("Name")?.Value == "PrintDate"));

        Assert.That(table.Element(ns + "Left"), Is.Null,
            "the table starts at the leftmost thing in it, which here is the page's own edge");

        var widths = table.Element(ns + "TableColumns")!.Elements(ns + "TableColumn")
            .Select(c => c.Element(ns + "Width")!.Value).ToList();
        Assert.That(widths, Is.EqualTo(new[] { "144pt", "72pt" }),
            "a spacer column carries the gap so the data column stays where it was drawn");

        var printDate = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "PrintDate");
        Assert.That(printDate.Element(ns + "Left")?.Value, Is.EqualTo("0pt"),
            "the date keeps the page's left edge instead of being clamped onto the column");

        var label = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Label");
        Assert.That(label.Element(ns + "Left")?.Value, Is.EqualTo("144pt"),
            "and the column heading stays over its column");
    }

    // A page footer's FieldObjects are routed into the details table's footer band, because
    // a FieldObject placed in RDL's own <PageFooter> has no data scope and fails to resolve.
    // A Crystal special field needs no such scope - it becomes a Globals expression - and
    // routing one anyway costs the thing a page footer is for: the table's footer band
    // renders where the table ends, which on a short report is under the last detail row,
    // not at the foot of the page.
    [Test]
    public void RdlConverter_PageFooterOfSpecialFieldsOnly_StaysInThePagesOwnFooter()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Listing",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.PageFooter, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Page Number", Bounds = new(0, 0, 1440, 240) },
                        new TextObject { Name = "Printed", Text = "Printed {Print Date}",
                            Bounds = new(2880, 0, 2160, 240) }
                    ] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var pageFooter = doc.Descendants(ns + "PageFooter").SingleOrDefault();
        Assert.That(pageFooter, Is.Not.Null, "a footer of special fields belongs to the page");
        Assert.That(pageFooter!.Descendants(ns + "Value").Select(v => v.Value),
            Does.Contain("=Globals!PageNumber"));

        var table = doc.Descendants(ns + "Table").FirstOrDefault();
        Assert.That(table, Is.Not.Null);
        Assert.That(table!.Descendants(ns + "Value").Select(v => v.Value),
            Has.None.Contains("Globals!PageNumber"),
            "and is not also sitting in the table, which would print it twice");
    }

    // The other side of that narrowing, and the one that matters: a page footer referring to
    // real data still has to be routed, because RDL gives it no Fields! scope of its own.
    [Test]
    public void RdlConverter_PageFooterBoundToData_IsStillRoutedIntoTheTable()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Listing",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.PageFooter, HeightTwips = 240,
                    Objects =
                    [
                        new FieldObject { FieldName = "Page Number", Bounds = new(0, 0, 1440, 240) },
                        // One data-bound object is enough: the section is routed whole.
                        new FieldObject { FieldName = "ID", Bounds = new(2880, 0, 1440, 240) }
                    ] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "PageFooter").Any(), Is.False,
            "nothing can go in the page's own footer once one item in it needs the data");

        var table = doc.Descendants(ns + "Table").First();
        Assert.That(table.Descendants(ns + "Value").Select(v => v.Value),
            Does.Contain("=Globals!PageNumber"),
            "the whole section travels together into the table's footer band");
    }

    // Crystal prints a page footer at the foot of every page, under the report footer. Routed
    // into the table's footer band, its rows come last, and the table's Custom names how many
    // there are, for an engine that prints them at the foot of each page. One that does not
    // know the Custom ignores it and prints the band as before.
    [Test]
    public void RdlConverter_RoutedPageFooter_IsTheFootersLastRows_AndMarkedForTheFootOfThePage()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Listing",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.ReportFooter, HeightTwips = 300,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) },
                        new TextObject { Name = "Total", Text = "TOTAL", Bounds = new(2880, 0, 1440, 240) }] },
                new Section { Type = SectionType.ReportFooter, HeightTwips = 360,
                    Objects = [new TextObject { Name = "End", Text = "END OF REPORT", Bounds = new(0, 0, 2880, 240) }] },
                new Section { Type = SectionType.PageFooter, HeightTwips = 480,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) },
                        new TextObject { Name = "Copy", Text = "COPY", Bounds = new(2880, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        var table = doc.Descendants(ns + "Table").First();
        var rows = table.Element(ns + "Footer")!.Element(ns + "TableRows")!.Elements(ns + "TableRow").ToList();
        string[] labels = ["TOTAL", "END OF REPORT", "COPY"];
        Assert.That(rows.Select(r => labels.FirstOrDefault(l => r.ToString().Contains(l))).OfType<string>(),
            Is.EqualTo(labels), "the report footer's rows, its text included, before the page footer's");
        Assert.That(rows.Last().ToString(), Does.Contain("COPY"));
        Assert.That(table.Element(ns + "Custom")?.Element(ns + "FooterRowsAtBottomOfPage")?.Value, Is.EqualTo("1"));
    }

    [Test]
    public void RdlConverter_FooterBandWithoutAPageFooter_HasNoCustom()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Listing",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.ReportFooter, HeightTwips = 300,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var ns = doc.Root!.Name.Namespace;

        Assert.That(doc.Descendants(ns + "Table").First().Element(ns + "Custom"), Is.Null);
    }

    // A band the report never put anything in is common. Routing one would wrap an empty
    // ReportItems in a Rectangle, which the engine treats as fatal and loses the whole
    // report over — a blank page instead of a report.
    [Test]
    public void RdlConverter_EmptyPageHeader_IsNotRoutedIntoTheTable()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "EmptyBand",
            Fields = [new DatabaseField { Name = "ID", ColumnName = "ID", DataType = "Int32" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 480 },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "ID", Bounds = new(0, 0, 1440, 240) }] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        foreach (var items in doc.Descendants(ns + "ReportItems"))
            Assert.That(items.Elements().Any(), Is.True,
                "an empty ReportItems is fatal to the engine, so none may be written");
    }

    // Crystal's Default alignment puts a number on the right and text on the left, as RDL's
    // General does, so it stays implicit. A number set Left is written Left: boyum__BOM's
    // line numbers sit at the left of their column, and General put them on the right,
    // against the item code beside them.
    [Test]
    public void RdlConverter_ANumberSetLeft_IsWrittenLeft()
    {
        var path = Path.Combine(CorpusDir, "boyum__BOM.rpt");
        if (!File.Exists(path))
            Assert.Ignore("boyum__BOM.rpt not found — run scripts/download-test-rpts.sh");
        var report = RptParser.Parse(path).Report!;
        var line = report.Sections.SelectMany(s => s.Objects).OfType<FieldObject>().Single(f => f.Name == "Line1");
        var quantity = report.Sections.SelectMany(s => s.Objects).OfType<FieldObject>().Single(f => f.Name == "LineQty1");

        Assert.That(line.Format.HAlign, Is.EqualTo(HorizontalAlignment.Left));
        Assert.That(quantity.Format.HAlign, Is.Not.EqualTo(HorizontalAlignment.Left));

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";
        // Not counting the copy of the Details that prints when there are no rows.
        var textbox = doc.Descendants(ns + "Textbox").Single(t => (string?)t.Element(ns + "Value") == "=Fields!Line__.Value"
            && !t.Attribute("Name")!.Value.EndsWith("_NoRows"));
        Assert.That((string?)textbox.Element(ns + "Style")?.Element(ns + "TextAlign"), Is.EqualTo("Left"));
    }

    private static ReportDefinition Bom()
    {
        var path = Path.Combine(CorpusDir, "boyum__BOM.rpt");
        if (!File.Exists(path))
            Assert.Ignore("boyum__BOM.rpt not found — run scripts/download-test-rpts.sh");
        return RptParser.Parse(path).Report!;
    }

    private static readonly System.Xml.Linq.XNamespace RdlNs2005 =
        "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static string? FieldValue(System.Xml.Linq.XDocument doc, string name) =>
        (string?)doc.Descendants(RdlNs2005 + "Field").SingleOrDefault(f => (string?)f.Attribute("Name") == name)
            ?.Element(RdlNs2005 + "Value");

    // Crystal's ToText of a number prints two decimals and grouped thousands, where VB's
    // CStr prints "50"; of a string column it is the string, left alone.
    [Test]
    public void ToTextOfANumberColumn_PrintsTwoDecimals()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(Bom()));

        // Line_UnitPrice: ToText({Lines.Price})+' '+{Lines.Currency}, Price a currency column.
        Assert.That(FieldValue(doc, "Line_UnitPrice"), Does.Contain("CStr(Fields!Price.Value, 2, \",\")"));
        // TotalComponents sums a currency column inside its ToText.
        Assert.That(FieldValue(doc, "TotalComponents"), Does.Contain("Sum(Fields!TotalLC.Value), 2, \",\")"));
    }

    // Given the decimals, Crystal's ToText still groups the thousands; of a number formula or
    // parameter it prints two decimals as of a number column; of a Date parameter, the date
    // alone. A currency formula's ToText prints a "$", and is left alone.
    [Test]
    public void ToTextOfAFormulaOrParameter_PrintsAsCrystalPrintsIt()
    {
        var report = new ReportDefinition
        {
            Fields = [
                new DatabaseField { Name = "Step", ColumnName = "Step", TableName = "T", DataType = "Float64" },
                new ParameterField { Name = "Period", DataType = "Float64" },
                new ParameterField { Name = "Bill Date", DataType = "DateTime", DateOnly = true },
                new ParameterField { Name = "Read At", DataType = "DateTime" },
                new FormulaField { Name = "Charge", FormulaText = "{T.Step} * 2", ResultType = "Float64" },
                new FormulaField { Name = "Fee", FormulaText = "CCur({T.Step})", ResultType = "Currency" },
                new FormulaField { Name = "Line", ResultType = "String", FormulaText =
                    "ToText({T.Step}, 2) + ToText({@Charge}) + CStr({?Period}) + ToText({@Fee}) + ToText({?Bill Date}) + ToText({?Read At})" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "LineField", FieldName = "@Line", Bounds = new(0, 0, 4320, 240) }] }]
        };

        string? line = FieldValue(System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report)), "Line");

        Assert.Multiple(() =>
        {
            Assert.That(line, Does.Contain("CStr(Fields!Step.Value, 2, \",\")"));
            Assert.That(line, Does.Contain("CStr(Fields!Charge.Value, 2, \",\")"));
            Assert.That(line, Does.Contain("CStr(Parameters!Period.Value, 2, \",\")"));
            Assert.That(line, Does.Contain("CStr(Fields!Fee.Value)"));
            Assert.That(line, Does.Contain("CStr(Parameters!Bill_Date.Value, \"d\")"));
            Assert.That(line, Does.Contain("CStr(Parameters!Read_At.Value)"));
        });
    }

    // A special time prints in its object's time record, where it printed the machine's long
    // time: boyum__BOM's Data Time is "H:mm".
    [Test]
    public void ASpecialTime_PrintsInItsTimeRecord()
    {
        var report = Bom();
        var dataTime = report.Sections.SelectMany(s => s.Objects).OfType<FieldObject>().Single(f => f.Name == "DataTime1");
        Assert.That(dataTime.Format.FormatString, Is.EqualTo("H':'mm"));

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));
        var textbox = doc.Descendants(RdlNs2005 + "Textbox").First(t => (string?)t.Attribute("Name") == "DataTime1");
        Assert.Multiple(() =>
        {
            Assert.That((string?)textbox.Element(RdlNs2005 + "Value"), Is.EqualTo("=Globals!ExecutionTime"));
            Assert.That((string?)textbox.Element(RdlNs2005 + "Style")?.Element(RdlNs2005 + "Format"), Is.EqualTo("H':'mm"));
        });
    }

    // "@Qty" and "@UoM" are four characters; references were only taken from five up, so
    // the objects printed their own names, "[Qty1]" and "[UoM1]".
    [Test]
    public void AShortFormulaReference_IsRead()
    {
        var fields = Bom().Sections.SelectMany(s => s.Objects).OfType<FieldObject>().ToList();

        Assert.That(fields.Single(f => f.Name == "Qty1").FieldName, Is.EqualTo("@Qty"));
        Assert.That(fields.Single(f => f.Name == "UoM1").FieldName, Is.EqualTo("@UoM"));
    }

    // Crystal's Sum({T.Amount}, {T.Account}) is the total of the record's account group. The
    // group argument was dropped, and the formula's calculated field summed the whole report;
    // it now names the grouping. A field the report does not group on, and a date interval,
    // have no grouping to name and are dropped as before.
    [Test]
    public void ASummaryOverAGroup_IsScopedToThatGrouping()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Group Summaries",
            Fields =
            [
                new DatabaseField { Name = "T.Region", TableName = "T", ColumnName = "Region", DataType = "String" },
                new DatabaseField { Name = "T.Account", TableName = "T", ColumnName = "Account", DataType = "String" },
                new DatabaseField { Name = "T.Posted", TableName = "T", ColumnName = "Posted", DataType = "DateTime" },
                new DatabaseField { Name = "T.Amount", TableName = "T", ColumnName = "Amount", DataType = "Float64" },
                new FormulaField { Name = "AccountTotal", FormulaText = "Sum ({T.Amount}, {T.Account}) + 1" },
                new FormulaField { Name = "RegionCount", FormulaText = "Count ({T.Amount}, {T.Region})" },
                new FormulaField { Name = "RegionMean", FormulaText = "Average ({T.Amount}, {T.Region})" },
                new FormulaField { Name = "PostedTotal", FormulaText = "Sum ({T.Amount}, {T.Posted})" },
                new FormulaField { Name = "MonthTotal", FormulaText = "Sum ({T.Amount}, {T.Posted}, \"monthly\")" },
            ],
            Groups =
            [
                new GroupDefinition { Level = 0, FieldName = "Region", TableName = "T", Condition = GroupCondition.EachValue },
                new GroupDefinition { Level = 1, FieldName = "Account", TableName = "T", Condition = GroupCondition.EachValue },
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 300, Objects =
                [
                    new FieldObject { Name = "Amount1", FieldName = "T.Amount", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { Name = "AccountTotal1", FieldName = "@AccountTotal", Bounds = new(1440, 0, 1440, 240) },
                ] },
            ]
        };

        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(report));

        Assert.That(FieldValue(doc, "AccountTotal"), Is.EqualTo("=(Sum(Fields!Amount.Value, \"Group2\") + 1)"));
        Assert.That(FieldValue(doc, "RegionCount"), Is.EqualTo("=Count(Fields!Amount.Value, \"Group1\")"));
        Assert.That(FieldValue(doc, "RegionMean"), Is.EqualTo("=Avg(Fields!Amount.Value, \"Group1\")"));
        Assert.That(FieldValue(doc, "PostedTotal"), Is.EqualTo("=Sum(Fields!Amount.Value)"));
        Assert.That(FieldValue(doc, "MonthTotal"), Is.EqualTo("=Sum(Fields!Amount.Value)"));
    }

    // BOM's formula ParentPrice, "ToText({Header.ParentPrice}) + ' ' + {Header.ParentCurrency}",
    // shares its name with the column it formats, and the column took the name: the report
    // printed "240", not "240.00 $". The column now goes by another name.
    [Test]
    public void AFormulaNamedLikeAColumn_KeepsItsName_AndTheColumnIsRenamed()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(Bom()));
        var column = doc.Descendants(RdlNs2005 + "Field")
            .Single(f => (string?)f.Element(RdlNs2005 + "DataField") == "Header.ParentPrice");

        Assert.That((string?)column.Attribute("Name"), Is.EqualTo("ParentPrice_column"));
        Assert.That(FieldValue(doc, "ParentPrice"), Does.Contain("Fields!ParentPrice_column.Value").And.Contain("ParentCurrency"));
    }

    // Where the column is also placed on its own, the two would share a reference; the column
    // keeps the name, as before.
    [Test]
    public void AFormulaNamedLikeAColumnThatIsAlsoPlaced_LeavesTheColumnItsName()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Shadowed",
            Fields = [
                new DatabaseField { Name = "Price", ColumnName = "Price", TableName = "T", DataType = "Currency" },
                new FormulaField { Name = "Price", FormulaText = "ToText({T.Price}) + ' $'", ResultType = "String" },
            ],
            Sections = [
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "F1", FieldName = "@Price", Bounds = new(0, 0, 1440, 240) },
                    new FieldObject { Name = "F2", FieldName = "Price", Bounds = new(1500, 0, 1440, 240) }] }]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Not.Contain("Price_column"));
    }

    // BOM's page footer holds "Page: 1 of 1" in one of several languages, a formula of a
    // parameter and the page numbers. As a formula field it was put in the table's footer,
    // which prints straight after the details; reading no data, it is written in its place
    // in the RDL page footer, at the foot of the page as in Crystal.
    [Test]
    public void APageFooterFormulaThatReadsNoData_StaysInThePageFooter()
    {
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(Bom()));
        var footer = doc.Descendants(RdlNs2005 + "PageFooter").SingleOrDefault();

        Assert.That(footer, Is.Not.Null);
        string value = footer!.Descendants(RdlNs2005 + "Value").Single().Value;
        Assert.That(value, Does.Contain("Globals!TotalPages").And.Contain("Parameters!CURRENT_LANGUAGE"));
        Assert.That(value, Does.Not.Contain("Fields!"));
    }

    // A percentage of a group's total divides by the sum over that group, which the engine
    // takes as an aggregate's quoted scope; a share of the grand total by the DataSet's.
    [TestCase("synthetic__pct_base_grandtotal", "DataSet1")]
    [TestCase("synthetic__pct_base_outergroup", "Group1")]
    [TestCase("synthetic__pct_base_innergroup", "Group2")]
    public void APercentage_DividesByItsBaseGroupsSum(string stem, string scope)
    {
        string path = Path.GetFullPath($"../../../../rpt-corpus-external/{stem}.rpt", AppContext.BaseDirectory);
        if (!File.Exists(path))
            Assert.Ignore($"{stem}.rpt not found — run scripts/download-test-rpts.sh");
        string rdl = new RdlConverter().Convert(RptParser.Parse(path).Report!);

        Assert.That(rdl, Does.Contain($"Sum(Fields!amount.Value) / Sum(Fields!amount.Value, &quot;{scope}&quot;) * 100")
            .Or.Contain($"Sum(Fields!amount.Value) / Sum(Fields!amount.Value, \"{scope}\") * 100"));
    }

    // Crystal prints the report header first on page 1 and the page header below it, so an
    // empty page header is a gap between them. The pct_base probes' report and page headers
    // are both 600 twips and empty; without the page header's, their groups printed 30pt
    // above Crystal's. As an RDL page header the gap would come above the report header.
    [Test]
    public void AnEmptyPageHeader_IsAGapBelowTheReportHeader()
    {
        string path = Path.GetFullPath("../../../../rpt-corpus-external/synthetic__pct_base_innergroup.rpt", AppContext.BaseDirectory);
        if (!File.Exists(path))
            Assert.Ignore("synthetic__pct_base_innergroup.rpt not found — run scripts/download-test-rpts.sh");
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(RptParser.Parse(path).Report!));

        Assert.That(doc.Descendants(RdlNs2005 + "PageHeader"), Is.Empty);
        Assert.That((string?)doc.Descendants(RdlNs2005 + "Table").First().Element(RdlNs2005 + "Top"), Is.EqualTo("60pt"));
    }

    // StatementOfAccount has two page footers: the company address, then the publisher's
    // block with the page number. Only the first was written.
    [Test]
    public void APageFooterOfTwoSections_PrintsBoth()
    {
        var path = Path.Combine(CorpusDir, "souvikduttachoudhury__StatementOfAccount.rpt");
        if (!File.Exists(path))
            Assert.Ignore("souvikduttachoudhury__StatementOfAccount.rpt not found — run scripts/download-test-rpts.sh");
        var doc = System.Xml.Linq.XDocument.Parse(new RdlConverter().Convert(RptParser.Parse(path).Report!));
        var footer = doc.Descendants(RdlNs2005 + "PageFooter").Single();
        var values = footer.Descendants(RdlNs2005 + "Value").Select(v => v.Value).ToList();

        Assert.That(values, Does.Contain("Xtreme Mountain Bikes, Inc."));
        Assert.That(values.Any(v => v.Contains("Globals!PageNumber")), Is.True, "the second section's page number");
    }

    // Crystal's IsDate is the engine's IsDateTime; written as IsDate it was "Function isdate
    // is not known", which loses the report.
    [Test]
    public void IsDate_IsWrittenAsIsDateTime()
    {
        string expr = FormulaTranspiler.ToRdlExpression(new FormulaField
            { Name = "F", FormulaText = "if isdate({T.D}) then 'y' else 'n'" });

        Assert.That(expr, Does.Contain("IsDateTime(").And.Not.Contain("isdate("));
    }

    // RDL has no justify, so the one Crystal alignment without a schema equivalent is
    // written the way the target engine names it. Default stays implicit (see above).
    [Test]
    public void RdlConverter_JustifiedText_IsWrittenWithTheNameTheEngineKnows()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Aligned",
            Sections =
            [
                new Section { Type = SectionType.ReportHeader, HeightTwips = 480,
                    Objects = [
                        new TextObject { Name = "T1", Text = "block of prose",
                            Bounds = new(0, 0, 5000, 240),
                            Format = new ObjectFormat { HAlign = HorizontalAlignment.Justify } },
                        new TextObject { Name = "T2", Text = "over on the right",
                            Bounds = new(0, 240, 5000, 240),
                            Format = new ObjectFormat { HAlign = HorizontalAlignment.Right } }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        Assert.That(rdl, Does.Contain("<TextAlign>Justified</TextAlign>"));
        Assert.That(rdl, Does.Contain("<TextAlign>Right</TextAlign>"));
        Assert.That(rdl, Does.Not.Contain("<TextAlign>Justify</TextAlign>"),
            "the model's name for it is not the name RDL is given");
    }

    // Crystal leaves gaps between columns. An RDL table's columns are contiguous, so a
    // column has to be as wide as the distance to the next one; taking each object's own
    // width instead closes every gap and drags everything to its right leftwards,
    // cumulatively. The table also starts where its first column does, not at the body's
    // left edge.
    [Test]
    public void RdlConverter_TableColumns_SpanTheGapsBetweenDetailObjects()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Spaced",
            Fields = [
                new DatabaseField { Name = "Left", ColumnName = "Left", DataType = "String" },
                new DatabaseField { Name = "Right", ColumnName = "Right", DataType = "String" }
            ],
            Page = new PageLayout
            {
                WidthTwips = 12240, HeightTwips = 15840,
                LeftMarginTwips = 240, RightMarginTwips = 240,
                TopMarginTwips = 240, BottomMarginTwips = 240
            },
            Sections =
            [
                // 720 twips of empty space between the first object's right edge (2160)
                // and the second object's left edge (2880).
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Left", Bounds = new(720, 0, 1440, 240) },
                        new FieldObject { FieldName = "Right", Bounds = new(2880, 0, 1440, 240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var table = doc.Descendants(ns + "Table").First();

        Assert.That(table.Element(ns + "Left")?.Value, Is.EqualTo("36pt"),
            "the table starts where its first column does");

        var widths = table.Element(ns + "TableColumns")!.Elements(ns + "TableColumn")
            .Select(c => c.Element(ns + "Width")!.Value).ToList();
        Assert.That(widths[0], Is.EqualTo("108pt"),
            "the first column reaches the second: 2880 - 720 twips, gap included");
        Assert.That(widths[1], Is.EqualTo("72pt"),
            "the last column has nothing to its right to measure to, so it keeps its own width");
    }

    // A Crystal group header commonly holds the group's own field at the left and column
    // labels further across. Cells used to be filled in declaration order, which put the
    // first label in the first cell and dropped the group field, so every group rendered
    // captioned with the wrong words and nameless. Which column an object belongs to is
    // decided by where it sits, against the detail objects below it.
    [Test]
    public void RdlConverter_GroupHeaderObjects_LandInTheColumnTheySitOver()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "Grouped Report",
            Fields = [
                new DatabaseField { Name = "Customer", ColumnName = "Customer", DataType = "String" },
                new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }
            ],
            Groups = [new GroupDefinition { Level = 0, FieldName = "Customer", SortOrder = GroupSortOrder.Ascending }],
            Sections =
            [
                // The label is declared first and sits over the second column; the group
                // field is declared second and sits over the first.
                new Section { Type = SectionType.GroupHeader, HeightTwips = 240, GroupLevel = 0,
                    Objects = [
                        new TextObject { Text = "Amount", Bounds = new(1440, 0, 1440, 240) },
                        new FieldObject { FieldName = "Customer", Bounds = new(0, 0, 1440, 240) }
                    ] },
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [
                        new FieldObject { FieldName = "Customer", Bounds = new(0, 0, 1440, 240) },
                        new FieldObject { FieldName = "Amount", Bounds = new(1440, 0, 1440, 240) }
                    ] }
            ]
        };

        string rdl = new RdlConverter().Convert(report);

        // Read the group header row's cells rather than searching the text: the group
        // expression and the detail row both mention the same field, so string order
        // proves nothing about which cell the header put it in.
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        var groupHeaderRow = doc.Descendants(ns + "TableGroup").FirstOrDefault()
            ?.Element(ns + "Header")?.Element(ns + "TableRows")?.Element(ns + "TableRow");
        Assert.That(groupHeaderRow, Is.Not.Null, "the group header row must be emitted");

        var values = groupHeaderRow!.Element(ns + "TableCells")!.Elements(ns + "TableCell")
            .Select(c => c.Descendants(ns + "Value").FirstOrDefault()?.Value ?? "")
            .ToList();

        Assert.That(values.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(values[0], Is.EqualTo("=Fields!Customer.Value"),
            "the group field sits over the first column, so it belongs in the first cell");
        Assert.That(values[1], Is.EqualTo("Amount"),
            "the label sits over the second column, so it belongs in the second cell");
    }

    // Objects were once flowed across the band because the format appeared to carry no
    // position. It does - in a separate record - so the converter must leave the parsed
    // position alone. Flowing a section whose objects share a left edge would spread a
    // vertical stack out into a row.
    [Test]
    public void RdlConverter_ObjectPositions_ArePreservedNotFlowed()
    {
        var page = new PageLayout
        {
            WidthTwips = 12240, HeightTwips = 15840,
            LeftMarginTwips = 720, RightMarginTwips = 720,
            TopMarginTwips = 720, BottomMarginTwips = 720
        };
        // A stack: same left, descending the band. A flow pass would put these side by side.
        var objects = Enumerable.Range(0, 3)
            .Select(n => (ReportObject)new TextObject
            {
                Text = $"line{n}",
                Bounds = new(0, n * 240, 2880, 240)
            })
            .ToList();

        var report = new ReportDefinition
        {
            ReportTitle = "Stack",
            Page = page,
            Sections = [new Section { Type = SectionType.PageHeader, HeightTwips = 720, Objects = objects }]
        };

        new RdlConverter().Convert(report);

        Assert.That(objects.Select(o => o.Bounds.Left), Is.EqualTo(new[] { 0, 0, 0 }),
            "objects sharing a left edge are a stack, not a row to be spread out");
        Assert.That(objects.Select(o => o.Bounds.Top), Is.EqualTo(new[] { 0, 240, 480 }));
    }

    // Crystal stores the designed band height, but an object placed against the bottom
    // edge can round past it once heights convert, and anything below the band is
    // clipped away.
    [Test]
    public void RdlConverter_SectionGrows_ToHoldAnObjectPastItsBottomEdge()
    {
        var overhanging = new TextObject { Text = "tail", Bounds = new(0, 400, 2880, 240) };
        var report = new ReportDefinition
        {
            ReportTitle = "Overhang",
            Page = new PageLayout
            {
                WidthTwips = 12240, HeightTwips = 15840,
                LeftMarginTwips = 720, RightMarginTwips = 720,
                TopMarginTwips = 720, BottomMarginTwips = 720
            },
            Sections = [new Section { Type = SectionType.PageHeader, HeightTwips = 500, Objects = [overhanging] }]
        };

        new RdlConverter().Convert(report);

        Assert.That(report.Sections[0].HeightTwips, Is.EqualTo(640),
            "the band must reach the bottom of the object it contains");
    }

    // A detail band's objects are recorded in the order the report was authored, which says
    // nothing about where they are drawn. Half the public corpus records at least one detail
    // field out of position. Two separate things go wrong when it happens, and both are
    // asserted here because one sort fixes both.
    private static ReportDefinition OutOfOrderDetailBand() => new()
    {
        ReportTitle = "Out Of Order",
        Page = new PageLayout
        {
            WidthTwips = 12240, HeightTwips = 15840,
            LeftMarginTwips = 720, RightMarginTwips = 720,
            TopMarginTwips = 720, BottomMarginTwips = 720
        },
        Fields =
        [
            new DatabaseField { Name = "Left", ColumnName = "Left", DataType = "String" },
            new DatabaseField { Name = "Middle", ColumnName = "Middle", DataType = "String" },
            new DatabaseField { Name = "Right", ColumnName = "Right", DataType = "String" }
        ],
        Sections =
        [
            new Section
            {
                Type = SectionType.Details,
                HeightTwips = 240,
                // Recorded left, RIGHT, middle - the rightmost object in front of the one
                // to its left, which is what ProductPriceList-xs does with Price.
                Objects =
                [
                    new FieldObject { FieldName = "Left", Bounds = new(0, 0, 1000, 240) },
                    new FieldObject { FieldName = "Right", Bounds = new(5000, 0, 1000, 240) },
                    new FieldObject { FieldName = "Middle", Bounds = new(2000, 0, 1000, 240) }
                ]
            }
        ]
    };

    // The values have to land in the columns the report draws them in.
    [Test]
    public void RdlConverter_OutOfOrderDetailBand_EmitsCellsLeftToRight()
    {
        string rdl = new RdlConverter().Convert(OutOfOrderDetailBand());
        // The Details band, not the copy of it in the footer that prints when there are no rows.
        string details = rdl[..rdl.IndexOf("</Details>", StringComparison.Ordinal)];

        var cells = System.Text.RegularExpressions.Regex
            .Matches(details, @"<Value>=Fields!(\w+)\.Value</Value>")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.That(cells, Is.EqualTo(new[] { "Left", "Middle", "Right" }));
    }

    // And the second fault, which rides on the first: column widths are the distance to the
    // next column, because Crystal leaves gaps and an RDL table's columns are contiguous.
    // That is guarded on the starts ascending, so an out-of-order band used to fall back to
    // each object's own width, closing every gap and dragging the band left cumulatively -
    // which is why the error grew across the row instead of being one swap at the end.
    //
    // 0-2000-5000 with a 1000-wide last object gives 2000, 3000, 1000 twips; the object
    // widths it fell back to would give 1000, 1000, 1000.
    [Test]
    public void RdlConverter_OutOfOrderDetailBand_StillMeasuresColumnsToTheNextColumn()
    {
        string rdl = new RdlConverter().Convert(OutOfOrderDetailBand());

        var widths = System.Text.RegularExpressions.Regex
            .Matches(rdl, @"<TableColumn>\s*<Width>([0-9.]+)pt</Width>")
            .Select(m => (int)Math.Round(double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) * 20))
            .ToList();

        // Exactly, with no tolerance. This used to need "within a twip or two" because the
        // width went out as inches to three decimals and 1000 twips came back as 999; a
        // twip is exactly 0.05pt, so points round-trip integer twips without loss. The
        // fallback this guards against is 1000/1000/1000.
        Assert.That(widths, Is.EqualTo(new[] { 2000, 3000, 1000 }));
    }

    // Highlighting rules become one nested IIf over the object's own value, first rule
    // outermost because Crystal applies the first match. A rule that sets only one of the
    // two colours must leave the other alone rather than resolving it to a default.
    private static ReportDefinition HighlightedSubtotal(params ConditionalFormat[] rules) => new()
    {
        ReportTitle = "Highlighted",
        Page = new PageLayout
        {
            WidthTwips = 12240, HeightTwips = 15840,
            LeftMarginTwips = 720, RightMarginTwips = 720,
            TopMarginTwips = 720, BottomMarginTwips = 720
        },
        Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
        Sections =
        [
            new Section
            {
                Type = SectionType.Details,
                HeightTwips = 240,
                Objects =
                [
                    new FieldObject
                    {
                        FieldName = "Amount",
                        Bounds = new(0, 0, 1440, 240),
                        Format = new ObjectFormat { Conditions = rules }
                    }
                ]
            }
        ]
    };

    [Test]
    public void RdlConverter_HighlightingRules_NestFirstRuleOutermost()
    {
        string rdl = new RdlConverter().Convert(HighlightedSubtotal(
            new ConditionalFormat(ConditionalOperator.LessThan, 1000, "#FF0000", "#C0C0C0"),
            new ConditionalFormat(ConditionalOperator.GreaterThan, 100000, "#008000", null)));

        Assert.That(rdl, Does.Contain(
            "<Color>=IIf(Fields!Amount.Value &lt; 1000, \"#FF0000\", "
            + "IIf(Fields!Amount.Value &gt; 100000, \"#008000\", \"#000000\"))</Color>"));
    }

    // The second rule sets no background, so the background expression must mention only the
    // first. Resolving the second to white there would paint over whatever is behind it.
    [Test]
    public void RdlConverter_HighlightingRule_WithoutABackground_LeavesItOutOfThatExpression()
    {
        string rdl = new RdlConverter().Convert(HighlightedSubtotal(
            new ConditionalFormat(ConditionalOperator.LessThan, 1000, "#FF0000", "#C0C0C0"),
            new ConditionalFormat(ConditionalOperator.GreaterThan, 100000, "#008000", null)));

        Assert.That(rdl, Does.Contain(
            "<BackgroundColor>=IIf(Fields!Amount.Value &lt; 1000, \"#C0C0C0\", "
            + "\"Transparent\")</BackgroundColor>"));
    }

    // A cell whose value is literal text has nothing to compare, and emitting a comparison
    // against it would either fail in the engine's expression parser or never match. The
    // rules are dropped and the object keeps its own colours.
    [Test]
    public void RdlConverter_HighlightingRules_AreDroppedWhenTheValueIsNotAnExpression()
    {
        // In a page header, so it is emitted free-form with its own style rather than
        // routed through a details table.
        var report = HighlightedSubtotal(
            new ConditionalFormat(ConditionalOperator.LessThan, 1000, "#FF0000", null));
        report.Sections.Add(new Section
        {
            Type = SectionType.PageHeader,
            HeightTwips = 240,
            Objects =
            [
                new TextObject
                {
                    Text = "Total",
                    Bounds = new(0, 0, 1440, 240),
                    Format = new ObjectFormat
                    {
                        ForeColor = "#123456",
                        Conditions =
                            [new ConditionalFormat(ConditionalOperator.LessThan, 1000, "#FF0000", null)]
                    }
                }
            ]
        });
        report.Sections[0].Objects.Clear();

        string rdl = new RdlConverter().Convert(report);

        // The DataSet reads a null Amount as 0 (see NullFieldTests); the report items are what
        // is tested here.
        Assert.That(rdl[rdl.IndexOf("</DataSets>", StringComparison.Ordinal)..], Does.Not.Contain("IIf"));
        Assert.That(rdl, Does.Contain("<Color>#123456</Color>"));
    }

    // Crystal's font size is the character cell height - ascent plus descent - because it
    // hands the size to GDI as a positive lfHeight. RDL's FontSize is the em size. Emitting
    // the number unchanged renders every glyph about 11% too wide, which this repo had
    // recorded as an unfixable renderer difference.
    //
    // The expected values are not derived from the table the converter uses: they are what
    // Crystal's own PDF export writes. A 10pt Arial object comes out "8.95 Tf", a 16pt one
    // "14.3", a 33pt one "29.55"; Verdana at 8pt is "6.6" and at 10pt "8.25". They are
    // compared exactly, because the em is GDI's, a whole number of twips, and the converter
    // now reproduces it rather than approximating it. The ratio alone is close for these and
    // well off for the bold ones: Arial Bold 11pt is 9.65 where it gives 9.85, Verdana Bold
    // 12pt 9.95 where it gives 9.87 (see GdiEmSizes).
    [TestCase("Arial", 10.0, false, false, 8.95)]
    [TestCase("Arial", 16.0, false, false, 14.3)]
    [TestCase("Arial", 33.0, false, false, 29.55)]
    [TestCase("Arial", 11.0, true, false, 9.65)]
    [TestCase("Verdana", 8.0, false, false, 6.6)]
    [TestCase("Verdana", 10.0, false, false, 8.25)]
    [TestCase("Verdana", 12.0, true, false, 9.95)]
    [TestCase("Verdana", 18.0, true, true, 14.8)]
    public void RdlConverter_FontSize_IsTheEmSizeCrystalActuallyRenders(
        string family, double crystalPoints, bool bold, bool italic, double expectedEm)
    {
        string rdl = new RdlConverter().Convert(StyledDetail(family, crystalPoints, bold, italic));

        var m = System.Text.RegularExpressions.Regex.Match(rdl, @"<FontSize>([0-9.]+)pt</FontSize>");
        Assert.That(m.Success, Is.True, "no FontSize emitted");
        Assert.That(double.Parse(m.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture),
            Is.EqualTo(expectedEm).Within(1e-9));
    }

    // A family whose metrics are not known is emitted unchanged rather than scaled by some
    // other font's ratio. The ratio is a property of the font, and guessing one would
    // mis-size text with nothing to reveal it.
    [Test]
    public void RdlConverter_FontSize_OfAnUnknownFamily_IsLeftAlone()
    {
        string rdl = new RdlConverter().Convert(StyledDetail("Wingdings Fictional", 10.0));

        Assert.That(rdl, Does.Contain("<FontSize>10pt</FontSize>"));
    }

    private static ReportDefinition StyledDetail(string family, double points,
        bool bold = false, bool italic = false) => new()
    {
        ReportTitle = "Sized",
        Page = new PageLayout
        {
            WidthTwips = 12240, HeightTwips = 15840,
            LeftMarginTwips = 720, RightMarginTwips = 720,
            TopMarginTwips = 720, BottomMarginTwips = 720
        },
        Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", DataType = "Float64" }],
        Sections =
        [
            new Section
            {
                Type = SectionType.Details,
                HeightTwips = 240,
                Objects =
                [
                    new FieldObject
                    {
                        FieldName = "Amount",
                        Bounds = new(0, 0, 1440, 240),
                        Format = new ObjectFormat { FontName = family, FontSize = points, Bold = bold, Italic = italic }
                    }
                ]
            }
        ]
    };

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
}
