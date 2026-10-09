# Changelog

Release notes, newest first. [BACKLOG.md](BACKLOG.md) has the measurements behind each
line; [ROADMAP.md](ROADMAP.md) has what comes next.

## Unreleased

**Added**

- **A multi-table report renders from one table per Crystal table** (#36). Push each table
  under its name in `RuntimeOverrides.TableData`, and the engine joins them as the report's
  own links say: inner and left-outer joins on equal keys, in the order Crystal applies them.
  A report renders the same from per-table data as from the table a caller joined already,
  and a caller that pushes one pre-joined table through `Data` keeps working unchanged.
- **The report's tables and links are read from the file.** The `QESession` stream, the
  QueryEngine's saved session, is now decrypted and decoded. It gives the parsed report its
  data source, every table with its columns and alias, and its links
  (`ReportDefinition.TableLinks`); `ReportEngine.Analyze` lists them too. They agree with
  the Crystal runtime's own for every table and link in all three corpora: 79 links in the
  public one, 166 in the third-party one and 648 field pairs in the private one.

**Changed**

- **Each RDL field's `DataField` names its table** ("Orders.Customer ID"), so two tables
  with a column of the same name no longer collide in the flattened dataset. The RDL field
  names, and so every expression, are unchanged. The engine adds the qualified names to a
  pushed table that has only the bare ones, for `Data` and `SubreportData` alike.
- **A converted report's query is its own.** It selects every column of every table and
  joins them as the links say, and its data provider is the one the report connects
  through (ODBC or OLE DB) rather than always SQL.

## v0.3.2

**Changed**

- **The engine is Majorsilence.Reporting 26.0.7.** Two of its fixes reach this package:
  - A subreport with no details prints when it has no rows. A subreport that shows the
    parameter values its parent passed was blank; it now prints them, as Crystal does.
  - On Linux and macOS, PDFs use the fonts installed there by the family they declare, so a
    report in Arial gets Arial where it is installed rather than Liberation Sans.

**Known gaps** are those of v0.3.1. A subreport with a Details section still prints nothing
when it has no rows; Crystal prints its headers.

## v0.3.1

**Fixed**

- **A placed subreport is framed, sized and stacked as Crystal draws it** (#37). The
  subreport object's border is read and drawn as its frame; the subreport prints at its own
  position rather than directly under the item above it; a full-width band in it ends at its
  edge rather than the page's; and a second report-header section prints below the first
  instead of over it, in any report. CrystalCmd's subreport-data scenario rises from 67.9%
  to 88.0% ink agreement against Crystal.

## v0.3.0

**Added**

- **Data for subreports.** `RuntimeOverrides.SubreportData` maps a subreport name to the
  one flattened table it renders from, as `RuntimeOverrides.Data` is the main report's.
  Names match case-insensitively; a name that matches no subreport is a warning. No engine
  change was needed: `RptEngine` hands each subreport its table as the engine loads it.
  Until Reporting #345, a subreport given no rows still draws nothing.
- `RdlConverter.Description`, and a `keyedDescriptions` option on
  `RenderPrep.ConvertWithSubreports`, which writes each subreport companion's file stem as
  its Description so a renderer can tell subreports apart while they load.

**Known gaps** are those of v0.2.1, except that data can now be pushed to a subreport.

## v0.2.1

**Fixed**

- **An empty page header still takes its height above a report's Details**, in a report
  that reads no table. 0.2.0 dropped it, which put CrystalCmd's plain sample's text 0.42in
  above Crystal's. A section suppressed outright is now neither drawn nor spaced there, as
  in Crystal. Private corpus: 2 reports have their Details placed lower.

## v0.2.0

Three fixes found by CrystalCmd's acceptance corpus, which renders the same requests through
the Crystal runtime and through this engine and compares the pages.

**Fixed**

- **A report that reads no table prints its Details once**, as Crystal does (#32). Such a
  report rendered blank: its Details went nowhere, or into a table that prints nothing over
  no rows. Its page header and Details sections are now body bands, and the formulas placed
  there are written as expressions. Private corpus: 11 reports with Details content that
  was blank now print it.
- **A section's background colour is drawn across its band** (#34). The colour is read
  from the section's properties and drawn on table rows, free-form sections, body bands and
  the RDL page header and footer. An object's own colour still wins.
- **A report that follows its printer prints on the paper its settings name** (#33). The
  page record's flag says whether the stored page is the template's own. When it is not,
  the template's printer settings name the paper and orientation. Against the Crystal
  runtime's own page, private templates go from 2,072 to 2,163 of 2,203 matching, and none
  that matched stops.

**Added**

- `PageLayout.PaperFromPrinter` marks a template that prints on its printer's default
  paper, and `RuntimeOverrides.PrinterPaper` lays such a report out on the paper a host's
  printer holds. Given A4, the paper of the machine the references come from, 2,201 of the
  2,203 private templates match Crystal's page.
- `Section.BackColor`.
- `ReportDefinition.Page` is settable.

**Known gaps** are those of v0.1.0, and one more: a subreport that reads no table carries
its bands but draws nothing, because the engine runs a subreport only when one of its
datasets returns a row ([Reporting #345](https://github.com/majorsilence/Reporting/issues/345)).

## v0.1.0 — first preview

The first published packages: `Majorsilence.Crystal.Model`, `.Parser`, `.Converter`,
`.Runtime`, `.RptEngine` and `.Cli`. A preview: the render path is measured on plain
tabular reports and structurally verified on everything else.

**What it does**

- Parses a Crystal Reports `.rpt` (OLE compound document, TSLV records) into a model:
  sections, fields, formulas, parameters, groups, running totals, objects with their
  positions and formats, subreports, charts, cross-tabs, embedded images.
- Converts the model to SSRS 2005 RDL: tables with group bands, page and report bands,
  formulas transpiled from Crystal and Basic syntax (a measured subset of the language,
  including the arithmetic, rounding and conditional semantics the Crystal runtime uses),
  parameters, subreport links, a first cross-tab axis and cell, single-series charts.
- Renders the RDL on the Majorsilence.Reporting engine (26.0.6, SkiaSharp build) to PDF,
  CSV, Excel (`.xlsx`) and RTF, with pushed data, parameters read by their declared type,
  and runtime overrides for formulas, record selection, sort, and object suppress, resize,
  move, text and can-grow. It never opens the connection a template names.

**What is measured**

- 11 public reports rendered with data score 86–99% ink agreement against Crystal's own
  PDF output. Subreports, cross-tabs and charts have no data fixtures and are not measured.
- Every report in three corpora (88 public, 114 third-party, 2,324 private) parses,
  converts and loads in the engine with 0 errors; a deliberately broken control still
  flags 87 of 88.

**Known gaps** (see ROADMAP.md, Stages 2 and 3)

- One flattened data table per report; the file's table links are not decoded, so a
  multi-table report needs its data joined before it is pushed.
- No data can be pushed to a subreport.
- The report's own sort order is not decoded (`SortByFieldName` supplies one).
- Formula-driven page breaks on the Details section, Crystal variable declarations, and
  the export types with no engine equivalent (the template itself, plain text, Word) are
  not expressed.
