using System.Xml.Linq;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Model.Objects;
using NUnit.Framework;

namespace Majorsilence.Crystal.Tests;

/// <summary>
/// Suppress Blank Section on a section whose text fields all print nothing: Crystal leaves the
/// section out, and its height with it.
/// </summary>
[TestFixture]
public class BlankSectionTests
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2005/01/reportdefinition";

    private static ReportDefinition Report(params ReportObject[] headerObjects) => new()
    {
        Fields =
        [
            new DatabaseField { Name = "Region", ColumnName = "Region", TableName = "Sales", DataType = "String" },
            new DatabaseField { Name = "Note", ColumnName = "Note", TableName = "Sales", DataType = "String" },
            new DatabaseField { Name = "Amount", ColumnName = "Amount", TableName = "Sales", DataType = "Float64" },
        ],
        Groups = [new GroupDefinition { Level = 0, FieldName = "Region", TableName = "Sales" }],
        Sections =
        [
            new Section { Type = SectionType.GroupHeader, GroupLevel = 0, HeightTwips = 240, SuppressBlank = true,
                Objects = [.. headerObjects] },
            new Section { Type = SectionType.Details, HeightTwips = 240, Objects = [
                new FieldObject { Name = "AmountField", FieldName = "Amount", Bounds = new(2880, 0, 1440, 240) }] },
        ]
    };

    private static string? GroupHeaderHidden(ReportDefinition report) =>
        XDocument.Parse(new RdlConverter().Convert(report))
            .Descendants(Rdl + "TableGroup").First().Element(Rdl + "Header")!
            .Descendants(Rdl + "TableRow").First().Element(Rdl + "Visibility")?.Element(Rdl + "Hidden")?.Value;

    private static FieldObject Note() => new() { Name = "NoteField", FieldName = "Note", Bounds = new(0, 0, 2880, 240) };

    [Test]
    public void ASectionOfTextFields_IsHiddenWhereTheyAllPrintNothing()
    {
        var hidden = GroupHeaderHidden(Report(Note()));

        Assert.That(hidden, Does.Contain("IsNothing(Fields!Note.Value)").And.Contain("CStr(Fields!Note.Value) = \"\""));
    }

    [Test]
    public void ASectionWithTextOrANumber_IsLeftToPrint()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GroupHeaderHidden(Report(Note(), new TextObject { Name = "Label", Text = "Note:", Bounds = new(3000, 0, 1440, 240) })),
                Is.Null);
            Assert.That(GroupHeaderHidden(Report(new FieldObject { Name = "AmountHead", FieldName = "Amount", Bounds = new(0, 0, 1440, 240) })),
                Is.Null);
        });
    }
}
