using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// What a certificate laid out as a letter prints as Crystal prints it: a letterhead in two
/// sizes of type, a column label typed with a final Enter, a number inside its text, an
/// owner's address read from the second of two tables with the same column, a report
/// header suppressed outright, and the footer of a report that reads no table.
/// </summary>
[TestFixture]
public class LetterLayoutTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static XDocument Converted(ReportDefinition report) => XDocument.Parse(new RdlConverter().Convert(report));

    private static XElement Textbox(XDocument rdl, string name) =>
        rdl.Descendants(Rdl + "Textbox").Single(t => (string?)t.Attribute("Name") == name);

    private static double Points(XElement item, string element) =>
        double.Parse(((string)item.Element(Rdl + element)!).Replace("pt", ""), System.Globalization.CultureInfo.InvariantCulture);

    private static double FontSize(XElement textbox) =>
        double.Parse(((string)textbox.Element(Rdl + "Style")!.Element(Rdl + "FontSize")!).Replace("pt", ""),
            System.Globalization.CultureInfo.InvariantCulture);

    // ── A text object's records ────────────────────────────────────────────────────────

    private static TslvRecord Record(int tag, params byte[] data) => new() { Tag = tag, Data = data };

    private static byte[] Mutf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        return [0, 0, (byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }

    // A tag-8 font record: the family, then flags, the size in points, no style, a weight of
    // 400 and the size in twentieths.
    private static TslvRecord Font(string family, int points) =>
        Record(8, [.. Mutf8(family), 0x20, 0x02, 0x01, 0x00, (byte)points, 0, 0, 0, 0, 0, 0, 0x01, 0x90,
            0, 0, (byte)(points * 20 >> 8), (byte)(points * 20)]);

    // A tag-249 number format: its tag-248 child, two decimals, a leading minus and grouped
    // thousands, as Crystal's default for a number is.
    private static TslvRecord NumberFormat()
    {
        var d = new List<byte> { 0, 0, 1, 0, 1, 0, 1, 0, 2, 0, 0, 0, 0, 0 };
        d.AddRange(Mutf8(",")); d.AddRange(Mutf8(".")); d.AddRange(Mutf8("$"));
        d.AddRange(new byte[6]);
        d.AddRange(Mutf8("<Default Format>"));
        var child = new List<byte> { 0xE0, 0xF8, 0, 0, 0, 0, (byte)(d.Count >> 8), (byte)d.Count };
        child.AddRange(d.Select(b => (byte)(b ^ 0xF8)));
        return Record(249, [.. child]);
    }

    private static List<TslvRecord> Paragraphs(params (string Text, int Points)[] paragraphs)
    {
        var records = new List<TslvRecord> { Record(165) };
        foreach (var (text, points) in paragraphs)
        {
            records.Add(Record(192, new byte[23]));
            records.Add(Record(194, Mutf8(text)));
            records.Add(Font("Arial", points));
            records.Add(Record(195));
            records.Add(Record(193));
        }
        records.Add(Record(166));
        return records;
    }

    [Test]
    public void ALabelTypedWithAFinalEnter_PrintsInTheFontOfItsText()
    {
        // "Year" in 12pt, then an empty paragraph in 8pt: Crystal prints the label in 12pt,
        // and taken from the last font record it printed in 8pt.
        var text = RptParser.TextObjectFrom(Paragraphs(("Year", 12), ("", 8)))!;

        Assert.Multiple(() =>
        {
            Assert.That(text.Text, Is.EqualTo("Year"));
            Assert.That(text.Format.FontSize, Is.EqualTo(12));
        });
    }

    [Test]
    public void TextEndingInALineOfTabs_PrintsInTheFontOfItsText()
    {
        // The tabs print nothing; taken from their font, the paragraph above printed in 12pt.
        var text = RptParser.TextObjectFrom(Paragraphs(("We certify the taxes are paid.", 10), ("\t\t\t", 12)))!;

        Assert.That(text.Format.FontSize, Is.EqualTo(10));
    }

    // A tag-8 font record in bold: a weight of 700.
    private static TslvRecord BoldFont(string family, int points) =>
        Record(8, [.. Mutf8(family), 0x20, 0x02, 0x01, 0x00, (byte)points, 0, 0, 0, 0, 0, 0, 0x02, 0xBC,
            0, 0, (byte)(points * 20 >> 8), (byte)(points * 20)]);

    [Test]
    public void TextInOneSizeWithBoldRunsAndAFinalEnter_PrintsInTheSizeOfItsText()
    {
        // A notice's paragraph in 14pt with its service in bold, then an empty paragraph in
        // 10pt: Crystal prints the paragraph in 14pt, its plain runs plain; taken from the last
        // font record it printed in 10pt.
        var records = new List<TslvRecord>
        {
            Record(165), Record(192, new byte[23]),
            Record(194, Mutf8("This is to advise that the ")), Font("Arial", 14), Record(195),
            Record(194, Mutf8("WATER SERVICE")), BoldFont("Arial", 14), Record(195),
            Record(194, Mutf8(" to this property has been disconnected.")), Font("Arial", 14), Record(195),
            Record(193), Record(192, new byte[23]), Record(194, Mutf8("")), Font("Arial", 10), Record(195), Record(193),
            Record(166),
        };
        var text = RptParser.TextObjectFrom(records)!;

        Assert.Multiple(() =>
        {
            Assert.That(text.Format.FontSize, Is.EqualTo(14));
            Assert.That(text.Format.FontName, Is.EqualTo("Arial"));
            Assert.That(text.Format.Bold, Is.False, "the longest run is plain");
        });
    }

    [Test]
    public void TextInTwoFontsWithAFinalEnter_KeepsTheLastFontRecord()
    {
        // Text in several fonts is drawn in the last run's; the empty line does not change that.
        var text = RptParser.TextObjectFrom(Paragraphs(("Number", 14), ("Code", 16), ("", 10)))!;

        Assert.That(text.Format.FontSize, Is.EqualTo(10));
    }

    // A tag-192 paragraph record whose byte 12 is its alignment: 1 left, 2 centre.
    private static TslvRecord Paragraph(byte align)
    {
        var data = new byte[23];
        data[12] = align;
        return Record(192, data);
    }

    [Test]
    public void TextAfterAnEmptyParagraph_PrintsInItsOwnParagraphsAlignment()
    {
        // An invoice's closing message, centred, typed after an empty paragraph set at the left:
        // Crystal centres it, and taken from the first paragraph it printed at the left.
        var records = new List<TslvRecord>
        {
            Record(165),
            Paragraph(1), Record(194, Mutf8("")), Font("Arial", 7), Record(195), Record(193),
            Paragraph(2), Record(194, Mutf8("Please read the back of this invoice.")), Font("Arial", 10), Record(195), Record(193),
            Paragraph(2), Record(194, Mutf8("Thank you.")), Font("Arial", 10), Record(195), Record(193),
            Record(166),
        };

        var text = RptParser.TextObjectFrom(records)!;

        Assert.That(text.Format.HAlign, Is.EqualTo(HorizontalAlignment.Center));
    }

    [Test]
    public void AFieldEmbeddedInText_KeepsItsNumberFormat()
    {
        var records = new List<TslvRecord> { Record(165), Record(192, new byte[23]),
            Record(194, Mutf8("Owing $")), Font("Arial", 10),
            Record(196, Mutf8("@Total")), Font("Arial", 10), NumberFormat(), NumberFormat(), Record(197),
            Record(195), Record(193), Record(166) };

        var text = RptParser.TextObjectFrom(records)!;

        Assert.That(text.FieldNumberFormats["@Total"], Is.EqualTo(("#,##0.00", "#,##0.00")));
    }

    [Test]
    public void ANumberInsideText_PrintsInItsNumberFormat()
    {
        // "${@Total}" printed "$5096" for Crystal's "$5,096.00".
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Bills", DataType = "Float64" },
                      new FormulaField { Name = "Total", FormulaText = "{Bills.Amount}", ResultType = "Float64" }],
            Sections = [new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [new TextObject
            {
                Name = "Owing", Text = "Owing ${@Total}", Bounds = new(0, 0, 5760, 240),
                FieldNumberFormats = new() { ["@Total"] = ("\"$\"#,##0.00", "#,##0.00") },
            }] }],
        };

        string value = Textbox(Converted(report), "Owing").Element(Rdl + "Value")!.Value;

        Assert.That(value, Does.Contain("Format(Fields!Total.Value, \"#,##0.00\")"));
    }

    [Test]
    public void ALetterheadInTwoSizes_PrintsEachLineInItsOwnSize()
    {
        // The name in 18pt over the address in 10pt; one textbox has one font, and the whole
        // letterhead printed in 10pt.
        var letterhead = new TextObject
        {
            Name = "Letterhead", Text = "Town Office\nMain Street\nPhone", Bounds = new(0, 0, 7200, 1300),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Town Office", "Arial", 18, false, false, false),
                    new TextRun("\nMain Street\nPhone", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1300, Objects = [letterhead] }],
        };

        var rdl = Converted(report);
        var name = Textbox(rdl, "Letterhead");
        var address = Textbox(rdl, "Letterhead_Line1");

        Assert.Multiple(() =>
        {
            Assert.That(name.Element(Rdl + "Value")!.Value, Is.EqualTo("Town Office"));
            Assert.That(FontSize(name), Is.GreaterThan(FontSize(address) * 1.5));
            Assert.That(Points(address, "Top"), Is.EqualTo(Points(name, "Top") + Points(name, "Height")).Within(0.01));
            Assert.That(Points(name, "Height"), Is.GreaterThan(18).And.LessThan(24));
            Assert.That(Points(address, "Top") + Points(address, "Height"), Is.EqualTo(65).Within(0.01));
        });
    }

    [Test]
    public void AHeadingOverParagraphsWithBoldWords_PrintsEachInItsOwnSizeAndStyles()
    {
        // A notice's 12pt underlined heading over 11pt paragraphs with bold words in them.
        // The size changes only where a line ends, but the bold words begin inside a line;
        // the whole notice printed plain in the object's own 10pt.
        var notice = new TextObject
        {
            Name = "Notice", Text = "Outstanding Taxes\n\nPlease note, payment is due now.", Bounds = new(0, 0, 7200, 1300),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Outstanding Taxes\n", "Arial", 12, true, false, true),
                    new TextRun("\nPlease note", "Arial", 11, true, false, false),
                    new TextRun(", payment is due now.", "Arial", 11, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1300, Objects = [notice] }],
        };

        var rdl = Converted(report);
        var heading = Textbox(rdl, "Notice");
        var body = Textbox(rdl, "Notice_Line1");
        var bodyStyle = body.Element(Rdl + "Style")!;

        Assert.Multiple(() =>
        {
            Assert.That(heading.Element(Rdl + "Value")!.Value, Is.EqualTo("=\"Outstanding Taxes\" & Chr(10) & \"\""));
            Assert.That((string?)heading.Element(Rdl + "Style")!.Element(Rdl + "FontWeight"), Is.EqualTo("Bold"));
            Assert.That(FontSize(heading), Is.GreaterThan(FontSize(body)));
            Assert.That((string?)bodyStyle.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That(body.Element(Rdl + "Value")!.Value, Does.StartWith("<b>Please note</b>, payment"));
            Assert.That(Points(body, "Top"), Is.EqualTo(Points(heading, "Top") + Points(heading, "Height")).Within(0.01));
            Assert.That(Points(body, "Top") + Points(body, "Height"), Is.EqualTo(65).Within(0.01));
        });
    }

    // A size that changes inside a line has no line to split the text at, and the object
    // stays whole.
    [Test]
    public void ASizeThatChangesInsideALine_KeepsTheObjectWhole()
    {
        var notice = new TextObject
        {
            Name = "Notice", Text = "Amount Due: 12.00", Bounds = new(0, 0, 7200, 600),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Amount Due: ", "Arial", 10, true, false, false),
                    new TextRun("12.00", "Arial", 14, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 600, Objects = [notice] }],
        };

        var rdl = Converted(report);

        Assert.That(rdl.Descendants(Rdl + "Textbox").Select(t => (string?)t.Attribute("Name")), Is.EqualTo(new[] { "Notice" }));
    }

    [Test]
    public void ALetterWhoseParagraphsWrap_StaysOneTextbox()
    {
        // Split, each piece was as tall as its hard lines and lost the lines its paragraph
        // wrapped onto.
        var text = new TextObject
        {
            Name = "Letter", Text = "Notice\nThe amount shown above includes interest to the date of this letter and must be paid in full.\nYours truly",
            Bounds = new(0, 0, 4320, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 11, IsTextObject = true },
            Runs = [new TextRun("Notice", "Arial", 12, true, false, false),
                    new TextRun("\nThe amount shown above includes interest to the date of this letter and must be paid in full.", "Arial", 10, false, false, false),
                    new TextRun("\nYours truly", "Arial", 11, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [text] }],
        };

        var rdl = Converted(report);

        Assert.That(rdl.Descendants(Rdl + "Textbox").Count(t => ((string?)t.Attribute("Name"))?.StartsWith("Letter") == true),
            Is.EqualTo(1));
    }

    [Test]
    public void BoldAndUnderlinedWordsInAParagraph_PrintInTheirOwnStyle()
    {
        // A notice's paragraph in 10pt with its service in bold and its contact underlined:
        // Crystal prints each run in its own style; one textbox printed it all plain.
        var text = new TextObject
        {
            Name = "Notice", Text = "The WATER SERVICE ends on  {Notices.Contact} & after.\nThank you",
            Bounds = new(0, 0, 7200, 720),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("The ", "Arial", 10, false, false, false),
                    new TextRun("WATER SERVICE", "Arial", 10, true, false, false),
                    new TextRun(" ends on  ", "Arial", 10, false, false, false),
                    new TextRun("{Notices.Contact}", "Arial", 10, false, false, true),
                    new TextRun(" & after.\nThank you", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Contact", ColumnName = "Contact", TableName = "Notices", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [text] }],
        };

        var notice = Textbox(Converted(report), "Notice");

        Assert.Multiple(() =>
        {
            Assert.That((string?)notice.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That(notice.Element(Rdl + "Value")!.Value, Does.Contain(
                "The <b>WATER SERVICE</b> ends on&nbsp; <span style=\"\"text-decoration:underline\"\">"));
            Assert.That(notice.Element(Rdl + "Value")!.Value, Does.Contain("</span> &amp; after.<br>Thank you"));
            Assert.That(notice.Element(Rdl + "Value")!.Value, Does.Contain("Replace(Replace(Replace(Replace(Replace(Replace(CStr("),
                "a field's value has its own line breaks and tags made html");
        });
    }

    // Paragraphs, each a tag-192 record whose byte 12 is its alignment (1 left, 2 centre, 4
    // justified), with its text in 10pt Arial.
    private static List<TslvRecord> AlignedParagraphs(params (string Text, byte Align)[] paragraphs)
    {
        var records = new List<TslvRecord> { Record(165) };
        foreach (var (text, align) in paragraphs)
        {
            var paragraph = new byte[23];
            paragraph[12] = align;
            records.AddRange([Record(192, paragraph), Record(194, Mutf8(text)), Font("Arial", 10), Record(195), Record(193)]);
        }
        records.Add(Record(166));
        return records;
    }

    [Test]
    public void ParagraphsAlignedApart_AreReadWithWhereEachStarts()
    {
        var text = RptParser.TextObjectFrom(AlignedParagraphs(("", 1), ("TITLE", 2), ("Name:", 1), ("", 2), ("Signed", 4)))!;
        var alike = RptParser.TextObjectFrom(AlignedParagraphs(("Name:", 1), ("", 2), ("Address:", 1)))!;

        Assert.Multiple(() =>
        {
            Assert.That(text.Text, Is.EqualTo("TITLE\nName:\n\nSigned"));
            Assert.That(text.Paragraphs, Is.EqualTo(new[]
            {
                new TextParagraph(0, HorizontalAlignment.Center), new TextParagraph(6, HorizontalAlignment.Left),
                new TextParagraph(12, HorizontalAlignment.Center), new TextParagraph(13, HorizontalAlignment.Justify),
            }), "the empty paragraph before the text gives way to the one the text starts in");
            Assert.That(alike.Paragraphs, Is.Empty, "an empty paragraph's alignment does not print");
        });
    }

    [Test]
    public void ParagraphsAlignedApart_PrintEachInItsOwnAlignment()
    {
        // A form's centred heading over its lines at the left, and a centred note after an
        // empty line: Crystal aligns each paragraph on its own; one textbox centred them all.
        var form = new TextObject
        {
            Name = "Form", Text = "PLEASE ATTACH A CHEQUE\nAccount Type:  Savings\n\nThank you",
            Bounds = new(0, 0, 9000, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, HAlign = HorizontalAlignment.Center, IsTextObject = true },
            Paragraphs = [new(0, HorizontalAlignment.Center), new(23, HorizontalAlignment.Left),
                          new(46, HorizontalAlignment.Left), new(47, HorizontalAlignment.Center)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [form] }],
        };

        var box = Textbox(Converted(report), "Form");

        Assert.Multiple(() =>
        {
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That(box.Element(Rdl + "Value")!.Value, Is.EqualTo(
                "<div style=\"text-align:center\">PLEASE ATTACH A CHEQUE</div><div style=\"text-align:left\">Account Type:&nbsp; Savings</div>"
                + "<div style=\"text-align:left\">&nbsp;</div><div style=\"text-align:center\">Thank you</div>"));
            Assert.That((string?)box.Element(Rdl + "Custom")?.Element(Rdl + "WrapInset"), Does.EndWith("pt"),
                "the lines break short of the right edge, and are aligned against the object's own");
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "PaddingLeft"), Is.Null.Or.EqualTo("0pt"));
        });
    }

    [Test]
    public void JustifiedWordsInBold_PrintInTheirOwnStyle()
    {
        // A justified notice with its amount in bold: the engine spreads justified html lines,
        // so the runs are written as html as in text at the left, breaking short by WrapInset.
        var notice = new TextObject
        {
            Name = "Notice", Text = "The amount of $10.00 is due on receipt of this notice, and must be paid in full by the date shown.",
            Bounds = new(0, 0, 4320, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, HAlign = HorizontalAlignment.Justify, IsTextObject = true },
            Runs = [new TextRun("The amount of ", "Arial", 10, false, false, false),
                    new TextRun("$10.00", "Arial", 10, true, false, false),
                    new TextRun(" is due on receipt of this notice, and must be paid in full by the date shown.", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [notice] }],
        };

        var box = Textbox(Converted(report), "Notice");

        Assert.Multiple(() =>
        {
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "TextAlign"), Is.EqualTo("Justified"));
            Assert.That(box.Element(Rdl + "Value")!.Value, Does.StartWith("The amount of <b>$10.00</b> is due"));
            Assert.That((string?)box.Element(Rdl + "Custom")?.Element(Rdl + "WrapInset"), Does.EndWith("pt"));
        });
    }

    [Test]
    public void RichText_BreaksItsLinesInTheStyleItsRunsPrintIn()
    {
        // A justified notice whose object format says bold while every run in it is plain:
        // Crystal measures the lines in the plain font they print in, so the lines break where
        // a plain object's do. Measured as bold, they broke a word short on every line.
        TextObject Notice(string name, bool objectBold) => new()
        {
            Name = name, Text = "Please note that the taxes on this property are now in arrears. The property will be listed for sale.",
            Bounds = new(0, 0, 7200, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, Bold = objectBold, HAlign = HorizontalAlignment.Justify, IsTextObject = true },
            Runs = [new TextRun("Please note that the taxes on this property are now in arrears.", "Arial", 10, false, false, false),
                    new TextRun(" The property will be listed for sale.", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [Notice("BoldObject", true), Notice("PlainObject", false)] }],
        };

        var rdl = Converted(report);
        string? Inset(string name) => (string?)Textbox(rdl, name).Element(Rdl + "Custom")?.Element(Rdl + "WrapInset");

        Assert.Multiple(() =>
        {
            Assert.That(Inset("PlainObject"), Does.EndWith("pt"));
            Assert.That(Inset("BoldObject"), Is.EqualTo(Inset("PlainObject")));
        });
    }

    [Test]
    public void TabsInRichText_GoToCrystalsTabStops()
    {
        // A label in bold, a tab, then its value: the tab is the engine's <tab>, at Crystal's
        // quarter-inch stops; written plain, the label printed plain.
        var line = new TextObject
        {
            Name = "Line", Text = "Account:\t1234\nOwner:\tA. Person",
            Bounds = new(0, 0, 4320, 720),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Account:", "Arial", 10, true, false, false),
                    new TextRun("\t1234\n", "Arial", 10, false, false, false),
                    new TextRun("Owner:", "Arial", 10, true, false, false),
                    new TextRun("\tA. Person", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [line] }],
        };

        var box = Textbox(Converted(report), "Line");

        Assert.Multiple(() =>
        {
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That(box.Element(Rdl + "Value")!.Value, Is.EqualTo("<b>Account:</b><tab>1234<br><b>Owner:</b><tab>A. Person"));
            Assert.That((string?)box.Element(Rdl + "Custom")?.Element(Rdl + "TabStopInterval"), Is.EqualTo("18pt"));
        });
    }

    [Test]
    public void GrowingRichText_IsWrittenAsHtml_AndKeepsAFieldsSpaces()
    {
        // A text that may grow, with a field: the engine grows html text by the lines it draws,
        // as it grows plain text. The field's spaces in a row are kept, as plain text keeps them.
        var note = new TextObject
        {
            Name = "Note", Text = "Owner:\n{Owners.Address}\n",
            Bounds = new(0, 0, 4320, 240),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, CanGrow = true, IsTextObject = true },
            Runs = [new TextRun("Owner:", "Arial", 10, true, false, false),
                    new TextRun("\n{Owners.Address}\n", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Address", ColumnName = "Address", TableName = "Owners", DataType = "String" }],
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [note] }],
        };

        var box = Textbox(Converted(report), "Note");

        Assert.Multiple(() =>
        {
            Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.EqualTo("html"));
            Assert.That((string?)box.Element(Rdl + "CanGrow"), Is.EqualTo("true"));
            Assert.That(box.Element(Rdl + "Value")!.Value, Does.StartWith("=\"<b>Owner:</b><br>\""));
            Assert.That(box.Element(Rdl + "Value")!.Value, Does.Contain(", \"  \", \" &nbsp;\")"));
        });
    }

    [Test]
    public void ParagraphsOnlyJustifiedOrLeft_StayPlainText()
    {
        // A justified box justifies these as Crystal does, the line at the left being one line.
        var letter = new TextObject
        {
            Name = "Letter", Text = "Please pay the amount owing.\nYours truly",
            Bounds = new(0, 0, 9000, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, HAlign = HorizontalAlignment.Justify, IsTextObject = true },
            Paragraphs = [new(0, HorizontalAlignment.Justify), new(29, HorizontalAlignment.Left)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [letter] }],
        };

        var box = Textbox(Converted(report), "Letter");

        Assert.That((string?)box.Element(Rdl + "Style")!.Element(Rdl + "Format"), Is.Null);
    }

    [Test]
    public void LinesInTheirOwnFonts_KeepTheirParagraphsAlignment()
    {
        // A centred 12pt title over a line at the left in 10pt is printed as one textbox for
        // each font; the line took the title's alignment.
        var form = new TextObject
        {
            Name = "Form", Text = "TENDER FORM\nName:",
            Bounds = new(0, 0, 9000, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 12, HAlign = HorizontalAlignment.Center, IsTextObject = true },
            Runs = [new TextRun("TENDER FORM", "Arial", 12, false, false, false), new TextRun("\nName:", "Arial", 10, false, false, false)],
            Paragraphs = [new(0, HorizontalAlignment.Center), new(12, HorizontalAlignment.Left)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [form] }],
        };

        var rdl = Converted(report);

        Assert.Multiple(() =>
        {
            Assert.That((string?)Textbox(rdl, "Form").Element(Rdl + "Style")!.Element(Rdl + "TextAlign"), Is.EqualTo("Center"));
            Assert.That((string?)Textbox(rdl, "Form_Line1").Element(Rdl + "Style")!.Element(Rdl + "TextAlign"), Is.EqualTo("Left"));
        });
    }

    [Test]
    public void ALetterWhoseLastParagraphWraps_PrintsItInItsOwnSize()
    {
        // The last piece keeps the rest of the box, so the paragraph wraps as the whole
        // object's would; kept whole, the 12pt heading printed in the paragraph's 10pt.
        var text = new TextObject
        {
            Name = "Letter", Text = "Notice\nThe amount shown above includes interest to the date of this letter and must be paid in full.",
            Bounds = new(0, 0, 4320, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Notice", "Arial", 12, true, false, false),
                    new TextRun("\nThe amount shown above includes interest to the date of this letter and must be paid in full.", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [text] }],
        };

        var rdl = Converted(report);
        var heading = Textbox(rdl, "Letter");
        var body = Textbox(rdl, "Letter_Line1");

        Assert.Multiple(() =>
        {
            Assert.That(FontSize(heading), Is.GreaterThan(FontSize(body)));
            Assert.That(Points(body, "Top") + Points(body, "Height"), Is.EqualTo(72).Within(0.01));
        });
    }

    [Test]
    public void TextEndingInTabsInAnotherSize_IsBrokenWhereCrystalBreaksIt()
    {
        // The tabs print nothing. Counted as a second size, they kept the text whole, in a
        // textbox not padded to break its lines where Crystal's break.
        var text = new TextObject
        {
            Name = "Certify", Text = "We certify that the taxes on the property described above are paid in full.\n\t\t\t",
            Bounds = new(0, 0, 4320, 1440),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("We certify that the taxes on the property described above are paid in full.", "Arial", 10, false, false, false),
                    new TextRun("\n\t\t\t", "Arial", 12, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 1440, Objects = [text] }],
        };

        var rdl = Converted(report);
        var certify = Textbox(rdl, "Certify");

        Assert.Multiple(() =>
        {
            Assert.That(rdl.Descendants(Rdl + "Textbox").Count(t => ((string?)t.Attribute("Name"))?.StartsWith("Certify") == true),
                Is.EqualTo(1));
            Assert.That(Points(certify, "Height"), Is.EqualTo(72).Within(0.01));
            Assert.That(certify.Element(Rdl + "Style")!.Element(Rdl + "PaddingRight"), Is.Not.Null);
        });
    }

    [Test]
    public void ALinesWidth_IsTheSumOfItsAdvancesAtTheSize()
    {
        Assert.Multiple(() =>
        {
            // Arial's "A" is 1366 units of its 2048-unit em, its "i" 455.
            Assert.That(TextWidths.Points("Arial", 10, false, false, "Ai"), Is.EqualTo((1366 + 455) * 10 / 2048.0).Within(1e-9));
            Assert.That(TextWidths.Points("arial", 10, false, false, "\u2019"), Is.Not.Null);
            Assert.That(TextWidths.Points("Tahoma", 10, true, true, "A"), Is.EqualTo(TextWidths.Points("Tahoma", 10, true, false, "A")));
            Assert.That(TextWidths.Points("No Such Font", 10, false, false, "A"), Is.Null);
            Assert.That(TextWidths.Points("Arial", 10, false, false, "\u4E00"), Is.Null);
        });
    }

    [Test]
    public void ALetterheadNameThatFitsInItsFont_PrintsInItsOwnSize()
    {
        // A name in 11pt bold capitals that fits its box in Arial's widths, wider than the
        // box by the generous estimate used for a font without them.
        var letterhead = new TextObject
        {
            Name = "Letterhead", Text = "MUNICIPAL OFFICE\n12 Main Street", Bounds = new(0, 0, 2400, 720),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("MUNICIPAL OFFICE", "Arial", 11, true, false, false),
                    new TextRun("\n12 Main Street", "Arial", 10, false, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 720, Objects = [letterhead] }],
        };

        var rdl = Converted(report);

        Assert.Multiple(() =>
        {
            Assert.That(Textbox(rdl, "Letterhead").Element(Rdl + "Value")!.Value, Is.EqualTo("MUNICIPAL OFFICE"));
            Assert.That(Textbox(rdl, "Letterhead_Line1").Element(Rdl + "Value")!.Value, Is.EqualTo("12 Main Street"));
        });
    }

    [Test]
    public void TextInTwoFontsOnOneLine_StaysOneTextbox()
    {
        var text = new TextObject
        {
            Name = "Mixed", Text = "Total due now", Bounds = new(0, 0, 7200, 240),
            Format = new ObjectFormat { FontName = "Arial", FontSize = 10, IsTextObject = true },
            Runs = [new TextRun("Total ", "Arial", 10, false, false, false),
                    new TextRun("due now", "Arial", 12, true, false, false)],
        };
        var report = new ReportDefinition
        {
            Sections = [new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [text] }],
        };

        var rdl = Converted(report);

        Assert.That(rdl.Descendants(Rdl + "Textbox").Count(t => ((string?)t.Attribute("Name"))?.StartsWith("Mixed") == true),
            Is.EqualTo(1));
    }

    [Test]
    public void AFormulaReadingTheSecondTablesColumn_ReadsThatTablesColumn()
    {
        // A property table and an owner table both have Address1; the owner's address formula
        // printed the property's.
        var report = new ReportDefinition
        {
            ConvertDatabaseNulls = true,
            Fields =
            [
                new DatabaseField { Name = "ADDRESS1", ColumnName = "ADDRESS1", TableName = "Property", DataType = "String" },
                new DatabaseField { Name = "Address1", ColumnName = "Address1", TableName = "Owner", DataType = "String" },
                new FormulaField { Name = "OwnerAddress", FormulaText = "Trim({Owner.Address1})", ResultType = "String" },
            ],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "Legal", FieldName = "ADDRESS1", Bounds = new(0, 0, 2880, 240) },
                    new FieldObject { Name = "Owner", FieldName = "@OwnerAddress", Bounds = new(2880, 0, 2880, 240) }] },
            ],
        };

        var rdl = Converted(report);
        var fields = rdl.Descendants(Rdl + "Field").ToList();
        string? formula = (string?)fields.Single(f => (string?)f.Attribute("Name") == "OwnerAddress").Element(Rdl + "Value");

        Assert.Multiple(() =>
        {
            Assert.That(formula, Does.Contain("Fields!Owner_Address1.Value").And.Not.Contain("Fields!Address1.Value"));
            Assert.That(rdl.Descendants(Rdl + "DataField").Select(d => d.Value),
                Is.SupersetOf(new[] { "Property.ADDRESS1", "Owner.Address1" }));
        });
    }

    [Test]
    public void AReportHeaderSuppressedOutright_TakesNoRoomFromWhatFollows()
    {
        // Written as a hidden table, its height was closed up and the rows below rose into
        // the blank page header above them.
        var report = new ReportDefinition
        {
            Fields = [new DatabaseField { Name = "Code", ColumnName = "Code", TableName = "Loans", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.PageHeader, HeightTwips = 600 },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 240, Objects = [
                    new TextObject { Name = "Heading", Text = "Loan", Bounds = new(0, 0, 1440, 240) }] },
                new Section { Type = SectionType.ReportHeader, HeightTwips = 440, Suppress = true, Objects = [
                    new FieldObject { Name = "HiddenCode", FieldName = "Code", Bounds = new(0, 220, 1440, 220) }] },
                new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                    new FieldObject { Name = "CodeField", FieldName = "Code", Bounds = new(0, 0, 1440, 240) }] },
            ],
        };

        var rdl = Converted(report);

        Assert.Multiple(() =>
        {
            Assert.That(rdl.Descendants(Rdl + "Table").Select(t => (string?)t.Attribute("Name")),
                Does.Not.Contain("TableReportHeader"));
            Assert.That(rdl.Descendants(Rdl + "Textbox").Select(t => (string?)t.Attribute("Name")),
                Does.Not.Contain("HiddenCode"));
        });
    }

    // A letter that reads no table: a report header, a blank page header, the letter in the
    // Details and the signature in the report footer.
    private static ReportDefinition Letter(string signature) => new()
    {
        Fields = [new ParameterField { Name = "Number", DataType = "String" }],
        Sections =
        [
            new Section { Type = SectionType.ReportHeader, HeightTwips = 600 },
            new Section { Type = SectionType.PageHeader, HeightTwips = 600 },
            new Section { Type = SectionType.Details, HeightTwips = 2000, Objects = [
                new TextObject { Name = "Body", Text = "Please note.", Bounds = new(0, 0, 7200, 1900) }] },
            new Section { Type = SectionType.ReportFooter, HeightTwips = 1000, Objects = [
                new TextObject { Name = "Signature", Text = signature, Bounds = new(4320, 240, 2880, 240) }] },
        ],
    };

    [TestCase("Yours truly,")]
    [TestCase("Receipt {?Number}")]
    public void TheFooterOfAReportThatReadsNoTable_PrintsBelowItsDetails(string signature)
    {
        // It was set under the report header, over the letter.
        var rdl = Converted(Letter(signature));
        var footer = Textbox(rdl, "Signature");
        double top = Points(footer, "Top") + footer.Ancestors().Where(a => a.Name == Rdl + "Table" || a.Name == Rdl + "Rectangle")
            .Sum(a => a.Element(Rdl + "Top") is { } t ? Points(a, "Top") : 0);

        Assert.That(top, Is.EqualTo((600 + 600 + 2000 + 240) / 20.0).Within(0.01));
    }
}
