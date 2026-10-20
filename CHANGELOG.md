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

- **The report's own record sorts are read** (#31). A converted report now sorts its details as
  the report does, ascending or descending, whatever order the data arrives in. They agree
  with the Crystal runtime's for every report in all three corpora.
- **A subreport with no rows prints its header and footer** (#38), where it printed nothing.
- **A group's top or bottom N prints as Crystal's does**: the Top5USA family keeps only the
  N groups ranked first by the summary, in rank order, while percentages of the grand total
  still count the rest, as Crystal's do. A percentage summary with no format of its own
  prints with two decimals and a percent sign, where it printed a bare number.
- **A formula's result type is read from the file** (#9), so an object showing a numeric
  formula gets the number format the report gives it, where it printed unformatted.
- **More date formats are honoured** (#11): a date showing only two of month, day and year
  with different separators, text before or after the date, and the day of week before or
  after it. Each was checked against what Crystal prints for the same setting.
- **A group footer that lists or totals its group through a variable prints it** (#19).
  Crystal's usual way of doing that takes three formulas: one in the group header resets a
  variable, one in the details adds each record to it, and one in the group footer shows
  it. The footer printed nothing, because an RDL expression has no variables; it is now
  written as the aggregate it amounts to, a joined list or a sum over the group's records.
  Only that exact shape is rewritten; any other use of a variable still prints empty.
- **A formula that builds its value in steps prints it** (#19): an address assembled line by
  line, a value computed through a variable or two, an if that sets a variable. Such a
  formula is reduced to the single expression it computes. One that loops, uses arrays, or
  reads a variable left over from an earlier record still prints empty.
- **The same, for Basic-syntax formulas**, the dialect most production reports use: an
  address built in a Shared variable line by line, a Dim set in an If block, an ElseIf
  chain. Across a corpus of 2,324 production reports, the share of formulas that printed
  nothing fell from 30% to 21%.
- **A report with several Details sections prints one line per band under each record**
  (#7), each object where its band draws it, where they were flattened into one line. Each
  band's own suppress formula applies: section formulas sharing a name (every section's
  visibility formula is called Section_Visibility) are told apart by the position the file
  records for each, where every section used to get the last one read.
- **A formula that sets a Shared or Global variable only under conditions prints**, when
  no other formula writes that variable: a label chosen by a parameter, an address whose
  first line is optional.

**Fixed**

- **Sections hidden for drill-down no longer print.** A summary report that keeps its details
  (and often its group footers) for drill-down, rather than suppressing them, printed every
  detail row. The area-level "Hide (Drill-Down OK)" and suppress flags are now read, and
  agree with the Crystal runtime's for every report in the public and third-party corpora.
- **A suppressed report header takes no space.** It was not drawn but still pushed the rest
  of the report down by its height: four blank inches on Top5USA-piechart.
- **An empty Report Comments field prints nothing**, where it printed two quote marks.
- **A field placed in a group header or footer prints**, where the band's own cells did not
  write it. Only a field matching a detail column had a cell, so a formula there, or a
  database field the details do not show, was dropped.
- **A subreport is reached by the name Crystal gives it**, such as "CanadaOrders.rpt", as
  well as by its placed object's name. `SubreportData` and `SubreportParameters` naming a
  subreport as the Crystal runtime does were skipped when the report had more than one.
  `ReportEngine.Analyze` now reports the runtime's names.
- **A group section written outside the details table no longer prints when there are no
  rows**, as Crystal prints no group section then. Forms laid out in group sections print a
  blank page with no data, as they do in Crystal.
- **Every font is the size Crystal draws it, to the twentieth of a point** (#23). A
  converted report's font sizes are now the ones Crystal's own PDF export writes. Bold text
  below about 15pt had been off by up to a third of a point, and 10pt Courier New by half a
  point. Across 78 public reports, 262 of the 306 distinct sizes Crystal writes now match
  exactly, against 137 before; nearly all the rest are chart, cross-tab and map text.
  Bold Arial at 10pt is now 8.75pt, where it was 8.95.
- **`crystalcli verify` can report an error** (#17). It looked for messages starting "Error"
  or "Fatal", which the engine never writes, so every file passed. All three corpora still
  verify clean with the working check.

**Changed**

- **The engine is Majorsilence.Reporting 27.0.0-alpha.2**, a pre-release, and the package
  referenced is `Majorsilence.Reporting.RdlEngine` rather than its `.SkiaSharp` alias. A
  table with no rows now prints its header and footer, so a report rendered with no data
  prints its column labels and report header, and a subreport its report footer. A
  free-form subreport printed 16pt or more right of its place on this engine, its offset
  counted twice; the converter no longer repeats it.
- **`RuntimeOverrides.SortByFieldName` does what Crystal's callers do**: it regroups a grouped
  report on the field, replaces the first record sort of an ungrouped one, and warns, changing
  nothing, on a report with neither. It used to add a sort when the report had none.
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
