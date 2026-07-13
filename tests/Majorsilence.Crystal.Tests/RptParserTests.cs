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
