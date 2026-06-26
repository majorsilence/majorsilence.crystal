using Majorsilence.Crystal.Model.Fields;
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
}
