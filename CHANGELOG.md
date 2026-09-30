# Changelog

Release notes, newest first. [BACKLOG.md](BACKLOG.md) has the measurements behind each
line; [ROADMAP.md](ROADMAP.md) has what comes next.

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
