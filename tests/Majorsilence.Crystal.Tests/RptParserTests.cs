using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Decryption;
using Majorsilence.Crystal.Parser.OleStorage;
using Majorsilence.Crystal.Parser.Sections;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

[TestFixture]
public class RptParserTests
{
    private static readonly string SampleReport1 =
        Path.GetFullPath("../../../../../../CrystalCmd/thereport.rpt",
            AppContext.BaseDirectory);

    private static readonly string SampleReport2 =
        Path.GetFullPath("../../../../../../CrystalCmd/the_java_dataset_report.rpt",
            AppContext.BaseDirectory);

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void OleReader_Opens_ValidRptFile(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True, $"Sample file not found: {path}");

        using var reader = OleReader.Open(path);
        Assert.That(reader.HasStream("Contents"), Is.True);
    }

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void OleReader_ReadsContentsStream(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True);

        using var reader = OleReader.Open(path);
        byte[] contents = reader.ReadStream("Contents");
        Assert.That(contents.Length, Is.GreaterThan(0));
    }

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void ContentDecryptor_DetectsEncryption(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True);

        using var reader = OleReader.Open(path);
        byte[] contents = reader.ReadStream("Contents");
        bool encrypted = ContentDecryptor.IsEncrypted(contents);

        Assert.That(encrypted, Is.True, "Expected test RPT files to have encrypted Contents.");
    }

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void ReportInfoParser_ParsesBothFiles(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True);

        using var reader = OleReader.Open(path);
        byte[] riData = reader.ReadStream("ReportInfo");
        var parser = new ReportInfoParser();
        var record = parser.Parse(riData);

        Assert.That(record, Is.Not.Null);
        Console.WriteLine($"ReportInfo — FormatVersion: {record.FormatVersion}, Flag04F0: {record.Flag04F0}");
        Console.WriteLine($"Unknown tags: {string.Join(", ", record.UnknownTags.Keys.Select(k => $"0x{k:X4}"))}");
    }

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void QeSessionParser_ParsesBothFiles(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True);

        using var reader = OleReader.Open(path);
        byte[] qeData = reader.ReadStream("QESession");
        var parser = new QeSessionParser();
        var record = parser.Parse(qeData);

        Assert.That(record.IsValid, Is.True);
        Console.WriteLine($"QENG version: {record.Version}, flags: 0x{record.Flags:X8}");
        Console.WriteLine($"Payload size: {record.PayloadLength} bytes");
        if (record.ExtractedStrings.Count > 0)
        {
            Console.WriteLine("Extracted strings:");
            foreach (var s in record.ExtractedStrings.Take(20))
                Console.WriteLine($"  \"{s}\"");
        }
    }

    [Test]
    [TestCase(nameof(SampleReport1))]
    [TestCase(nameof(SampleReport2))]
    public void RptParser_ParsesWithoutThrowing(string reportKey)
    {
        string path = reportKey == nameof(SampleReport1) ? SampleReport1 : SampleReport2;
        Assume.That(File.Exists(path), Is.True);

        var result = RptParser.Parse(path);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Console.WriteLine($"CR version: {result.Report!.CrVersion}");
        Console.WriteLine($"Warnings ({result.Warnings.Count}):");
        foreach (var w in result.Warnings)
            Console.WriteLine($"  {w}");
        Console.WriteLine($"Raw chunks: {result.RawChunks.Count}");
        var sections = result.Report!.Sections;
        Console.WriteLine($"Sections ({sections.Count}):");
        foreach (var s in sections)
            Console.WriteLine($"  {s.Type}  height={s.HeightTwips}twips  objects={s.Objects.Count}");
        // Dump tag distribution for diagnostics
        var tagGroups = result.RawChunks.GroupBy(r => r.Tag).OrderBy(g => g.Key);
        Console.WriteLine($"Tag distribution: {string.Join(", ", tagGroups.Select(g => $"{g.Key}×{g.Count()}"))}");
    }

    // ---------------------------------------------------------------------------
    // Corpus test: runs against every .rpt file in tests/rpt-corpus/ (if present).
    // Populate the corpus by running: scripts/download-test-rpts.sh --download-only
    // ---------------------------------------------------------------------------

    private static readonly string CorpusDir =
        Path.GetFullPath("../../../../rpt-corpus", AppContext.BaseDirectory);

    private static IEnumerable<TestCaseData> CorpusFiles()
    {
        if (!Directory.Exists(CorpusDir))
            yield break;

        foreach (var f in Directory.EnumerateFiles(CorpusDir, "*.rpt", SearchOption.TopDirectoryOnly))
            yield return new TestCaseData(f).SetName(Path.GetFileName(f));
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_ParsesWithoutThrowing(string rptPath)
    {
        var result = RptParser.Parse(rptPath);

        Console.WriteLine($"  Success={result.Success}  sections={result.Report?.Sections.Count ?? 0}  chunks={result.RawChunks.Count}");
        foreach (var s in result.Report?.Sections ?? [])
            Console.WriteLine($"    {s.Type}  height={s.HeightTwips}  objects={s.Objects.Count}");
        foreach (var w in result.Warnings)
            Console.WriteLine($"    [WARN] {w}");

        Assert.That(result.Success, Is.True, $"Parse failed for {Path.GetFileName(rptPath)}");
        Assert.That(result.Report!.Sections, Is.Not.Empty, "Expected at least one section");
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_ExtractsDbFields(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        var dbFields = result.Report!.Fields.OfType<DatabaseField>().ToList();
        Console.WriteLine($"  DB fields ({dbFields.Count}):");
        foreach (var f in dbFields)
            Console.WriteLine($"    ColumnName='{f.ColumnName}' DataType={f.DataType}");

        // Every DB field should have a non-empty name and a known data type
        foreach (var f in dbFields)
        {
            Assert.That(f.ColumnName, Is.Not.Empty, "DB field should have a non-empty ColumnName");
            Assert.That(f.Name, Is.EqualTo(f.ColumnName), "Name and ColumnName should match for DB fields");
        }

        Console.WriteLine($"  RecordSelectionFormula: '{result.Report.RecordSelectionFormula}'");
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_FieldObjectsHaveNames(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        var detailSections = result.Report!.Sections
            .Where(s => s.Type == Majorsilence.Crystal.Model.SectionType.Details)
            .ToList();

        foreach (var sec in detailSections)
        {
            foreach (var obj in sec.Objects.OfType<Majorsilence.Crystal.Model.Objects.FieldObject>())
            {
                Console.WriteLine($"  FieldObject.FieldName='{obj.FieldName}' bounds={obj.Bounds}");
                Assert.That(obj.FieldName, Is.Not.Null, "FieldObject.FieldName should not be null");
            }
        }
    }

    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_TextObjectsHaveContent(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        var textObjects = result.Report!.Sections
            .SelectMany(s => s.Objects.OfType<Majorsilence.Crystal.Model.Objects.TextObject>())
            .ToList();

        // If the report has TextObjects, at least one should have non-trivial content
        // (not just a fallback internal name like "Text1")
        if (textObjects.Count > 0)
        {
            Console.WriteLine($"  TextObjects: {textObjects.Count}");
            foreach (var t in textObjects)
                Console.WriteLine($"    '{t.Text}' color={t.Format.ForeColor ?? "default"}");

            bool anyReal = textObjects.Any(t => t.Text.Length > 0 && !t.Text.StartsWith("Text", StringComparison.OrdinalIgnoreCase));
            Assert.That(anyReal, Is.True, "At least one TextObject should have real content (not internal name fallback)");
        }
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_FieldObjectsNoInternalNames(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        var allFields = result.Report!.Sections
            .SelectMany(s => s.Objects.OfType<Majorsilence.Crystal.Model.Objects.FieldObject>())
            .ToList();

        Console.WriteLine($"  FieldObjects ({allFields.Count}):");
        foreach (var f in allFields)
            Console.WriteLine($"    '{f.FieldName}' align={f.Format.HAlign}");

        // No FieldObject should have a Crystal internal name like "Field1", "Field2" etc.
        foreach (var f in allFields)
        {
            bool isInternalName = System.Text.RegularExpressions.Regex.IsMatch(f.FieldName, @"^Field\d+$");
            Assert.That(isInternalName, Is.False, $"FieldObject '{f.FieldName}' still uses Crystal internal name");
        }
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_GroupsExtracted(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        Console.WriteLine($"  Groups ({result.Report!.Groups.Count}):");
        foreach (var g in result.Report.Groups)
            Console.WriteLine($"    Level={g.Level} FieldName='{g.FieldName}' Sort={g.SortOrder}");

        // Groups may be empty for some files; when present, each must have a non-empty field name
        foreach (var g in result.Report.Groups)
            Assert.That(g.FieldName, Is.Not.Empty, "Group FieldName should not be empty");
    }

    [Test]
    [TestCaseSource(nameof(CorpusFiles))]
    public void RptParser_CorpusFile_ReportTitleExtracted(string rptPath)
    {
        var result = RptParser.Parse(rptPath);
        Assume.That(result.Success, Is.True);

        Console.WriteLine($"  ReportTitle='{result.Report!.ReportTitle}' Author='{result.Report.Author}'");

        // Title may legitimately be empty if the RPT author didn't fill in summary properties.
        // Assert only that it doesn't throw and is a valid (possibly empty) string.
        Assert.That(result.Report.ReportTitle, Is.Not.Null, "ReportTitle should not be null");
    }

    private static readonly string RunningTotalCorpusFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__ChinaOrders-RunningTotals.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_RunningTotalField_ParsesSummarizedFieldAndFunction()
    {
        Assume.That(File.Exists(RunningTotalCorpusFile), Is.True,
            "Running-total corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(RunningTotalCorpusFile);
        Assert.That(result.Success, Is.True);

        var rt = result.Report!.Fields.OfType<RunningTotalField>().FirstOrDefault();
        Assert.That(rt, Is.Not.Null, "Expected at least one RunningTotalField");
        Assert.That(rt!.SummarizedFieldName, Is.EqualTo("Order Amount"),
            "SummarizedFieldName should be the column name (table prefix stripped)");
        Assert.That(rt.Function, Is.EqualTo(AggregateFunction.Sum),
            "Function should be Sum (Crystal binary code 1)");
    }

    // ---------------------------------------------------------------------------
    // Chart category (tag 289)
    // ---------------------------------------------------------------------------

    private static readonly string ChartNoCategoryFile = Path.GetFullPath(
        "../../../../rpt-corpus-external/parking__chart_grantt.rpt", AppContext.BaseDirectory);

    private static readonly string ChartWithCategoryFile = Path.GetFullPath(
        "../../../../rpt-corpus/benbrahim777__Top5USA-piechart.rpt", AppContext.BaseDirectory);

    // A chart's definition record ends with its font block - eight or more names like
    // "Arial" or "MS Shell Dlg" - and ScanStrings brute-forces every string out of it. A
    // chart with no category string of its own leaves those fonts as the only strings after
    // the title, so the second one was taken as the category field: the converter emitted
    // Fields!MS_Shell_Dlg.Value and the engine lost the whole report with "Field
    // 'MS_Shell_Dlg' not found". A category must be a field the report actually has.
    //
    // This file is in the opt-in external corpus, so the case skips unless it was fetched
    // (scripts/download-test-rpts.sh --with-rpt-rs).
    [Test]
    public void RptParser_ChartWithNoCategoryOfItsOwn_DoesNotTakeAFontName()
    {
        Assume.That(File.Exists(ChartNoCategoryFile), Is.True,
            "external corpus not present — run scripts/download-test-rpts.sh --with-rpt-rs");

        var result = RptParser.Parse(ChartNoCategoryFile);
        Assert.That(result.Success, Is.True);

        var charts = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.ChartObject>()
            .ToList();

        // With no usable category the parser drops the chart rather than inventing one, so
        // there is nothing here at all — and crucially no category named after a typeface.
        Assert.That(charts.SelectMany(c => c.CategoryFields),
            Has.None.Matches<string>(f => f.Contains("Shell") || f == "Arial"),
            "a typeface is not a category field");
    }

    // The other side: a chart that does carry a real category must still get it.
    [Test]
    public void RptParser_ChartWithARealCategory_KeepsIt()
    {
        Assume.That(File.Exists(ChartWithCategoryFile), Is.True,
            "corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(ChartWithCategoryFile);
        var chart = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.ChartObject>()
            .FirstOrDefault();

        Assert.That(chart, Is.Not.Null, "this report has a pie chart");
        Assert.That(chart!.CategoryFields, Does.Contain("Customer Name"));
    }

    // ---------------------------------------------------------------------------
    // Numeric format (tag 249 -> 248)
    // ---------------------------------------------------------------------------

    private static readonly string PlainNumberFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Country-Region-Sort.rpt",
            AppContext.BaseDirectory);

    // An object carries two numeric-format records and the SECOND is effective. This is the
    // case that proves it: Customer ID's first record carries a "$" and its second does not,
    // and the real engine renders a bare "158". Taking the first would put a currency symbol
    // on every plain number in the corpus.
    [Test]
    public void RptParser_PlainNumericField_TakesTheSecondRecordAndGetsNoCurrency()
    {
        Assume.That(File.Exists(PlainNumberFile), Is.True,
            "Corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(PlainNumberFile);
        Assert.That(result.Success, Is.True);

        var id = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.FieldObject>()
            .First(f => f.FieldName == "Customer ID");

        Assert.That(id.Format?.FormatString, Is.EqualTo("#,##0"),
            "no currency symbol and no decimals, which is what the second record says");
    }

    // A string field carries the numeric record too, holding whatever the object was last
    // defaulted to - Customer Name's says two decimals. Formatting a string as a number
    // would corrupt it, so the field's declared type decides, not the record's presence.
    [Test]
    public void RptParser_StringField_GetsNoNumericFormat()
    {
        Assume.That(File.Exists(PlainNumberFile), Is.True,
            "Corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(PlainNumberFile);
        var name = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.FieldObject>()
            .First(f => f.FieldName == "Customer Name");

        Assert.That(name.Format?.FormatString, Is.Null);
    }

    // ---------------------------------------------------------------------------
    // Object borders (tag 237 -> 236)
    // ---------------------------------------------------------------------------

    private static readonly string BorderedReportFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__SalesByCustomer-Grouped.rpt",
            AppContext.BaseDirectory);

    // What this report draws as a box around its title, a rule under each column label and
    // a frame around its subtotal are border formatting on the objects, not line objects.
    // The real engine's render shows exactly these four: title boxed (with a drop shadow),
    // two labels with only a bottom rule, subtotal boxed.
    [Test]
    public void RptParser_ObjectBorders_AreReadFromTheBorderRecord()
    {
        Assume.That(File.Exists(BorderedReportFile), Is.True,
            "Corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(BorderedReportFile);
        Assert.That(result.Success, Is.True);

        var all = result.Report!.Sections.SelectMany(s => s.Objects).ToList();

        var boxed = all.Where(o => o.Format is
            { BorderLeft: not 0, BorderRight: not 0, BorderTop: not 0, BorderBottom: not 0 }).ToList();
        Assert.That(boxed, Has.Count.EqualTo(2), "the title and the subtotal are boxed");
        Assert.That(boxed.Count(o => o.Format!.DropShadow), Is.EqualTo(1),
            "and only the title carries the drop shadow");

        var underlined = all.Where(o => o.Format is
            { BorderLeft: 0, BorderRight: 0, BorderTop: 0, BorderBottom: not 0 }).ToList();
        Assert.That(underlined, Has.Count.EqualTo(2),
            "the two column labels have only the rule beneath them");
        Assert.That(underlined, Has.All.Matches<Majorsilence.Crystal.Model.Objects.ReportObject>(
            o => o is Majorsilence.Crystal.Model.Objects.TextObject));
    }

    // ---------------------------------------------------------------------------
    // Font style flags
    // ---------------------------------------------------------------------------

    private static readonly string UnderlinedHeadingsFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Country-Region-Sort.rpt",
            AppContext.BaseDirectory);

    private static readonly string ItalicTitleFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__SalesByCustomer-Grouped.rpt",
            AppContext.BaseDirectory);

    // The font record's style field was read as 0x02 = italic and 0x04 = underline. Neither
    // bit is set anywhere in either corpus, so both attributes were dead and no report ever
    // emitted an italic or an underline. The bits the files actually use are 0x1 and
    // 0x10000; this report's four column headings are underlined in the real engine's
    // output and are exactly the four objects carrying the first.
    [Test]
    public void RptParser_UnderlinedColumnHeadings_AreParsedAsUnderlined()
    {
        Assume.That(File.Exists(UnderlinedHeadingsFile), Is.True,
            "Corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(UnderlinedHeadingsFile);
        Assert.That(result.Success, Is.True);

        var underlined = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .Where(o => o.Format?.Underline == true)
            .ToList();

        Assert.That(underlined, Has.Count.EqualTo(4),
            "the four column headings, and nothing else, are underlined");
        Assert.That(result.Report.Sections.SelectMany(s => s.Objects).Any(o => o.Format?.Italic == true),
            Is.False, "and none of them is italic");
    }

    [Test]
    public void RptParser_ItalicTitle_IsParsedAsItalic()
    {
        Assume.That(File.Exists(ItalicTitleFile), Is.True,
            "Corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(ItalicTitleFile);
        Assert.That(result.Success, Is.True);

        var italics = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .Where(o => o.Format?.Italic == true)
            .ToList();

        Assert.That(italics, Has.Count.EqualTo(1), "this report's one italic object is its title");
        // Its rules are drawn objects rather than an underlined font, so nothing here is
        // underlined - which is what separates the two bits rather than conflating them.
        Assert.That(result.Report.Sections.SelectMany(s => s.Objects).Any(o => o.Format?.Underline == true),
            Is.False);
    }

    // ---------------------------------------------------------------------------
    // Parameter field tests
    // ---------------------------------------------------------------------------

    private static readonly string LanguagePickListFile =
        Path.GetFullPath("../../../../rpt-corpus/boyum__SolutionKnowledgeBase_HANA.rpt",
            AppContext.BaseDirectory);

    private static readonly string AccountBalanceFile =
        Path.GetFullPath("../../../../rpt-corpus/boyum__AccountBalance_HANA.rpt",
            AppContext.BaseDirectory);

    private static readonly string PaymentsFile =
        Path.GetFullPath("../../../../rpt-corpus/boyum__Payments.rpt",
            AppContext.BaseDirectory);

    private static readonly string Orders5150File =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Orders5-150.rpt",
            AppContext.BaseDirectory);

    private static readonly string JournalEntryFile =
        Path.GetFullPath("../../../../rpt-corpus/boyum__JournalEntry.rpt",
            AppContext.BaseDirectory);

    // Crystal's Format Editor formulas ("Display String", a font size, a page break)
    // arrive through the same record tag as user formulas but are not fields - they
    // format an object rather than producing a row value, and a report carries one copy
    // per formatted object. Emitted as DataSet fields they collide, and the engine drops
    // all but the first, which is silent data loss rather than a cosmetic problem.
    [Test]
    public void RptParser_ObjectFormatHooks_AreNotFields()
    {
        Assume.That(File.Exists(JournalEntryFile), Is.True,
            "boyum JournalEntry corpus file not found - run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(JournalEntryFile);
        Assert.That(result.Success, Is.True);

        var formulaNames = result.Report!.Fields.OfType<FormulaField>()
            .Select(f => f.Name).ToList();

        Assert.That(formulaNames, Does.Not.Contain("Display_String"),
            "a format hook must not be surfaced as a formula field");
        // The file really does carry the hook, so the assertion above is not vacuous:
        // it appears more than once, which is only possible for a per-object formula
        // since Crystal requires user formula names to be unique within a report.
        Assert.That(result.Report.Fields.OfType<FormulaField>().Count(), Is.GreaterThan(0),
            "the report should still expose its genuine user formulas");
    }

    [Test]
    public void RptParser_ParameterField_LargePickList_ParsedCorrectly()
    {
        Assume.That(File.Exists(LanguagePickListFile), Is.True,
            "Boyum SolutionKnowledgeBase_HANA corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(LanguagePickListFile);
        Assert.That(result.Success, Is.True);

        var lang = result.Report!.Fields.OfType<ParameterField>()
            .FirstOrDefault(p => p.Name == "$[CURRENT_LANGUAGE]");
        Assert.That(lang, Is.Not.Null, "Expected $[CURRENT_LANGUAGE] parameter");
        Assert.That(lang!.DataType, Is.EqualTo("String"));
        Assert.That(lang.PromptText, Is.EqualTo("Enter Language"));
        Assert.That(lang.PickListValues.Count, Is.GreaterThanOrEqualTo(10),
            "Language pick-list should have at least 10 entries");
        Assert.That(lang.PickListValues.Select(p => p.Value), Does.Contain("English"),
            "Pick-list should include 'English'");
        Assert.That(lang.PickListValues.Select(p => p.Label), Does.Contain("English"),
            "Pick-list label should include 'English'");
    }

    [Test]
    public void RptParser_ParameterField_AgeByPickList_ThreeCorrectValues()
    {
        Assume.That(File.Exists(AccountBalanceFile), Is.True,
            "Boyum AccountBalance_HANA corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(AccountBalanceFile);
        Assert.That(result.Success, Is.True);

        var ageBy = result.Report!.Fields.OfType<ParameterField>()
            .FirstOrDefault(p => p.Name == "$[BOY_AB_AGE_BY]");
        Assert.That(ageBy, Is.Not.Null, "Expected $[BOY_AB_AGE_BY] parameter");
        Assert.That(ageBy!.DataType, Is.EqualTo("String"));
        Assert.That(ageBy.PickListValues.Count, Is.EqualTo(3),
            "AgeBy should have exactly 3 pick-list values");

        var values = ageBy.PickListValues.Select(p => p.Value).ToList();
        Assert.That(values, Does.Contain("Document Date"));
        Assert.That(values, Does.Contain("Due Date"));
        Assert.That(values, Does.Contain("Posting Date"));
    }

    [Test]
    public void RptParser_ParameterField_IncludeRT_YesNoPickList()
    {
        Assume.That(File.Exists(AccountBalanceFile), Is.True,
            "Boyum AccountBalance_HANA corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(AccountBalanceFile);
        Assert.That(result.Success, Is.True);

        var includeRt = result.Report!.Fields.OfType<ParameterField>()
            .FirstOrDefault(p => p.Name == "$[BOY_AB_INCLUDE_RT]");
        Assert.That(includeRt, Is.Not.Null, "Expected $[BOY_AB_INCLUDE_RT] parameter");
        Assert.That(includeRt!.PickListValues.Count, Is.EqualTo(2));
        var values = includeRt.PickListValues.Select(p => p.Value).ToList();
        Assert.That(values, Does.Contain("No"));
        Assert.That(values, Does.Contain("Yes"));
    }

    [Test]
    public void RptParser_ParameterField_ObjectIdPickList_NoParamNameLeak()
    {
        Assume.That(File.Exists(PaymentsFile), Is.True,
            "Boyum Payments corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(PaymentsFile);
        Assert.That(result.Success, Is.True);

        var objId = result.Report!.Fields.OfType<ParameterField>()
            .FirstOrDefault(p => p.Name == "ObjectId@");
        Assert.That(objId, Is.Not.Null, "Expected ObjectId@ parameter");
        Assert.That(objId!.PickListValues.Count, Is.EqualTo(2));

        var values = objId.PickListValues.Select(p => p.Value).ToList();
        Assert.That(values, Does.Contain("Incoming Payment"));
        Assert.That(values, Does.Contain("Outgoing Payment"));
        Assert.That(values, Does.Not.Contain("ObjectId"),
            "Bare parameter name should not leak into pick-list");
    }

    [Test]
    public void RptParser_ParameterField_RangeParameter_NoFalsePickList()
    {
        Assume.That(File.Exists(Orders5150File), Is.True,
            "benbrahim777 Orders5-150 corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(Orders5150File);
        Assert.That(result.Success, Is.True);

        var rangeParam = result.Report!.Fields.OfType<ParameterField>()
            .FirstOrDefault(p => p.Name == "Order_Amt_Range");
        Assert.That(rangeParam, Is.Not.Null, "Expected Order_Amt_Range parameter");
        Assert.That(rangeParam!.DataType, Is.EqualTo("Float64"));
        Assert.That(rangeParam.PickListValues.Count, Is.EqualTo(0),
            "Range parameters must not produce a false pick-list from field references");
    }

    // ---------------------------------------------------------------------------
    // Image object tests
    // ---------------------------------------------------------------------------

    private static readonly string CustomerListFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__CustomerList.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_EmbeddedPicture_ResolvedFromOleEmbeddingStorage()
    {
        Assume.That(File.Exists(CustomerListFile), Is.True,
            "CustomerList corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CustomerListFile);
        Assert.That(result.Success, Is.True);

        // tag-175 picture object whose tag-189 record points at "Embedding 1"
        var img = result.Report!.Sections.SelectMany(s => s.Objects)
            .OfType<ImageObject>()
            .FirstOrDefault(i => i.Source == ImageSourceKind.Embedded);
        Assert.That(img, Is.Not.Null, "Expected an embedded ImageObject");
        Assert.That(img!.EmbeddingIndex, Is.EqualTo(1));
        Assert.That(img.ImageData, Is.Not.Null, "Image bytes should resolve from Embedding 1/CONTENTS");
        Assert.That(img.MimeType, Is.EqualTo("image/bmp"));
        Assert.That(img.ImageData!.Length, Is.GreaterThan(1000));
    }

    // ---------------------------------------------------------------------------
    // Page setup tests
    // ---------------------------------------------------------------------------

    // Page size was assumed to be US Letter portrait for every report. It is recorded,
    // as width then height in twips with orientation already applied, so a landscape
    // report stores the wider value first and needs no separate flag.
    //
    // CustomerList is landscape Letter, which the real engine confirms: it renders the
    // report into a 792x612pt page.
    [Test]
    public void RptParser_PageSetup_ReadsALandscapePage()
    {
        Assume.That(File.Exists(CustomerListFile), Is.True,
            "CustomerList corpus file not found — run scripts/download-test-rpts.sh");

        var page = RptParser.Parse(CustomerListFile).Report!.Page;

        Assert.That(page.WidthTwips, Is.EqualTo(15840), "11in");
        Assert.That(page.HeightTwips, Is.EqualTo(12240), "8.5in");
        Assert.That(page.Orientation, Is.EqualTo(PageOrientation.Landscape));
    }

    // The same record on a portrait report, so the test above cannot pass by hardcoding
    // landscape the way the parser previously hardcoded portrait.
    [Test]
    public void RptParser_PageSetup_ReadsAPortraitPage()
    {
        Assume.That(File.Exists(GroupedSalesFile), Is.True,
            "SalesByCustomer-Grouped corpus file not found — run scripts/download-test-rpts.sh");

        var page = RptParser.Parse(GroupedSalesFile).Report!.Page;

        Assert.That(page.WidthTwips, Is.EqualTo(12240), "8.5in");
        Assert.That(page.HeightTwips, Is.EqualTo(15840), "11in");
        Assert.That(page.Orientation, Is.EqualTo(PageOrientation.Portrait));
    }

    // The margin default, pinned because it is a measured choice rather than a
    // convention: the page-setup record's margin block is identical in every corpus
    // file, a "use the printer's defaults" sentinel, and a sixth of an inch is the inset
    // the real engine renders these reports into. Half an inch was assumed before, and it
    // narrowed the body until content Crystal fits on the page no longer fitted on ours.
    [Test]
    public void RptParser_PageMargins_AreASixthOfAnInch()
    {
        Assume.That(File.Exists(CustomerListFile), Is.True,
            "CustomerList corpus file not found — run scripts/download-test-rpts.sh");

        var page = RptParser.Parse(CustomerListFile).Report!.Page;

        Assert.That(page.LeftMarginTwips, Is.EqualTo(240));
        Assert.That(page.RightMarginTwips, Is.EqualTo(240));
        Assert.That(page.TopMarginTwips, Is.EqualTo(240));
        Assert.That(page.BottomMarginTwips, Is.EqualTo(240));

        // The report's own columns reach 15000 twips; a body narrower than that clips it.
        Assert.That(page.WidthTwips - page.LeftMarginTwips - page.RightMarginTwips,
            Is.GreaterThanOrEqualTo(15000),
            "the body must be wide enough for the columns the report places in it");
    }

    // Neither Letter nor portrait: A4, to a twip. Page size is read rather than
    // recognised, so a size that is not in any table of paper names still comes through -
    // the corpus includes label stock an inch square.
    [Test]
    public void RptParser_PageSetup_ReadsAnA4Page()
    {
        string a4 = Path.GetFullPath("../../../../rpt-corpus/boyum__ServiceCall.rpt",
            AppContext.BaseDirectory);
        Assume.That(File.Exists(a4), Is.True,
            "ServiceCall corpus file not found — run scripts/download-test-rpts.sh");

        var page = RptParser.Parse(a4).Report!.Page;

        // 210mm x 297mm is 11906 x 16838 twips; the file rounds to 11899 x 16841.
        Assert.That(page.WidthTwips, Is.EqualTo(11899));
        Assert.That(page.HeightTwips, Is.EqualTo(16841));
        Assert.That(page.Orientation, Is.EqualTo(PageOrientation.Portrait));
    }

    // ---------------------------------------------------------------------------
    // Object placement tests
    // ---------------------------------------------------------------------------

    // Position is not in the object's own size record - both of that record's spare
    // slots are zero for every object in every corpus file - but in the tag-190 that
    // follows the object wrapper.
    //
    // The expected lefts are the ones the real Crystal engine renders this report at.
    // Its PDF puts the six page-header column labels at 84, 222, 348, 474 and 612
    // points from the page edge, over a body inset 12 points from it, and the first
    // column's underline ends exactly at the right edge of a 1123-twip object starting
    // at 120 - a right-aligned numeric column, which is what it is.
    [Test]
    public void RptParser_ObjectPlacement_MatchesTheRenderedColumnPositions()
    {
        Assume.That(File.Exists(CustomerListFile), Is.True,
            "CustomerList corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CustomerListFile);
        Assert.That(result.Success, Is.True);

        int[] expectedLefts = [120, 1440, 4200, 6720, 9240, 12000];

        var header = result.Report!.Sections.First(s => s.Type == SectionType.PageHeader);
        Assert.That(header.Objects.Select(o => o.Bounds.Left), Is.EqualTo(expectedLefts));
        Assert.That(header.Objects.Select(o => o.Bounds.Top), Is.EqualTo(Enumerable.Repeat(495, 6)),
            "the column labels sit on one line, below the header's own title row");

        // The detail fields sit under their labels, at the top of their own band.
        var detail = result.Report.Sections.First(s => s.Type == SectionType.Details);
        Assert.That(detail.Objects.Select(o => o.Bounds.Left), Is.EqualTo(expectedLefts));
        Assert.That(detail.Objects.Select(o => o.Bounds.Top), Is.EqualTo(Enumerable.Repeat(0, 6)));
    }

    private static readonly string CustomFunctionsFile =
        Path.GetFullPath("../../../../rpt-corpus/souvikduttachoudhury__CustomFunctions.rpt",
            AppContext.BaseDirectory);

    // Crystal records a date field's order and separator, and this report asks for
    // month-day-year with slashes: it renders 05/26/2001 where an unformatted render gives
    // 2001-05-26. The separator is quoted because .NET reads a bare "/" as "this machine's
    // date separator", which is not what the file means.
    [Test]
    public void RptParser_DateField_TakesTheOrderAndSeparatorTheFileRecords()
    {
        Assume.That(File.Exists(GroupedSalesFile), Is.True,
            "SalesByCustomer-Grouped corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(GroupedSalesFile);
        Assert.That(result.Success, Is.True);

        var fields = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.FieldObject>()
            .ToList();

        var date = fields.First(f => f.FieldName == "Order Date");
        Assert.That(date.Format?.FormatString, Is.EqualTo("MM'/'dd'/'yyyy"));

        // The same record sits on every field object, holding whatever that object was
        // last defaulted to, so it must not reach a field that is not a date. This field
        // gets the *numeric* record's format instead, which is the point: the two must not
        // be confused for one another.
        var amount = fields.First(f => f.FieldName == "Order Amount");
        Assert.That(amount.Format?.FormatString, Is.EqualTo("\"$\"#,##0.00"),
            "a number takes its own format, never the date-format record every object carries");
    }

    // Order 1 is not a format: it means "use whatever short date the machine has", which is
    // what the renderer does on its own. Emitting one would bake this machine's locale in.
    [Test]
    public void RptParser_DateFieldDeferringToTheMachine_GetsNoFormatAtAll()
    {
        Assume.That(File.Exists(CustomFunctionsFile), Is.True,
            "CustomFunctions corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CustomFunctionsFile);
        Assert.That(result.Success, Is.True);

        var dates = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.FieldObject>()
            .Where(f => f.FieldName is "ORDER_DATE" or "REQUIRED_DATE")
            .ToList();

        Assert.That(dates, Is.Not.Empty, "the report's two date columns must be found");
        Assert.That(dates.All(d => d.Format?.FormatString is null), Is.True,
            "these render with the machine's own short date, so no format may be written");
    }

    // A text object's alignment lives on its paragraph record, not on the object. In this
    // report the object-level record reads "unset" for every one of them, so reading only
    // that record renders the whole page flush left - which is wrong for three of the nine.
    [Test]
    public void RptParser_TextObjectAlignment_ComesFromTheParagraphNotTheObject()
    {
        Assume.That(File.Exists(CustomerListFile), Is.True,
            "CustomerList corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CustomerListFile);
        Assert.That(result.Success, Is.True);

        var byText = result.Report!.Sections
            .SelectMany(s => s.Objects)
            .OfType<Majorsilence.Crystal.Model.Objects.TextObject>()
            .ToDictionary(o => o.Text, o => o.Format?.HAlign ?? HorizontalAlignment.Left);

        Assert.That(byText["Customer ID"], Is.EqualTo(HorizontalAlignment.Right),
            "the ID column's label is right-aligned over its right-aligned numbers");
        Assert.That(byText["Customer List"], Is.EqualTo(HorizontalAlignment.Center),
            "the report title is centred across the page, not flush left");
        Assert.That(byText["Customer Name"], Is.EqualTo(HorizontalAlignment.Left),
            "the other column labels really are left-aligned");
    }

    // ---------------------------------------------------------------------------
    // Summary field tests
    // ---------------------------------------------------------------------------

    private static readonly string GroupedSalesFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__SalesByCustomer-Grouped.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_SummaryFieldObject_ParsesAggregateFunctionFromReferencePrefix()
    {
        Assume.That(File.Exists(GroupedSalesFile), Is.True,
            "SalesByCustomer-Grouped corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(GroupedSalesFile);
        Assert.That(result.Success, Is.True);

        // The group footer places "Sum of Orders.Order Amount"
        var footer = result.Report!.Sections.First(s => s.Type == SectionType.GroupFooter);
        var summary = footer.Objects.OfType<FieldObject>()
            .FirstOrDefault(f => f.SummaryFunction is not null);
        Assert.That(summary, Is.Not.Null, "group footer should contain a summary FieldObject");
        Assert.That(summary!.SummaryFunction, Is.EqualTo(AggregateFunction.Sum));
        Assert.That(summary.FieldName, Is.EqualTo("Order Amount"),
            "the summarized column name should be extracted without the function prefix");

        // Plain detail fields must NOT get a summary function
        var detail = result.Report.Sections.First(s => s.Type == SectionType.Details);
        Assert.That(detail.Objects.OfType<FieldObject>()
            .All(f => f.SummaryFunction is null), Is.True);
    }

    private static readonly string CustomerProfileReportFile =
        Path.GetFullPath("../../../../rpt-corpus/souvikduttachoudhury__CustomerProfileReport.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_PercentageSummaryFieldObject_ParsesCompoundPrefix()
    {
        Assume.That(File.Exists(CustomerProfileReportFile), Is.True,
            "CustomerProfileReport corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CustomerProfileReportFile);
        Assert.That(result.Success, Is.True);

        // The raw reference reads "Percentage of Sum of ORDERS.ORDER_AMOUNT" — a compound
        // prefix wrapping an inner Sum summary. The inner function is discarded; only the
        // outer Percentage and the bare column name should survive.
        var percentageFields = result.Report!.Sections.SelectMany(s => s.Objects)
            .OfType<FieldObject>().Where(f => f.SummaryFunction == AggregateFunction.Percentage).ToList();
        Assert.That(percentageFields, Is.Not.Empty, "expected at least one Percentage summary field");
        Assert.That(percentageFields, Has.All.Matches<FieldObject>(f => f.FieldName == "ORDER_AMOUNT"));

        // The same underlying column also has a plain Sum summary elsewhere — confirming
        // the compound prefix didn't get merged/confused with the inner function.
        var sumFields = result.Report.Sections.SelectMany(s => s.Objects)
            .OfType<FieldObject>().Where(f => f.SummaryFunction == AggregateFunction.Sum && f.FieldName == "ORDER_AMOUNT");
        Assert.That(sumFields, Is.Not.Empty, "expected a plain Sum summary of the same column alongside the Percentage one");
    }

    // ---------------------------------------------------------------------------
    // Section suppress formula tests
    // ---------------------------------------------------------------------------

    private static readonly string UsaVsFranceFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__USAvsFrance.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_SectionSuppressFormula_ResolvedFromFormulaHookEntry()
    {
        Assume.That(File.Exists(UsaVsFranceFile), Is.True,
            "USAvsFrance corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(UsaVsFranceFile);
        Assert.That(result.Success, Is.True);

        // Entry 0 of the tag-255 formula hooks references '@Section_Visibility'
        var conditional = result.Report!.Sections
            .FirstOrDefault(s => s.SuppressFormula is not null);
        Assert.That(conditional, Is.Not.Null,
            "a section should carry the suppress formula text resolved by name");
        Assert.That(conditional!.SuppressFormula, Is.Not.Empty);
    }

    // ---------------------------------------------------------------------------
    // Line / Box object tests
    // ---------------------------------------------------------------------------

    private static readonly string DunningFile =
        Path.GetFullPath("../../../../rpt-corpus/boyum__Dunning_HANA.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_LineObjects_ParsedWithBounds()
    {
        Assume.That(File.Exists(DunningFile), Is.True,
            "Dunning_HANA corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(DunningFile);
        Assert.That(result.Success, Is.True);

        var lines = result.Report!.Sections.SelectMany(s => s.Objects)
            .OfType<LineObject>().ToList();
        Assert.That(lines, Is.Not.Empty, "Dunning_HANA places line objects");
        Assert.That(lines.All(l => l.Bounds.Width > 0 || l.Bounds.Height > 0), Is.True,
            "zero-extent shape records must be dropped at parse time");
    }

    // ---------------------------------------------------------------------------
    // Cross-tab tests
    // ---------------------------------------------------------------------------

    private static readonly string CrossTabFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Canada-CrossTab.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_CrossTab_ParsesAxesAndSummaryCell()
    {
        Assume.That(File.Exists(CrossTabFile), Is.True,
            "Canada-CrossTab corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CrossTabFile);
        Assert.That(result.Success, Is.True);

        var crossTab = result.Report!.Sections.SelectMany(s => s.Objects)
            .OfType<CrossTabObject>().FirstOrDefault();
        Assert.That(crossTab, Is.Not.Null, "expected a CrossTabObject from the tag-185 wrapper");
        Assert.That(crossTab!.RowGroupFields, Does.Contain("Region"));
        Assert.That(crossTab.ColumnGroupFields, Does.Contain("Product Type Name"));
        Assert.That(crossTab.Cells, Has.Count.EqualTo(1),
            "repeated total-cell references must be deduplicated");
        Assert.That(crossTab.Cells[0].FieldName, Is.EqualTo("Order Amount"));
        Assert.That(crossTab.Cells[0].Function, Is.EqualTo(AggregateFunction.Sum));
    }

    [Test]
    public void RptParser_CrossTabAxisFields_DoNotLeakIntoReportGroups()
    {
        // tag-229 is shared by real report groups ("@Group #N Order") and cross-tab/chart
        // axis definitions ("@Row #N Order" / "@Column #N Order" / "@Detail Value Grid
        // #N Order"). Canada-CrossTab.rpt has no real grouping — its row/column axis
        // fields must not appear as phantom GroupDefinition entries.
        Assume.That(File.Exists(CrossTabFile), Is.True,
            "Canada-CrossTab corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CrossTabFile);
        Assert.That(result.Success, Is.True);
        Assert.That(result.Report!.Groups, Is.Empty);
    }

    // ---------------------------------------------------------------------------
    // Chart tests
    // ---------------------------------------------------------------------------

    private static readonly string PieChartFile =
        Path.GetFullPath("../../../../rpt-corpus/benbrahim777__Top5USA-piechart.rpt",
            AppContext.BaseDirectory);

    [Test]
    public void RptParser_Chart_ParsesTitleFieldsAndPieType()
    {
        Assume.That(File.Exists(PieChartFile), Is.True,
            "Top5USA-piechart corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(PieChartFile);
        Assert.That(result.Success, Is.True);

        var charts = result.Report!.Sections.SelectMany(s => s.Objects).OfType<ChartObject>().ToList();
        Assert.That(charts, Has.Count.EqualTo(2), "expected the two tag-180 chart objects in this file");
        Assert.That(charts, Has.All.Matches<ChartObject>(c => c.Kind == ChartKind.Pie));
        Assert.That(charts, Has.All.Matches<ChartObject>(c => c.CategoryFields is ["Customer Name"]));
        Assert.That(charts, Has.All.Matches<ChartObject>(c => c.SeriesField == "Order Amount"));
        Assert.That(charts, Has.All.Matches<ChartObject>(c => c.SeriesFunction == AggregateFunction.Sum));
        Assert.That(charts.Select(c => c.Title), Does.Contain("Top 5 Customers Percentage of Total Orders"),
            "the custom-titled chart's title must be captured verbatim");
    }

    private static readonly string CrossTabChartFile = CrossTabFile;

    [Test]
    public void RptParser_Chart_DrivenByCrossTabUsesDifferentTypeByte()
    {
        Assume.That(File.Exists(CrossTabChartFile), Is.True,
            "Canada-CrossTab corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(CrossTabChartFile);
        Assert.That(result.Success, Is.True);

        var chart = result.Report!.Sections.SelectMany(s => s.Objects).OfType<ChartObject>().FirstOrDefault();
        Assert.That(chart, Is.Not.Null, "expected a ChartObject from the tag-180 wrapper");
        Assert.That(chart!.CategoryFields, Is.EqualTo(new[] { "Product Type Name" }));
        Assert.That(chart.SeriesField, Is.EqualTo("Order Amount"));
        // tag-284 byte[2]=0x02 here vs 0x01 for pie charts; only one confirmed sample of
        // this value exists, so the parser conservatively falls back to Column rather
        // than asserting a specific non-Pie type.
        Assert.That(chart.Kind, Is.EqualTo(ChartKind.Column));
    }

    // ---------------------------------------------------------------------------
    // WMF rasterization tests
    // ---------------------------------------------------------------------------

    [Test]
    public void WmfRasterizer_RoundTripsGdiMetafileToPng()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.True, "GDI+ rasterization is Windows-only");

        // Build a small metafile in memory with GDI+ and feed its bytes through
        byte[] wmfBytes;
        using (var referenceBitmap = new System.Drawing.Bitmap(1, 1))
        using (var referenceGraphics = System.Drawing.Graphics.FromImage(referenceBitmap))
        {
            var stream = new MemoryStream();
            nint hdc = referenceGraphics.GetHdc();
            try
            {
                using var metafile = new System.Drawing.Imaging.Metafile(stream, hdc,
                    new System.Drawing.Rectangle(0, 0, 100, 50),
                    System.Drawing.Imaging.MetafileFrameUnit.Pixel);
                using var g = System.Drawing.Graphics.FromImage(metafile);
                g.FillRectangle(System.Drawing.Brushes.Navy, 10, 10, 80, 30);
            }
            finally
            {
                referenceGraphics.ReleaseHdc(hdc);
            }
            wmfBytes = stream.ToArray();
        }

        byte[]? png = WmfRasterizer.TryRasterizeToPng(wmfBytes);

        Assert.That(png, Is.Not.Null, "the metafile should rasterize");
        Assert.That(png![0], Is.EqualTo(0x89));   // PNG magic
        Assert.That(png[1], Is.EqualTo(0x50));
    }

    [Test]
    public void WmfRasterizer_FindMetafileOffset_LocatesWmfAndEmfAfterPrefixes()
    {
        // OlePres000-style stream: a small header then the placeable WMF magic
        byte[] wmf = new byte[40];
        wmf[12] = 0xD7; wmf[13] = 0xCD; wmf[14] = 0xC6; wmf[15] = 0x9A;
        Assert.That(WmfRasterizer.FindMetafileOffset(wmf), Is.EqualTo(12));

        // EMF after a 4-byte prefix: EMR_HEADER iType=1 with " EMF" at header offset 40
        byte[] emf = new byte[64];
        emf[4] = 0x01;
        emf[44] = 0x20; emf[45] = 0x45; emf[46] = 0x4D; emf[47] = 0x46;
        Assert.That(WmfRasterizer.FindMetafileOffset(emf), Is.EqualTo(4));

        Assert.That(WmfRasterizer.FindMetafileOffset(new byte[40]), Is.EqualTo(-1),
            "no magic present → -1");
    }

    // ---------------------------------------------------------------------------
    // Subreport tests
    // ---------------------------------------------------------------------------

    [Test]
    public void RptParser_Subreports_ParsedRecursivelyFromSubdocumentStorages()
    {
        Assume.That(File.Exists(PaymentsFile), Is.True,
            "Boyum Payments corpus file not found — run scripts/download-test-rpts.sh");

        var result = RptParser.Parse(PaymentsFile);
        Assert.That(result.Success, Is.True);

        var subs = result.Report!.Sections.SelectMany(s => s.Objects)
            .OfType<SubreportObject>()
            .OrderBy(s => s.SubdocumentIndex)
            .ToList();
        Assert.That(subs, Has.Count.EqualTo(2), "Payments.rpt places two subreports");
        Assert.That(subs[0].SubdocumentIndex, Is.EqualTo(1));
        Assert.That(subs[1].SubdocumentIndex, Is.EqualTo(2));

        foreach (var sub in subs)
        {
            Assert.That(sub.Report, Is.Not.Null,
                $"subreport '{sub.SubreportName}' should parse from 'Subdocument {sub.SubdocumentIndex}/Contents'");
            Assert.That(sub.Report!.Sections, Is.Not.Empty,
                "inner report should contain sections");
        }
    }
}
