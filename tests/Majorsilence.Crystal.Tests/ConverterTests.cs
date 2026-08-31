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
        Assert.That(rdl, Does.Contain("10pt"));
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
        Assert.That(rdl, Does.Contain("3.500in"), "Table width should be the sum of column widths");
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
        Assert.That(rdl, Does.Contain("<Value>\"\"</Value>").Or.Contain(":Value>\"\"<"),
            "Empty ReportComments should emit empty string value");
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
            "the plain Sum summary must still be placed in its table cell");
        Assert.That(rdl, Does.Contain(
            "=Sum(Fields!Amount.Value) / Sum(Fields!Amount.Value, \"DataSet1\") * 100"),
            "the Percentage summary must not be silently dropped");

        // Every table row must still have exactly one cell per column (no corruption
        // from the leftover-placement path).
        var doc = System.Xml.Linq.XDocument.Parse(rdl);
        var ns = doc.Root!.Name.Namespace;
        int columnCount = doc.Descendants(ns + "TableColumn").Count();
        foreach (var cells in doc.Descendants(ns + "TableCells"))
            Assert.That(cells.Elements(ns + "TableCell").Count(), Is.EqualTo(columnCount));
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
        var hiddenBox = textboxes.Single(t => (string?)t.Attribute("Name") == "Watermark");
        var visibleBox = textboxes.Single(t => (string?)t.Attribute("Name") == "Notice");

        Assert.That(hiddenBox.Descendants(ns + "Hidden").Single().Value, Is.EqualTo("true"));
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

        Assert.That(rdl, Does.Contain("<Hidden>=(Fields!Comments.Value = \"\")</Hidden>"));
        Assert.That(rdl, Does.Not.Contain("<Hidden>true</Hidden>"),
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
            Is.EqualTo("=(Fields!Code.Value = \"US\")"));
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
    public void RdlConverter_ChartInGroupFooterAllCellsFilled_EmittedAsLeftoverBodyItem()
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
                // empty cell exists for the chart to fall into — it must be emitted
                // as a positioned leftover body item instead of silently dropped.
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
        int columnCount = doc.Descendants(ns + "TableColumn").Count();
        foreach (var cells in doc.Descendants(ns + "TableCells"))
            Assert.That(cells.Elements(ns + "TableCell").Count(), Is.EqualTo(columnCount));
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
        Assert.That(rdl, Does.Contain("=Sum(Fields!Amount.Value)"));
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
        Assert.That(rdl, Does.Contain("=Sum(Fields!Amount.Value)"));
        Assert.That(rdl, Does.Contain("=Count(Fields!Units.Value)"));
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

        Assert.That(rdl, Does.Contain("RowNumber()"));
        Assert.That(rdl, Does.Not.Contain("recordnumber"));
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

        Assert.That(rdl, Does.Contain("<Width>7.500in</Width>"),
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
        int labelCells = doc.Descendants(ns + "Textbox")
            .Count(tb => tb.Element(ns + "Value")?.Value == "Amount");
        Assert.That(labelCells, Is.EqualTo(1),
            "the group header's label belongs in exactly one cell");
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
        Assert.That(table.Element(ns + "Left")?.Value, Is.EqualTo("1.500in"),
            "the table itself is where its first column is");

        var label = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Label");
        Assert.That(label.Element(ns + "Left")?.Value, Is.EqualTo("0.000in"),
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
        Assert.That(widths, Is.EqualTo(new[] { "2.000in", "1.000in" }),
            "a spacer column carries the gap so the data column stays where it was drawn");

        var printDate = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "PrintDate");
        Assert.That(printDate.Element(ns + "Left")?.Value, Is.EqualTo("0.000in"),
            "the date keeps the page's left edge instead of being clamped onto the column");

        var label = table.Descendants(ns + "Textbox")
            .First(tb => tb.Attribute("Name")?.Value == "Label");
        Assert.That(label.Element(ns + "Left")?.Value, Is.EqualTo("2.000in"),
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

    // RDL has no justify, so the one Crystal alignment without a schema equivalent is
    // written the way the target engine names it. Left is the default and stays implicit.
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

        Assert.That(table.Element(ns + "Left")?.Value, Is.EqualTo("0.500in"),
            "the table starts where its first column does");

        var widths = table.Element(ns + "TableColumns")!.Elements(ns + "TableColumn")
            .Select(c => c.Element(ns + "Width")!.Value).ToList();
        Assert.That(widths[0], Is.EqualTo("1.500in"),
            "the first column reaches the second: 2880 - 720 twips, gap included");
        Assert.That(widths[1], Is.EqualTo("1.000in"),
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

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
}
