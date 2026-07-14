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
        // Only one <FontWeight>Bold</FontWeight> element should appear (for the header row);
        // the data row with Bold=false must not emit another one.
        int fwElements = System.Text.RegularExpressions.Regex.Matches(rdl, @"<\w+:FontWeight>|<FontWeight>").Count;
        Assert.That(fwElements, Is.EqualTo(1), "Only header row should emit FontWeight; data row with Bold=false should not");
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

    [Test]
    public void RdlConverter_Textbox_HasCanGrow()
    {
        var report = new ReportDefinition
        {
            ReportTitle = "CanGrow Report",
            Fields = [new DatabaseField { Name = "Name", ColumnName = "Name", DataType = "String" }],
            Sections =
            [
                new Section { Type = SectionType.Details, HeightTwips = 240,
                    Objects = [new FieldObject { FieldName = "Name", Bounds = new(0,0,1440,240) }] }
            ]
        };

        var converter = new RdlConverter();
        string rdl = converter.Convert(report);

        Assert.That(rdl, Does.Contain("<CanGrow>true</CanGrow>").Or.Contain(":CanGrow>true<"),
            "All Textbox elements should have <CanGrow>true</CanGrow>");
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

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
}
