using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Parser;
using Majorsilence.Crystal.Parser.Chunks;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// A group whose sort direction is a formula ("Use a Formula as Group Sort Order"). Crystal
/// sorts by what the formula returns, whatever direction the group stores: a group stored as
/// ascending whose formula is crOriginalOrder prints in the order the records come.
/// </summary>
[TestFixture]
public class GroupSortFormulaTests
{
    private static byte[] Mutf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        return [0, 0, (byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }

    // A group record: the field, the six option bytes (ascending), the hooks a group has,
    // and last the sort direction formula's hook with the formula's position after it.
    private static TslvRecord Group(int? formulaOrdinal)
    {
        byte[] data =
        [
            .. Mutf8("Orders.Region"), 0, 0, 2, 0, 0, 0,
            .. Mutf8("Others"), 0, 0, 0, 1,
            .. Mutf8("@Group #1 Order"), 1, 0, 15, 0xFF, 0xFF,
        ];
        if (formulaOrdinal is { } ordinal)
            data = [.. data, .. Mutf8("@Group Sort Order Formula"), 1, (byte)(ordinal >> 8), (byte)ordinal, 0x39, 0xD8];
        return new TslvRecord { Tag = 229, Data = data };
    }

    [Test]
    public void AGroupsSortDirectionFormula_IsFoundByItsPosition()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RptParser.GroupSortFormulaOrdinal(Group(69)), Is.EqualTo(69));
            Assert.That(RptParser.GroupSortFormulaOrdinal(Group(300)), Is.EqualTo(300));
            Assert.That(RptParser.GroupSortFormulaOrdinal(Group(null)), Is.Null, "a group without the formula");
        });
    }

    [Test]
    public void ASortDirectionFormulaThatIsAConstant_GivesTheDirection()
    {
        // What the formula editor leaves when only the constant is typed: its template, a
        // comment that lists all three, and then the one chosen.
        const string template = "// This conditional formatting formula must return one of the following Group Sort Order Constants:\r\n"
            + "// \r\n// crAscendingOrder\r\n// crDescendingOrder\r\n// crOriginalOrder\r\n// \r\n ";

        Assert.Multiple(() =>
        {
            Assert.That(RptParser.ConstantSortOrder(template + "crOriginalOrder"), Is.EqualTo(GroupSortOrder.OriginalOrder));
            Assert.That(RptParser.ConstantSortOrder("crDescendingOrder;"), Is.EqualTo(GroupSortOrder.Descending));
            Assert.That(RptParser.ConstantSortOrder("CRASCENDINGORDER"), Is.EqualTo(GroupSortOrder.Ascending));
        });
    }

    [Test]
    public void ASortDirectionFormulaThatWorksItOut_LeavesTheStoredDirection()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RptParser.ConstantSortOrder("if {?ByName} then crAscendingOrder else crOriginalOrder"), Is.Null);
            Assert.That(RptParser.ConstantSortOrder("// crOriginalOrder"), Is.Null, "only a comment");
            Assert.That(RptParser.ConstantSortOrder(null), Is.Null);
        });
    }
}
