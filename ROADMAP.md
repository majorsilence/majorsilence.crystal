# Roadmap: a first release, and an optional backend for CrystalCmd

[CrystalCmd](https://github.com/majorsilence/CrystalCmd) is a service that renders an
uploaded `.rpt` template with data the caller pushes to it (per-table CSV, parameters,
formula and object overrides) and returns a PDF or another export. Its only backend is the
SAP Crystal Reports runtime, which ties its worker to .NET Framework 4.8 and, on Linux, to
Wine. This repo's parser, converter and `RptEngine` can render the same input on .NET 10
with the Majorsilence.Reporting engine and no SAP dependency.

This document plans the work in three stages. Each stage ships on its own and has exit
criteria; the later ones widen the set of reports the backend can serve. It is a plan, not a
record: what was done and measured goes in [BACKLOG.md](BACKLOG.md) as it lands, and every
item here is tracked as a GitHub issue in the repository whose code it changes (see
[Tracking](#tracking-and-keeping-this-current)).

Conventions that hold throughout: `.rpt` files are never committed; the private corpus is
reported as counts only; every change runs the unit suite, the visual-regression suite and
the compile scans of all three corpora with their positive control, and gets a BACKLOG entry.

## Where things stand

- **What renders well.** Plain tabular reports score 86–99% ink agreement against Crystal's
  own PDF across the 11 public reports the visual suite can render with data. Every report in
  all three corpora (88 public, 114 third-party, 2,324 private) parses, converts and compiles
  in the engine with 0 errors.
- **What is not measured.** Subreports, cross-tabs and charts have no data fixtures, so their
  fidelity is unknown rather than known-bad. The private corpus has never been rendered with
  real data; its scans prove structure, not output.
- **The gaps are about data, not layout.** `RptEngine` takes one flattened `DataTable` for the
  main report and nothing for subreports. In the private corpus:

  | Shape | Reports | Share |
  |---|---|---|
  | Single table, no subreport with its own data, no cross-tab or chart | 1,153 | 50% |
  | Main report reads more than one table | 882 | 38% |
  | A subreport reads its own table | 519 | 22% |
  | Chart | 39 | 2% |

  (The shapes overlap. The first row is what Stage 2 can serve; Stage 3 is the next two.)

## Stage 1 — a first release (v0.1.0)

**Goal:** a tagged, published set of packages that a host can call to render an `.rpt` with
pushed data, offline, to the export formats CrystalCmd offers.

1. **Package `RptEngine`.** The repo defaults to `IsPackable=false` and
   `Majorsilence.Crystal.RptEngine` never opts back in, so a tag today would publish the
   parser, converter, runtime and CLI but not the rendering entry point. Opt it in with a
   `PackageId` and check `dotnet pack Majorsilence.Crystal.slnx` produces it.
2. **Render offline.** The converter copies the `.rpt`'s server name, DSN and SQL into the
   RDL, and `RptEngine` parses without `SkipDatabaseSchemaValidation`, so any table that is
   not handed data is queried on whatever host the template names. A service rendering
   uploaded templates must never do that. Two changes, belt and braces: a converter option
   that writes no connection string, and `RptEngine` setting the engine's skip flag so the
   run-time connect path returns before opening anything. Test on the emitted RDL (no host,
   no DSN) and on a render of a model that names a server (completes, no fatal error). The
   scratch render scan of the private corpus gets the same flag: today it attempts the
   connections the reports name.
3. **Override parity with CrystalCmd's contract.** Its `Data` object carries everything the
   SAP runtime is asked to do; `RuntimeOverrides` covers most of it. Close the rest:
   - `CanGrow` per object (the model already parses `ObjectFormat.CanGrow`; add the override
     and apply it beside `Suppress`).
   - Sort-by takes a table *and* a field; `SortByFieldName` takes only a name. Accept both
     and compose the model's `Table.Field` form.
   - Object moves clamp `Top` to the section's height minus the object's height, as the SAP
     path does; mirror it.
   - `SubreportParameters` is declared and documented as honoured but nothing reads it. Either
     pass the values through (the converter already writes the subreport's `<Parameters>`
     links from the model, so a value override is a model edit before conversion) or remove
     the property until Stage 3. Do not leave it silently ignored.
   - Object and table name matching is case-insensitive in CrystalCmd, and a bad key logs and
     continues; `RuntimeOverrides` must be as tolerant, with each ignored key reported back.
4. **Export formats.** `ExportFormat` has only `Pdf`. The engine also renders CSV, Excel
   (2007 and data-only) and RTF, which cover CrystalCmd's CSV, Excel, ExcelDataOnly and
   RichText. Three of its formats have no equivalent and stay with the SAP backend:
   `CrystalReport` (returns the `.rpt` itself), `TEXT`, and `WordDoc` (the engine's `Word`
   output type has no renderer behind it and falls through to HTML). Note that Excel comes
   out as `.xlsx`, not the `.xls` the SAP runtime writes.
5. **Parameter coercion.** CrystalCmd coerces JSON values by the parameter's declared type
   (booleans from 0/1, dates parsed invariant-first then current-culture, numbers int-or-
   decimal, a missing required parameter set to empty with a warning). `RptEngine` hands a
   `Hashtable` to `RunGetData` and lets the engine parse; add the same coercion in front of
   it, with tests for each rule, so the two backends read one request the same way.
6. **Release mechanics.** `Directory.Build.props` already says 0.1.0. Publish the way the
   sibling repositories do: a `v*` tag packs and drafts a GitHub release, and publishing
   the release pushes to nuget.org with Trusted Publishing (OIDC and `vars.NUGET_USER`; no
   stored API key, and a one-time policy on nuget.org naming this repository and
   `publish-nuget.yml`). Add a "Packages" section to the README naming each package and
   what it is for, and a short release note drawn from BACKLOG. A 0.x version says
   "preview" on its own.

**Exit criteria.** `dotnet pack` yields Model, Parser, Converter, Runtime, RptEngine and Cli.
A .NET 10 console that references only the `RptEngine` package renders a report with pushed
data and no network access, in every supported format. All suites green, scans at 0 errors
with the control still flagging 87 of 88.

**Size:** one to two days. Items 1 and 2 are the ones that matter most; 3–5 can follow in a
0.1.x if the tag is wanted sooner.

## Stage 2 — an opt-in backend in CrystalCmd

**Goal:** a CrystalCmd deployment can route a request to this engine, and gets the SAP
result for anything this engine cannot serve yet. Existing users see no change unless they
opt in.

**Design.** CrystalCmd's worker is .NET Framework 4.8 and calls the SAP `ReportDocument`
directly; there is no renderer interface. This engine needs .NET 10. So the backend is a
**separate .NET 10 worker on the same work queues**, not a switch inside the existing worker:
it needs no Wine and no SAP install, deploys as its own container, and the API (already
.NET 10) decides where each request goes.

1. **Interfaces in `Common`.** Extract `IReportExporter` (template path + `Data` → bytes,
   extension, mime type) and `IReportAnalyzer` into the multi-targeted `Common` project. The
   existing `Exporter` and `CrystalReportsAnalyzer` implement them; `ExportQueue` and
   `HealthCheckTask` take them by interface. No behaviour change.
2. **The new worker.** A `.NET 10` project referencing `WorkQueues`, `Common` and the
   `Majorsilence.Crystal.RptEngine` package, with:
   - the `Data` → `RuntimeOverrides` translation: the CSV table format (header row, type
     row, quoted rows, blob columns as hex or base64) — port `CsvReader` into `Common`, it is
     pure code; table lookup by name or 1-based index, case-insensitive; `EmptyDataTables`
     built from the analysis's column list; `MoveObjects` → `MoveObjectOverride`;
     `ExportAs` → `ExportFormat`; every failure logged and skipped, as the SAP path does;
   - the analyzer channel: `ReportAnalysis` → `FullReportAnalysisResponse` (the shapes were
     designed to mirror each other; this is a mapping, not new analysis);
   - its own health check, rendering a bundled sample report every minute.
3. **Routing in the API.** Three inputs, in priority order: a per-request `Backend` field on
   `Data` (`Crystal`, `RptEngine`, `Auto`), a server setting for the default, and for `Auto`
   a parse of the template with `Majorsilence.Crystal.Parser` at enqueue time (parsing the
   whole private corpus takes seconds, so this is cheap) that applies the **serviceable
   rule**: one table, no subreport that reads its own table, no cross-tab or chart, and a
   supported export format. Serviceable requests go on a second queue the new worker
   consumes; everything else goes where it goes today. A deployment with only the new worker
   answers an unserviceable request with a clear error naming the rule it failed.
4. **Container image.** .NET 10 runtime, SkiaSharp's Linux native assets and fontconfig, and
   fonts: the SAP image installs a full font set through winetricks, and the visual work in
   this repo depends on Arial and Verdana metrics. Measure the substitutes (Liberation and
   DejaVu are the usual candidates) before choosing; a metric-compatible set is a fidelity
   decision, not a packaging one.
5. **Acceptance corpus.** CrystalCmd has 7 sample `.rpt` files and end-to-end scenarios that
   build their data in code (datasets, parameters, subreport parameters, subreport tables,
   empty subreport table). Render each scenario through both backends and compare the PDFs
   with the same ink-agreement measure the visual suite uses, recording baselines. The
   serviceable rule gets its own tests: one template per shape, asserting which queue it
   lands on.
6. **Documentation.** A README section in CrystalCmd, "Alternative backend (preview)", stating
   the rule, the routing inputs, the format differences and that fidelity is measured on the
   acceptance corpus, not guaranteed.

**Exit criteria.** The end-to-end scenarios pass on the new worker for serviceable shapes and
route to the SAP worker for the rest. The image builds and runs without the SAP runtime. The
acceptance baselines are recorded and the visual suite here is unchanged.

**Coverage at the end of Stage 2:** 50% of the private corpus, 15 of 88 public reports and 40
of 114 third-party ones are serviceable by the rule.

**Size:** one to two weeks, most of it in the translation layer and the acceptance run.

## Stage 3 — subreport data and multi-table reports

**Goal:** the serviceable rule relaxes to cover the two shapes that hold the other half.

1. **Subreport data (unlocks 22% of the private corpus).** `RptEngine` pushes only to the
   main report's `DataSet1`. No engine change is needed: the engine raises
   `SubreportDataRetrieval` as each subreport is about to fetch its data, with the report
   switched to the subreport's definition, and a table set on its dataset then is what it
   prints (a spike printed pushed rows through a converted subreport, with no errors). So
   `RuntimeOverrides` gains `SubreportData` and `EmptySubreportDataTables`, `ReportEngine`
   handles the event, the CrystalCmd translation fills them, and the visual suite's one
   permanent skip (a subreport's second page) becomes measurable once it has a fixture.
   A few days, with no Reporting release.
2. **Multi-table main reports (unlocks 38%).** Two parts, the first time-boxed:
   - *Decode table links.* The file records which fields join which tables and how. Measure
     it through the Crystal runtime's own object model (the same way the formula semantics
     in BACKLOG were established) and locate it in the file by varying one link at a time.
     Two to three days; if it does not fall out in that time, stop and record what was found.
   - *Join in memory.* Build `DataSet1` from the caller's per-table `DataTables` using the
     decoded links (inner and left-outer on equal keys, in link order). This forces a
     converter change that is worth making anyway: the flattened dataset names fields by
     column only, so two tables with an `ID` column collide; qualify `DataField` by table.
     That touches every converted report and is checked by the visual suite.
   - Size unknown until the decode is done; the join itself is a few days.
3. **Re-measure and relax the rule.** After each part lands, re-count the shapes, widen the
   routing rule, and extend the acceptance corpus with one template per newly serviceable
   shape. Charts and cross-tabs stay routed to the SAP worker until they have visual fixtures.

## Tracking and keeping this current

**One issue per item, in the repository whose code changes.** Three trackers are involved:
[majorsilence.crystal](https://github.com/majorsilence/majorsilence.crystal/issues) for the
parser, converter and `RptEngine`; [CrystalCmd](https://github.com/majorsilence/CrystalCmd/issues)
for the worker, routing, image and acceptance run; and
[Reporting](https://github.com/majorsilence/Reporting/issues) for engine changes. An item
that spans two repositories gets an issue in each, cross-linked. Each issue carries the
item's text from this document as its body, its exit criterion as the definition of done,
and a `roadmap` label so the open set can be listed in one query per tracker.

| Item | Repository | Issue |
|---|---|---|
| 1.1 Package `RptEngine` | majorsilence.crystal | [#25](https://github.com/majorsilence/majorsilence.crystal/issues/25), done |
| 1.2 Render offline (converter option, engine skip flag, scan tool) | majorsilence.crystal | [#26](https://github.com/majorsilence/majorsilence.crystal/issues/26), done |
| 1.3 Override parity (`CanGrow`, sort, move clamp, `SubreportParameters`, tolerant keys) | majorsilence.crystal | [#27](https://github.com/majorsilence/majorsilence.crystal/issues/27), done |
| 1.4 Export formats | majorsilence.crystal | [#28](https://github.com/majorsilence/majorsilence.crystal/issues/28), done |
| 1.5 Parameter coercion | majorsilence.crystal | [#29](https://github.com/majorsilence/majorsilence.crystal/issues/29), done |
| 1.6 Release mechanics (packages section, release note, tag) | majorsilence.crystal | [#30](https://github.com/majorsilence/majorsilence.crystal/issues/30), done at the `v0.1.0` tag |
| 2.1 `IReportExporter` / `IReportAnalyzer` in `Common` | CrystalCmd | [#52](https://github.com/majorsilence/CrystalCmd/issues/52), done ([PR 58](https://github.com/majorsilence/CrystalCmd/pull/58)) |
| 2.2 The .NET 10 worker | CrystalCmd | [#53](https://github.com/majorsilence/CrystalCmd/issues/53), done ([PR 59](https://github.com/majorsilence/CrystalCmd/pull/59)) |
| 2.3 Routing and the serviceable rule | CrystalCmd | [#54](https://github.com/majorsilence/CrystalCmd/issues/54), done ([PR 60](https://github.com/majorsilence/CrystalCmd/pull/60)) |
| 2.4 Container image and fonts | CrystalCmd | [#55](https://github.com/majorsilence/CrystalCmd/issues/55), done ([PR 64](https://github.com/majorsilence/CrystalCmd/pull/64)) |
| 2.5 Acceptance corpus | CrystalCmd | [#56](https://github.com/majorsilence/CrystalCmd/issues/56), done ([PR 61](https://github.com/majorsilence/CrystalCmd/pull/61)) |
| 2.6 Documentation | CrystalCmd | [#57](https://github.com/majorsilence/CrystalCmd/issues/57), done ([PR 65](https://github.com/majorsilence/CrystalCmd/pull/65)) |
| 3.1 Subreport data: engine registry | Reporting | not needed: the engine's `SubreportDataRetrieval` event serves |
| 3.1 Subreport data: `RuntimeOverrides` and translation | majorsilence.crystal, CrystalCmd | [#8](https://github.com/majorsilence/majorsilence.crystal/issues/8), [CrystalCmd #67](https://github.com/majorsilence/CrystalCmd/issues/67), done ([CrystalCmd PR 69](https://github.com/majorsilence/CrystalCmd/pull/69)) |
| 3.2 Decode table links (time-boxed) | majorsilence.crystal | [#35](https://github.com/majorsilence/majorsilence.crystal/issues/35), done under #36 |
| 3.2 In-memory join and table-qualified `DataField` | majorsilence.crystal | [#36](https://github.com/majorsilence/majorsilence.crystal/issues/36), done |
| 3.3 Re-measure and relax the rule | CrystalCmd | [#68](https://github.com/majorsilence/CrystalCmd/issues/68) |

**The issues are created before a stage starts**, not all at once: Stage 1's now, Stage 2's
when Stage 1 is tagged, Stage 3's when Stage 2's exit criteria are met. That keeps each
tracker's open set to what is actually next, and lets what was learned in one stage reshape
the next stage's issues before they are written.

**This document changes in the same pull request as the code.** A pull request that closes
an item fills in its issue link above, marks the row done, and adds or points at the
BACKLOG entry that records what was measured. A pull request that changes the plan (a size
estimate, a rule, a dropped or added item, a coverage count) edits the relevant paragraph
and notes the date and reason in the change log below. Reviewers treat a stale roadmap as a
defect in the pull request, the same as a missing test.

**The counts are re-measured at each stage's end** (the shape table in "Where things stand",
the coverage line in Stage 2, the percentages in Stage 3) against all three corpora, and the
serviceable rule is restated from the new numbers. A count that moved without a code change
is a sign the measuring tool changed and gets its own BACKLOG note.

**Change log**

- 2026-09-30: first version; the three stages and their sizes, from the shape counts of the
  three corpora and a survey of CrystalCmd's request contract and hosting. Stage 1's six
  issues created (#25–#30); Stage 3.1's `RptEngine` side is the existing #8.
- 2026-09-30: 1.1 and 1.2 done (BACKLOG: "RptEngine is packaged, and renders offline").
- 2026-09-30: 1.3 done (BACKLOG: "Runtime overrides match the request contract they
  mirror"). Found on the way: the parser does not decode a report's own sort order, so
  `SortFields` is empty for every parsed report; the sort override fills the gap for a host,
  and the decode is #31.
- 2026-09-30: 1.4 done (BACKLOG: "Four more export formats"). Corrected: the engine has no
  Word renderer, so `WordDoc` is a third format with no equivalent, not a covered one.
- 2026-09-30: 1.5 done (BACKLOG: "Parameter values are read by their declared type"). Only
  1.6, the release itself, remains in Stage 1.
- 2026-09-30: 1.6: README packages section and CHANGELOG.md written. Publishing changed
  from an API-key secret to the two-step flow Majorsilence.Forms and Reporting use: a `v*`
  tag drafts a GitHub release with the packages, and publishing the release pushes them to
  nuget.org with Trusted Publishing (`vars.NUGET_USER`, no secret). `v0.1.0` is tagged from
  this commit. Stage 1 is complete when the packages are listed on nuget.org; Stage 2's
  issues are created in CrystalCmd's tracker then.
- 2026-09-30: **Stage 1 complete.** v0.1.0 published; the six packages are on nuget.org.
  The tag moved twice first: the unit tests rendered with the System.Drawing engine and
  failed on ubuntu, and the CLI package was 159 MB until its native runtimes were trimmed
  to five and the symbol files dropped (37.5 MB). Stage 2's six issues created in
  CrystalCmd's tracker under a `roadmap` label there.
- 2026-10-01: 2.1 done (CrystalCmd PR 58): the two interfaces in `Common`, the Crystal
  exporter and analyzer implementing them, the queue and health check taking them. On the
  way, CrystalCmd's SQLite work queue moved to WAL mode with immediate write transactions,
  which was what made its end-to-end tests fail on the Windows runner (PR 51).
- 2026-10-01: 2.2 done (CrystalCmd PR 59): the .NET 10 worker on its own channels, the
  request translator with the serviceable refusals, the shared queue loop and health check.
  Three CI fixes went with it: a Linux job that tests the backend without the SAP runtime,
  the Windows job installing every runtime the client tests target, and build-then-test
  with `--no-build` so a test-launched worker locks nothing. CodeFactor was retired.
- 2026-10-02: 2.3 done (CrystalCmd PR 60): `Data.Backend`, the `Routing:DefaultBackend`
  setting (Crystal unless set), and the serviceable rule applied at enqueue for Auto; an
  explicit RptEngine request the rule rejects is a 400 naming the rule. The end-to-end
  tests launch both workers and prove the routing through the server.
- 2026-10-02: 2.5 done (CrystalCmd PR 61): four serviceable scenarios rendered through the
  server by both backends and compared by ink agreement. First baselines: 9.3%, 0.0%,
  0.0%, 0.0%. The gaps are three converter defects raised here: #32 (a Details section
  with no database fields is dropped), #33 (paper size read as Letter where Crystal has
  A4), #34 (section background colour not carried). They come before 2.4, since the font
  decision is measured with this corpus and three of its four scenarios render blank today.
- 2026-10-02: #32 fixed here (BACKLOG: "A report that reads no table prints its Details
  once"). Two of the three blank scenarios print; the third, a subreport that reads no
  table, waits on an engine change (the engine runs a subreport only when a dataset returns
  a row), raised in the Reporting tracker. The acceptance baselines rise when a release
  carries this and CrystalCmd takes it.
- 2026-10-05: #34 fixed here (BACKLOG: "A section's background colour is drawn across its
  band"). The dataset scenario's grey title band now prints.
- 2026-10-05: #33 fixed here (BACKLOG: "A report that follows its printer prints on the
  paper the printer settings name"). The dataset sample names no paper, so Crystal prints
  it on its printer's: A4 against an A4 printer, Letter against a Letter one. Such a
  report is now marked, and `RuntimeOverrides.PrinterPaper` lays it out on the paper a
  host says its printer holds. The dataset scenario's page sizes agree once CrystalCmd's
  RptEngine worker passes its Crystal host's paper, a CrystalCmd follow-up.
- 2026-10-05: follow-ups raised: [CrystalCmd #62](https://github.com/majorsilence/CrystalCmd/issues/62)
  (pass the Crystal host's paper to the worker), [Reporting #345](https://github.com/majorsilence/Reporting/issues/345)
  (draw a subreport that has no rows) and [Reporting #348](https://github.com/majorsilence/Reporting/issues/348)
  (find installed fonts on Linux). 2.4 and 2.6 are in review in CrystalCmd. v0.2.0 is
  prepared, carrying #32, #33 and #34; the acceptance baselines rise once CrystalCmd takes it.
- 2026-10-05: v0.2.0 released. Measured through CrystalCmd's acceptance corpus before
  taking it: the dataset scenario rises from 15.9% to 89.5% and the parameters scenario
  from 0.0% to 95.7%. The plain scenario stayed at 0.0%, its one line printed 0.42in too
  high because an empty page header took no room; fixed here for v0.2.1. The subreport
  scenario waits on Reporting #345.
- 2026-10-06: **Stage 2 complete.** 2.4 done (CrystalCmd PR 64): the worker's container
  image, plain .NET 10 with no Wine or SAP runtime, built and run in CI. It installs no
  fonts, because on Linux the engine draws Arial and the other common faces in its own
  metric-compatible Liberation fonts whatever is installed; real Arial would be 2 to 8
  points closer to Crystal, an engine change raised as Reporting #348. 2.6 done (CrystalCmd
  PR 65): the README section and release zips for the worker. CrystalCmd takes v0.2.1
  (PR 66, closing CrystalCmd #62 with a `Worker:PrinterPaper` setting). The acceptance
  comparison now lines the two pages up within 12pt before scoring, because Crystal's page
  moves with the host printer's margins: against Microsoft Print to PDF the same template
  printed 6pt right of and below where a laser printer put it, which dropped a one-line page
  from 88% to 7% on CI. Aligned baselines: 96.8%, 95.0%, 95.7% and 5.9% (the subreport
  scenario still waits on Reporting #345). Exit criteria met: the end-to-end scenarios pass
  on the new worker and route to the SAP worker for the rest, the image runs without the
  SAP runtime, the baselines are recorded, and the visual suite here is unchanged.
- 2026-10-06: Stage 3's issues created: [#8](https://github.com/majorsilence/majorsilence.crystal/issues/8) (now 3.1, here) and
  [CrystalCmd #67](https://github.com/majorsilence/CrystalCmd/issues/67) for subreport data, [#35](https://github.com/majorsilence/majorsilence.crystal/issues/35) and
  [#36](https://github.com/majorsilence/majorsilence.crystal/issues/36) for multi-table reports, [CrystalCmd #68](https://github.com/majorsilence/CrystalCmd/issues/68) for
  relaxing the rule. 3.1 needs no Reporting change after all: the engine's
  `SubreportDataRetrieval` event already lets a caller set a subreport's data as it loads.
- 2026-10-06: 3.1's half here done (#8, `RuntimeOverrides.SubreportData`); CrystalCmd #67
  takes it once v0.3.0 is released. 3.2's time-boxed decode (#35) stopped early, on a clear
  result (BACKLOG: "Table links are not in any stream this parser can read"): the links are
  in QESession, the one stream that opens with neither the Contents scheme nor any
  compression. Inferring links by matching field names is no substitute, matching the
  runtime's links in 46 of 764 private multi-table reports. So #36's join needs its links
  from the caller (a host that renders with Crystal already has them), or multi-table
  reports stay with the Crystal worker. That choice is open.
- 2026-10-09: Reporting 26.0.7 shipped [#345](https://github.com/majorsilence/Reporting/issues/345)
  and [#348](https://github.com/majorsilence/Reporting/issues/348), and the engine moves to it
  for v0.3.2. A subreport with no details now prints with no data, so CrystalCmd's
  subreport-parameters scenario draws its subreport. A subreport with a Details section
  still prints nothing without rows, since the engine follows SSRS there; that is a
  converter change here. **3.1 done:** CrystalCmd #67 merged on v0.3.1 (CrystalCmd PR 69),
  so a request pushing a subreport its table goes to RptEngine.
- 2026-10-09: **3.2's half here done.** The QESession stream decrypts, and its dependency
  graph carries every table, alias, column and link, matching the runtime's own in all three
  corpora (BACKLOG: "Table links decoded from QESession, and joined in memory"). So #36's
  join takes its links from the file, and the open choice above is settled without asking
  the caller for them. `RuntimeOverrides.TableData` takes one table per Crystal table, and
  every `DataField` is qualified by its table. The visual suite and the three corpora's
  scans are unchanged. CrystalCmd's translation still refuses a request with more than one
  table; letting those through is 3.3's (CrystalCmd #68).

## Risks and dependencies

- **Fidelity is the gate for "default", not for "opt-in".** Explicit routing means a wrong
  render reaches only callers who asked for this backend. Promoting it to the default for a
  shape needs the acceptance baselines at or above the visual suite's numbers for that shape.
- **Fonts on Linux** can move every line; measure before shipping the image.
- **Excel differs in kind** (`.xlsx` against `.xls`); callers that depend on the old format
  keep the SAP route.
- **Three of CrystalCmd's formats** (`CrystalReport`, `TEXT`, `WordDoc`) have no equivalent
  here.
- **Stage 3.1's engine fix shipped** in Reporting 26.0.7, and Stage 3.2 needed no engine
  change: the join happens before the data reaches it.
- **Stage 1.2 also fixes the private render scan**, which today attempts the connections the
  reports name; it should not.
