---
name: crystal-fidelity
description: How to keep bringing Majorsilence.Crystal's output closer to what Crystal Reports prints - reading the .rpt format, establishing Crystal's behaviour from its own output, measuring against Crystal's renders, and the conventions changes follow. Use when changing the parser, converter or the Majorsilence.Reporting engine for fidelity, when reading .rpt records, or when measuring renders against Crystal's.
---

# Crystal fidelity work

Majorsilence.Crystal reads Crystal Reports `.rpt` files without the vendor runtime, converts
them to RDL, and renders that RDL with the Majorsilence Reporting engine
([majorsilence/Reporting](https://github.com/majorsilence/Reporting)). The goal is that a
report prints as Crystal prints it. Progress is measured, not judged by eye.

## The pipeline, and where a fix belongs

| Stage | Where | What it does |
|---|---|---|
| Container | `src/Majorsilence.Crystal.Parser/OleStorage` | The `.rpt` is an OLE compound file. `Contents` holds the report as a flat sequence of tagged records (TSLV); `QESession` holds the query engine's session (tables, columns, links). |
| Parser | `src/Majorsilence.Crystal.Parser/RptParser.cs` | Turns records into the model. Each record's bytes are documented where they are read. |
| Model | `src/Majorsilence.Crystal.Model` | `ReportDefinition`, sections, objects, fields, formats. |
| Converter | `src/Majorsilence.Crystal.Converter` | `RdlConverter` writes RDL; `FormulaTranspiler` and `Formula/RdlEmitter` turn Crystal formulas into RDL expressions. |
| Render | `src/Majorsilence.Crystal.RptEngine`, `Majorsilence.Crystal.Runtime` | Push data and parameters, render through the engine. |
| Engine | separate repo, referenced as the `Majorsilence.Reporting.RdlEngine` package | Pagination, text layout, aggregates. Build against a checkout with `-p:ReportingSource=<path>`. |

Fix in the converter when RDL can say what Crystal does. Change the engine only when it
cannot (no RDL construct for the behaviour, or the engine does the RDL wrong), and then the
converter change waits for an engine release that has it: bump the package version in the
same commit that relies on it, or CI's visual suite breaks.

## Rules

- **Describe the format and Crystal's behaviour, never where knowledge came from.** Write
  "record 352 byte 4 holds Convert Database NULL Values to Default" and "the Crystal runtime
  prints X for this report". Never decompile or disassemble the vendor runtime, and never say
  something was found that way. Behaviour is established from the file itself and from what
  the runtime does through its public API: what it prints, exports and reports.
- **Private reports stay private.** If you measure against reports you were given privately,
  never put their file names, client or place names, formulas, field names or data in the
  repo, tests, comments, commit messages or PRs. Tests use the public corpora or a
  `ReportDefinition` built in code with made-up names.
- **No employer or product names** in code, comments, commits or docs.
- **Tests:** every fix has tests that fail without it. Run the three suites separately (two
  `.slnx` files at the root make a bare `dotnet test` fail with MSB1011):
  `dotnet test tests/Majorsilence.Crystal.Tests`,
  `dotnet test tests/Majorsilence.Crystal.RptEngine.Tests`,
  `dotnet test tests/Majorsilence.Crystal.VisualRegression.Tests`.
  Engine changes add tests in `ReportTests` built from RDL written in the test and run on
  both target frameworks.
- **CHANGELOG.md:** an entry under `## Unreleased`, in the style already there: a bold sentence
  saying what now prints as Crystal prints it, then what it did before and why.
- **Comments** say what Crystal does and why the code does this, in a short paragraph. Not a
  history of attempts. Match the surrounding code's density and naming.
- Keep diffs as small as the fix allows; other work changes the same large files
  (`RdlConverter.cs`, `RptParser.cs`).

## The method

1. **Find a difference.** Render a report with made-up data through Crystal and through us,
   and look at both with an overlay (Crystal's ink red, ours blue, shared ink black). A side by
   side shows *what* differs; the overlay shows by how much.
2. **Find the object and its record.** `crystalcli tags <file.rpt> [--hex <tag>] [--strings
   <tag>]` dumps the record sequence; `crystalcli scan <dir>` batch-parses and histograms tags;
   `crystalcli convert` writes the RDL. Compare the converted RDL with what the object asks for.
3. **Establish what Crystal does, by observation.** Where the file is ambiguous, ask the
   runtime: change one input and watch the output. The reference renderer
   (`tools/Majorsilence.Crystal.ReferenceRenderer`, net48, needs the Crystal runtime
   installed) renders a report to PNG or PDF, exports its saved rows as a fixture (`--rows`),
   or makes up rows and renders from them (`--synthetic`). A small net48 probe can load a
   report through `ReportDocument`, read what the runtime reports for an option, replace a
   formula's text and export, or build a section with one object and measure its position in
   the exported PDF. Measure in the PDF (text positions, baselines), not by eye.
4. **Write the rule down in the code**, where the record is read or the RDL is written, as
   what Crystal does.
5. **Test it**: a converter test on the RDL, and a render test where the engine can run it.
6. **Measure corpus-wide** (below) against a build of the same starting point, then look at
   every report that dropped by more than a point. A drop is often a real fix uncovering an
   older offset that it used to cancel; say so with evidence, or fix it.

## Measuring

- **Ink agreement** (0-100): the share of ink pixels that line up between Crystal's render and
  ours, in cells, on page 1. Mean pixel similarity is not used: a blank page scores high.
  `VisualRegressionTests.InkAgreementPercent` is the implementation.
- **Public baselines:** `tests/Majorsilence.Crystal.VisualRegression.Tests` scores the public
  corpus (`scripts/download-test-rpts.sh --download-only`, plus `--with-rpt-rs` for the
  external set) against `tests/reference-renders` with fixtures in `tests/reference-data`.
  Each case has a recorded baseline and a tolerance of 1.0. A case that improves by more than
  5 fails too: raise its baseline only when agreement truly rose.
- **Compare like with like.** A before/after pair must use the same engine, the same fixture
  loader and the same references. Build the "before" tool from your starting commit; don't
  compare with an old results file. Print dates and times give ±0.2 of noise.
- **Small moves on page 1 can hide large wins.** A fix to a later page, or to a few numbers,
  barely moves page 1 ink. Check the text of all pages when that is what changed.
- **Data matters.** Made-up rows rarely trigger every path (one record per group, empty
  fields). Read what a fixture holds before concluding a change does nothing.

## What the format holds (established)

Record and byte positions are within the record's payload. Each is documented in
`RptParser.cs` where it is read.

- **Field value types:** 2 Int16, 3 Int32, 4 Float32, 5 Float64, 6 Currency, 7 Number (double),
  8 Boolean, 9 Date, 10 Time, 11 String, 12-13 Memo, 14 Blob, 15 DateTime. A parameter's
  value type uses 6 Number and 7 Currency; the QueryEngine's numbering of database columns
  differs from the parameter's for 6 and 7.
- **Numeric format (tag 248):** [1] suppress if zero; [2] negative style (0 none, 1 leading
  minus, 2 trailing minus, 3 bracketed); [4] thousands separator; [6] leading zero;
  [8] decimals; [10] currency symbol (0 none, 1 fixed, 2 floating); [12] one symbol per page;
  [13] symbol position; then strings. An object carries one record for Currency and one for
  Number, and a currency value takes the first.
- **Null handling:** record 352 byte 4 is Convert Database NULL Values to Default, byte 28
  Convert Other NULL Values; a formula's own null treatment is tag 118 byte 18.
- **Printer binding:** record 5 present means the report is set for No Printer (laid out for
  the screen, 96 dpi). With record 3 it names a printer; with neither it takes the default
  printer. Default margins: a quarter inch (360 twips) for No Printer, the printer's own
  (a sixth of an inch, 240) otherwise.
- **Running total operation:** an Int16 (little-endian) just after the running total's own
  field definition (the tag-113 block), before the summarized field's name: 0 sum, 1 average,
  4 maximum, 5 minimum, 6 count, 9 distinct count, 15 and 16 the Nth largest and smallest;
  then N as a big-endian Int16. It agrees with the runtime for every running total in the
  corpora.
- **Section (area) tag 254:** New Page After N Visible Records is an Int16 at bytes 49..50,
  with its formula hook 12; Suppress Blank Section is bytes 15..16 of its child record.
  Keep Together and Keep Group Together are read into `Section.KeepTogether` and
  `GroupDefinition.KeepTogether`.

## What Crystal does (established)

**Text**
- Line pitch for a report bound to a printer comes from the printer's metrics at 600 dpi:
  GDI ascent, descent and leading, each rounded to twips separately. No Printer reports use
  the screen's pitch.
- Lines break where GDI's hinted whole-pixel advances say, at 96 dpi (No Printer) or 600 dpi,
  measured in a font whose em is the point size against the box rounded to whole pixels,
  though Crystal draws the smaller em GDI gives that size. Its lines stop about a tenth short
  of the right edge.
- First baseline: a field's is the font's GDI ascent with the point size as the em, in twips;
  a text object's is that ascent in whole device pixels (600 or 96 dpi). A field whose value
  runs to a second line follows the text-object rule.
- A box that may not grow prints line n when its height is at least
  min(1.2·(n−1)·pitch, n·pitch).
- A text object that may not grow, in a box one line tall, is not broken into lines: it is set
  on the one line and cut at the box edge.
- A section grows to the bottom of the object that grew, not by how much it grew; the room
  below the object is not kept.

**Numbers, dates and text**
- `ToText(x)`, `ToText(x, n)` and `CStr(x, n)` group thousands; `ToText(x)` of a number gives
  two decimals; `ToText(x, 0)` none. A number joined to a string with `&` formats the same way.
  A currency value gets its symbol.
- A Date parameter or Date formula prints the date alone. Print Time, Data Time and
  Modification Time print in their object's time record.
- Crystal's numbers are doubles, so `if c then 0 else a / b` keeps its fractions.

**Formulas and summaries**
- `Sum({x}, {G})` (and Count, Average, Maximum, Minimum, DistinctCount, and
  `NthLargest(1, x, {G})`) inside a formula is the total of the G group the current record
  belongs to.
- `Next({x})` and `Previous({x})` follow the printed order and cross group boundaries. A group
  header's formulas see the group's first record (whose Previous is the last record of the
  group before); a footer's see its last record (whose Next opens the next group); a page
  header sees the first record on the page, the report footer the last. Next is null on the
  last record and Previous on the first, and a formula that reads one there is null; as a
  suppress condition, null means the section prints.
- A running balance kept in a Shared or WhilePrintingRecords variable (set in a group header,
  added to in the details or an inner footer) is what prints, record by record, in print order.
- With Convert Database NULL Values on, a null reads as "", 0 or False, IsNull is False, and
  summaries count the record. A picture (blob) has no default, so IsNull of it stays True.
- With it off, a formula that reads a null field is null.

**Grouping, pagination, running totals**
- Groups on a string field sort as strings ("1", "11", "3").
- Keep Group Together moves a group that does not fit to the next page; one longer than a page
  starts a page and runs on.
- A Details New Page After formula (one reading only a parameter, say) breaks after each
  record it is true for, and replaces the checkbox.
- A running total resets before the record where its field changes, or where its reset formula
  is true. One evaluated "on change of group" adds the first record of each instance of the
  group, where an instance starts whenever the group's or an outer group's value changes.
- A page footer holding fields prints at the foot of every page.
- An area's suppress formula does not print a section that its own Suppress checkbox hides;
  a section's own suppress formula does override its checkbox.

## The engine, as it stands (27.0.0-alpha.6 and later)

- Text: the baseline is the font's hhea ascent below the top of the box; without a LineHeight
  the pitch is ascent + descent + lineGap. The converter writes LineHeight wherever Crystal's
  pitch is known and sets the first baseline through top padding, taken back off the bottom.
- A growing box is measured with the same word wrap the PDF draws with, in the font it draws
  in.
- A null number reads as NaN in the released engines; `x = Nothing` catches both.
- An aggregate takes a grouping name as its scope (`Sum(x, "Group1")`).
- `IIf` converts its second branch to the first branch's type, so `IIf(c, F, Nothing)` with a
  numeric F throws; put `Nothing` first. A whole-number literal first branch types the whole
  `IIf` as Int32.
- `Previous(x, "Table1")` and `Next` read every earlier row on each call; running Max and Min
  keep only the last row they were read at. Both limit what running totals can be mapped to.
- `Next` and `Previous` with no scope, in a table's details, read the DataSet's rows in the
  order they were read, not the table's sorted order: scope them to the table.
- `PageBreakCondition` on a break at a group's end is honoured from 27.0.0-alpha.7; earlier
  releases break after every instance.
- No nested aggregates: a per-record test that needs one goes in a calculated field.

## Pitfalls that cost time

- **Shared `obj` folders.** Two builds referencing the same project directories share `obj`,
  and MSBuild's incremental clean deletes other builds' outputs (SkiaSharp, HarfBuzzSharp and
  their native `runtimes` folder), even mid-run. Give every tool build its own worktree, and
  check its output folder after a scoring run before trusting the scores. A run where renders
  fail with "type initializer for SkiaSharp" is invalid.
- **Temp cleanup.** Windows Storage Sense can delete work under `%TEMP%`. Keep anything worth
  keeping outside it.
- **`git clean`** in a worktree removes directory junctions to the corpora; relink them, and
  check the test count (about 1,700 with the corpora present, about 800 without).
- **Line endings.** A patch with CRLF or a BOM fails to apply ("lacks the necessary blob").
  Write patches with LF and no BOM, and never rewrite a whole file: a CHANGELOG that changes
  every line's ending can't be merged. When two changes both add entries, keep both.
- **Shells.** Git Bash rewrites `https://` arguments and backslash paths; run `dotnet` with URLs
  from PowerShell. PowerShell variable names ignore case, so `$scores` and `$Scores` collide.
- **Linux CI fonts.** On Linux, Arial is Liberation Sans, and the graphics library's widths
  differ from the PDF's. A test that sizes a box from the text must use the width of the font
  the PDF draws in.
- **Fixtures.** Load "yyyy-MM-dd HH:mm:ss" as dates as well as "yyyy-MM-dd". A loader that types
  numeric-looking strings as numbers changes group order. References rendered on A4 while we
  print the report's Letter page differ everywhere.

## Open leads

- `Next()` and `Previous()` inside formula fields, and `OnFirstRecord`/`OnLastRecord`/`Next` in
  page-header and page-footer objects, are not mapped yet.
- A field whose value is empty draws no border in Crystal; we draw it.
- A fixed currency symbol prints on the first amount only in some reports, though the runtime
  reports one symbol per page as off.
- A formula named like a column the details also print: Crystal prints the formula.
- Running totals whose evaluate formula is `if A then <boolean>` with no else.
- A DateTime on the machine's formats with the object's time record (needs a Value expression).
- New Page After N records with printing group headers needs a "record opens a group" break.
- Running totals over the whole report evaluated on change of group, and running Max/Min,
  wait on a constant-time `Previous` and RunningState-based Max/Min in the engine.
- A null decimal field reads as the engine's internal marker in arithmetic.
