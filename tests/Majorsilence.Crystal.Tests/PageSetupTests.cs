using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

// Which page a report prints on. Record 398 holds the page it was last laid out on, with
// byte 41 set when that page is the template's own; clear, the report follows a printer,
// and the paper its printer settings (record 7) name is the one Crystal prints on.
[TestFixture]
public class PageSetupTests
{
    private const int Letter = 1, Legal = 5, A4 = 9;

    private static TslvRecord PageSetup(int widthTwips, int heightTwips, bool ownPage)
    {
        var data = new byte[48];
        data[0] = (byte)(widthTwips >> 24); data[1] = (byte)(widthTwips >> 16);
        data[2] = (byte)(widthTwips >> 8); data[3] = (byte)widthTwips;
        data[4] = (byte)(heightTwips >> 24); data[5] = (byte)(heightTwips >> 16);
        data[6] = (byte)(heightTwips >> 8); data[7] = (byte)heightTwips;
        data[41] = ownPage ? (byte)1 : (byte)0;
        return new TslvRecord { Tag = 398, Data = data };
    }

    // Two leading bytes, the mask, then one big-endian 16-bit value per set bit in bit order.
    private static TslvRecord PrinterSettings(int mask, params int[] values)
    {
        var data = new List<byte> { 0, 0, (byte)(mask >> 8), (byte)mask };
        foreach (int v in values) { data.Add((byte)(v >> 8)); data.Add((byte)v); }
        return new TslvRecord { Tag = 7, Data = data.ToArray() };
    }

    private const int Orientation = 0x1, PaperCode = 0x2, Length = 0x4, Width = 0x8;
    private const int Portrait = 1, Landscape = 2;

    // A real template's settings: the mask also carries scale, copies, source and quality,
    // which follow the paper and must not shift it.
    [Test]
    public void PrinterSettings_FromATemplate_NameLetterPortrait()
    {
        string path = Path.GetFullPath("../../../../rpt-corpus/souvikduttachoudhury__CustomFunctions.rpt", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "run scripts/download-test-rpts.sh");

        var settings = RptParser.Parse(path).RawChunks.First(c => c.Tag == 7);

        Assert.That(RptParser.ReadPrinterPaper(settings), Is.EqualTo((((int, int)?)(12240, 15840), (bool?)false)));
    }

    [Test]
    public void FollowingAPrinter_TheSettingsPaperReplacesTheStoredPage()
    {
        var page = RptParser.ResolvePage(PageSetup(12240, 15840, ownPage: false),
                                         PrinterSettings(Orientation | PaperCode, Portrait, A4));

        Assert.That(page, Is.EqualTo((11906, 16838, false)));
    }

    [Test]
    public void ItsOwnPage_IsKeptWhateverThePrinterSettingsSay()
    {
        var page = RptParser.ResolvePage(PageSetup(12240, 15840, ownPage: true),
                                         PrinterSettings(Orientation | PaperCode, Portrait, A4));

        Assert.That(page, Is.EqualTo((12240, 15840, false)));
    }

    [Test]
    public void ALandscapeSetting_TurnsThePaper()
    {
        var page = RptParser.ResolvePage(PageSetup(12240, 15840, ownPage: false),
                                         PrinterSettings(Orientation | PaperCode, Landscape, Legal));

        Assert.That(page, Is.EqualTo((20160, 12240, false)));
    }

    [Test]
    public void WithNoOrientationSetting_TheStoredPageKeepsItsShape()
    {
        var page = RptParser.ResolvePage(PageSetup(15840, 12240, ownPage: false),
                                         PrinterSettings(PaperCode, A4));

        Assert.That(page, Is.EqualTo((16838, 11906, false)));
    }

    // A custom size is stored as a length and width in tenths of a millimetre, and wins
    // over the paper code beside it: 100 x 150 mm here, beside a Letter code.
    [Test]
    public void ALengthAndWidth_WinOverThePaperCode()
    {
        var page = RptParser.ResolvePage(PageSetup(12240, 15840, ownPage: false),
                                         PrinterSettings(Orientation | PaperCode | Length | Width, Portrait, Letter, 1500, 1000));

        Assert.That(page, Is.EqualTo((5669, 8504, false)));
    }

    // Settings that name no paper this can size leave the report on its printer's paper,
    // the stored page kept as the best guess; an orientation still turns it, as Crystal
    // turns the printer's paper.
    [TestCase(Orientation, new[] { Landscape }, 20160, 12240, TestName = "PrinterSettings_WithOnlyAnOrientation_TurnTheStoredPage")]
    [TestCase(Orientation | PaperCode, new[] { Landscape, 149 }, 20160, 12240, TestName = "PrinterSettings_WithAnUnknownPaperCode_TurnTheStoredPage")]
    [TestCase(PaperCode, new[] { 256 }, 12240, 20160, TestName = "PrinterSettings_WithOnlyAnUnknownPaperCode_LeaveTheStoredPage")]
    public void PrinterSettingsNamingNoPaper_LeaveTheReportOnItsPrintersPaper(int mask, int[] values, int width, int height)
    {
        var page = RptParser.ResolvePage(PageSetup(12240, 20160, ownPage: false), PrinterSettings(mask, values));

        Assert.That(page, Is.EqualTo((width, height, true)), "the printer's paper, so marked as such");
    }

    // With no paper of its own, the report prints on the printer's default paper: the
    // stored page is kept as the best guess and marked as the printer's.
    [Test]
    public void WithoutPrinterSettings_TheStoredPageIsKept_AsThePrintersPaper()
    {
        Assert.That(RptParser.ResolvePage(PageSetup(12240, 20160, ownPage: false), null), Is.EqualTo((12240, 20160, true)));
    }

    [Test]
    public void ItsOwnPage_IsNotThePrintersPaper()
    {
        Assert.That(RptParser.ResolvePage(PageSetup(12240, 20160, ownPage: true), null), Is.EqualTo((12240, 20160, false)));
    }

    // CrystalCmd's dataset sample, which Crystal prints on A4 against an A4 printer and on
    // Letter against a Letter one, from the same file.
    [Test]
    public void ATemplateOnItsPrintersPaper_IsMarkedSo()
    {
        string path = Path.GetFullPath("../../../../../../CrystalCmd/dotnet/Majorsilence.CrystalCmd.NetFrameworkServer/Majorsilence.CrystalCmd.Tests/the_dotnet_dataset_report.rpt", AppContext.BaseDirectory);
        Assume.That(File.Exists(path), Is.True, "needs a CrystalCmd checkout beside this one");

        var page = RptParser.Parse(path).Report!.Page;

        Assert.That((page.WidthTwips, page.HeightTwips, page.PaperFromPrinter), Is.EqualTo((12240, 15840, true)));
    }
}
