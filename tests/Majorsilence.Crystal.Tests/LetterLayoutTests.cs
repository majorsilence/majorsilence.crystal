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
