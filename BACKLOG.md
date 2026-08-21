# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

### Private-corpus campaign opened: 2,324 real-world files, 1,743 → 941 fatal in three fixes

**In progress.** With the public corpus at 0/88, the same scan was pointed at
the 2,324-file private corpus (local-only; never referenced in commits).
First full run crashed the *process* partway — an uncatchable
StackOverflowException — traced to `qrdetail.rpt`: a report pairing a running
total `#X` with a display formula also named `X` emitted duplicate
`<Field Name="X">` entries, the engine dropped the second (the running
total), and the surviving formula referenced itself; the engine's
`IsConstant`/`ConstantOptimization` recursion then ran A→A forever. Fixed
converter-side: formulas whose names collide with running totals are skipped
(the RunningValue entry is the real value carrier), and any *surviving*
direct self-reference in a compound expression degrades the field to `=""`
(longer cycles would need a graph pass; none seen).

With the scan completing: **1,743 of 2,324 fatal**, cut to **941** by two
follow-ups: (1) `CrystalVarDecl` now also recognizes Basic-dialect
declarations — `Shared CustomerAddress as string`, `Global GTB7 as double`,
`Dim x as ...` — which this corpus uses almost exclusively (~1,700
occurrences; 566 files cleared); (2) engine-side `VBFunctions` additions:
`Abs(object)`, `StrDup` (either argument order — VB's (count, char) vs.
Crystal ReplicateString's (text, count)), cheque-style `ToWords` (English
words + "NN / 100"), and object-typed `Year`/`Month`/`Day` overloads for
String-typed field arguments (236 more files cleared).

**Numeric-usage inference round (1,177 → 925):** the `-*/` adjacency rule
gained a paren-tolerant form plus a `+`-adjacency rule for formulas with no
string literal and no `&` (Crystal's `+` is only concatenation when a string
is in reach), and now also covers *parameters* — including declared ones
(`ParameterField.DataType` made settable): every observed
String-typed-but-subtracted parameter (page numbers, years) is numeric in
all its uses. **Negative result worth keeping**: extending the same
inference to *declared* String columns was tried and measurably regressed
both corpora (public 0→1, private 938→995) — a column used numerically in
one formula is routinely a genuine string in another, so the retype broke
the string uses. Reverted same-session; the honest fix for the remaining
`{T.F4} - {T.FE4}` cluster (~350 errors) is a `CDbl()` wrap at each
arithmetic reference site, which needs field-type knowledge inside
`FormulaTranspiler` — future work.

**`GroupName` unwrap round (925 → 920 → 917):** Crystal's `GroupName({field})`
(and the 2-arg date-grouping form, `GroupName({field}, "daily")`) is simply
"the current group's value for this group-by field" — in a grouped RDL row
context that *is* the field itself, so there is no engine function to call
for it. Fixed at both emission sites: `RdlEmitter.EmitFuncCall` unwraps a
`GroupName` call node to its first argument's emitted expression (223 error
occurrences resolved this way), and `FormulaTranspiler`'s regex fallback
(used when the grammar can't parse the surrounding statement) does the same
textually, dropping any second argument. First pass only handled the 1-arg
form and left 3 files with the 2-arg date-grouping variant unresolved
(`GroupName(Fields!x.Value, "daily")`); extended both sites to accept either
arity. Verified: 843 tests green, public corpus 0/88, private corpus fatal
file set strictly improved with zero regressions (920 → 917, diffed by
filename, not just count).

**Missing-function round (917 → 795):** a full inventory of every
"Function X is not known" in the scan — rather than fixing them one at a time —
split into three causes and cleared all but nine occurrences in two passes.

1. *Existing functions the parser could not bind.* `XmlUtil.GetMethod` resolves
   by **exact** runtime argument type, so a field or parameter whose type was
   never inferred arrives as `Object` and the String-typed overload never
   matches — the call then reports as *unknown* rather than as a type error.
   `Trim` alone was 144 occurrences this way. Added Object-typed mirrors for
   `Trim`/`LTrim`/`RTrim`/`Mid`/`InStr`/`Replace`/`CDate`, the same reasoning as
   the earlier `Abs(object)`/`IsNothing(object)` additions.
2. *Functions Crystal has that VB.NET does not.* `Val` (81), `Now` (30 — VB
   spells it a property, so it is invisible to reflection until declared),
   `NumericText` (17), `Fix` (12), `DateValue` (9), `ChrW` (9), `Remainder`,
   `Floor`, `Ceiling`, `AscW`, `IsDateTime`, `Int`, `CDateTime`. Crystal's
   `Fix`/`Floor`/`Ceiling` take an optional second argument (decimal places for
   `Fix`; a *multiple* to round to for the other two), so both arities exist.
   Crystal's `DateTime()` synonym is mapped to `CDateTime` converter-side rather
   than declared: a method named `DateTime` would shadow the type of the same
   name for every member access in `VBFunctions`.
3. *3- and 4-argument `CStr`/`ToText`* — `CStr(x, 5, ",", ".")` (places,
   thousands separator, decimal separator) and `CStr(x, 0, "")` for an ungrouped
   integer, alongside the existing format-string form.

**`NthLargest` → `Max` (the round's largest single win):** all 238 occurrences
are `NthLargest(1, …)` — the largest value, which is exactly `Max`. No aggregate
machinery was needed; the emitter rewrites the literal-1 form and drops the
optional third *group-by field* argument for the same reason Sum's scope is
dropped above (RDL scope arguments accept only DataSet names). A non-literal-1
N is deliberately left untranslated so it surfaces rather than silently
reporting a wrong number — none occur today. `PreviousValue` maps to the
engine's existing `Previous` aggregate.

Verified: 843 tests green, public corpus 0/88, private corpus 917 → 795 with
the fatal file set diffed by filename (122 files fixed, **zero** regressions).
Because "the file stopped erroring" does not prove a function *computes* the
right answer, the new semantics are additionally pinned by 27 direct unit tests
in the engine repo (`ReportTests/VBFunctionsCrystalTest.cs`) — the negative-value
split between `Fix` and `Int`, the round-to-a-multiple second argument, `Val`'s
leading-prefix rule, and the separator-swap case (`CStr(x, 2, ".", ",")` →
`1.234,57`) that a naive replace would render as `1,234,57`.
Deliberately deferred: `Split` (7 — returns an array, and the parser's
return-type handling makes that a real risk for so few uses) and
`GroupingLevel` (2).

**Placed parameter fields (795 → 659, the campaign's single biggest fix):** the
`PageNumber1 expression '=Fields!Page_Number.Value' … Field not found` cluster
turned out to have nothing to do with page numbers or special fields.
`BuildKnownFieldsMap` mapped over *every* `report.Fields` entry, including
`ParameterField` — but `WriteDataSets` writes only database, formula and
running-total fields. So any object placed on a parameter resolved through that
map and emitted `Fields!X.Value` for a column the DataSet never declares, and
the report failed to render. The same report would meanwhile emit a perfectly
correct `Parameters!Page_Number.Value` from its *formulas*, which take a
different path — which is what made the generated RDL the fastest way to see it.

Fixed by excluding parameters from the known-fields map and resolving them
through a new `BuildParameterMap` to `Parameters!X.Value`, built with the same
SAP-wrapper stripping the `ReportParameter` declaration uses so the two match.
Text references get it too: Crystal writes them `{?Name}`, where the `?` is
reference syntax rather than part of the declared name.

Parameters are resolved *ahead* of the special-field list, because a report that
declares a parameter named "Page Number" means its own parameter — the
special-field list is a fallback for names nothing else resolves. The model has
no discriminator to separate a placed parameter from a placed special field of
the same name, so this ordering is a judgement call; the public corpus staying
at 0/88 is the evidence it is the right one. Both rules are pinned by tests.

**`ProperCase` never worked (632, −27 files):** the emitter appended
`VbStrConv.ProperCase` to every `StrConv` call, but the engine has no `StrConv`
*and* no `VbStrConv` enum — its expression parser reads a bare dotted name as an
identifier, so every one of these failed with "VbStrConv.ProperCase is an
unknown identifer". Fixed on both sides: `StrConv(object, object)` added to the
engine, and the converter now emits VB's plain numeric conversion code (3 =
proper case) instead of an enum reference nothing can resolve.

**The arithmetic-on-strings cluster was our own degrade placeholder (617, −15
files; 302 → 78 error occurrences).** This is a correction to the plan recorded
above: the fix was expected to be a `CDbl()` wrap for declared String columns,
but classifying the actual operands across a 25-file sample found **109 degraded
fields and zero String-typed columns**. A formula that cannot be translated
degrades to `=""`; when another formula then subtracts it, the engine rejects
that whole expression. Every degrade path funnels through the same empty-string
result, so one guard covers them all: when the corpus shows a formula being
referenced adjacent to an arithmetic operator, its placeholder becomes `=0`
instead. The value is equally unknown either way, but the arithmetic stays
well-typed. The remaining 78 are a genuine long tail (String-typed *parameters*
in arithmetic, date subtraction), not one cluster.

**Aggregates in Grouping context, and empty header bands (558, −69 files):** the
`PageBreakCondition` guard was a two-name blacklist (`RowNumber`, `CountRows`),
but Crystal's page-break formulas reach for aggregates constantly — "break when
the next row starts a new customer" transpiles to `Next()`. Widened to the
engine's full aggregate set. Separately, `WriteHeaderOnlyTable` always emitted a
`<Header>` even when there was no field-bound page-header content to put in it,
producing an empty `<TableRows/>`; the "at least one TableRow" rule its own
comment cites for `Details` binds to `Header` too. Both clusters went to zero.

**Verification-method fix, found the same round**: the fatal-set diff matched
only `FATAL` lines, so the `EXCEPTION` category was invisible to it. Ten files
looked like regressions until the scans showed all ten had previously been
*crashing* with a NullReferenceException (an empty `TableRows` reaching the
engine) and had merely become ordinary errors. Crashes fell 31 → 16 that round.
**Compare fatal *and* exception counts** — a fix that turns a crash into a
reported error looks like a regression to a fatal-only diff.

**Subreport data scope and unresolvable field references (494, −71 files, −7
crashes):** a `Subreport` that passes a parent *field* as a parameter emits
`Fields!` expressions, which the engine resolves by walking ancestors for a
DataRegion — so it has to sit inside the table, exactly like the field-bound
header sections `NeedsTableRouting` already routes there. Its subreport clause
covered only PageHeader/PageFooter, so a ReportHeader/ReportFooter holding
*nothing but* a field-bound subreport matched none of its three tests and stayed
at Body level, failing with "Field 'X' not found" even though the field was in
the DataSet. The parameter-binding logic is now shared between the routing
decision and the emission (`SubreportParameterBindings`) so the two cannot drift.

Routing those subreports in made the engine actually *compile* child reports it
had been skipping, which surfaced two latent bugs in them, both now fixed: a
transpiled formula referencing a name that exists nowhere in its DataSet (the
residual "Field not found" cluster — degraded like the self-reference guard
does, since no faithful translation exists), and `Sum(x, Fields!y)` reaching the
engine as "scope must be a constant". The latter was supposedly already handled,
but the guard tested the argument's *node shape*; it now tests the *emitted*
argument, because the engine's rule is that a scope must be a constant, so any
second argument that comes out as a field reference is invalid however it was
written. That cluster fell 94 → 70 occurrences.

**The empty-`ReportItems` hole, and what it actually was (481, −13 files):** the
regression above (`taxcert_Lunenburg_V3.rpt`, a child emitting an empty
`<Body><ReportItems />`) traced to a guard/writer disagreement, but not in
`HasRenderableContent` as first suspected. `hasTable` was decided on
`detailObjects.Count > 0` — *any* object in Details — while `WriteDetailsTable`
returns without writing anything unless there is a column to build from: a placed
field object, a database column, or an image. A Details section holding only
static text satisfies the first and not the second, so the Body committed to a
table that was never emitted. Both sides now ask one `DetailsTableHasColumns`
predicate, so they cannot drift. This only ever bites reports with no database
fields at all, which is why it stayed hidden. Cleared the cluster entirely
(13 → 0 occurrences, 13 files) with no regressions — and closes the regression
the subreport-routing round introduced.

**Database-bound images need the data region too (316, −165 files):** the
campaign's single largest cluster — 304 occurrences across 173 files, almost all
a barcode column placed in a PageFooter. A database-bound image's `Source` *is* a
field reference (`WriteImageSourceElements` emits `Fields!X.Value`), so it needs
a DataRegion ancestor exactly like a placed FieldObject, but `NeedsTableRouting`
tested only field objects, braced text and subreports. Adding images to it fixed
124 files.

The remaining 54 were a second, distinct shape: an image in a *group* section
while a table exists. Group sections aren't routed then — their content becomes
TableGroup rows — but objects the group row had no free cell for fell to a
"leftovers" path that emits them as positioned Body items specifically so they
are not silently dropped. For a database-bound image that placement is a
guaranteed fatal, so the image was dropped instead as the lesser loss. The round
below replaced that concession with the real fix and recovered the images.

### Surplus group sections never reached the band that owned them

**276 fatal, 1 crash (was 307/8) — and the subreport scope cluster to zero.**
Crystal splits a single group level across *several* sections (one strip per
subreport, say: 6 GroupHeaders and 4 GroupFooters over 2 group levels), but the
band writer took one section per group index, so every surplus section's content
fell to the free-form Body path — where a database-bound image or a field-bound
subreport cannot resolve its `Fields!` reference, however correct the DataSet is.
That single mapping gap was behind the whole remaining "Subreport*N* expression …
Field not found" cluster (84 → **0**) and the barcode images the round above had
to drop, which now render inside the table again.

Two pieces: `WriteQueuedExtrasRows` emits objects a band's own cells had no room
for as extra rows *in that band*, and each band now also walks the sections
sharing its `GroupLevel` beyond the one it mapped. Both keep the objects inside
the DataRegion, which is the whole point — and mark them consumed, so the Body
leftovers path stops seeing them.

Also routes any section whose *suppress formula* references fields: that formula
becomes the `Hidden` expression on every item the section emits, so it needs the
same data scope the content does. **It must also require the section to have
content** — routing an empty section emits a free-form row whose Rectangle holds
nothing, which is itself fatal ("At least one item must be in the ReportItems").
That mistake cost 27 files on the first attempt and is the third instance this
campaign of the same failure mode: *a widened guard that admits something the
writer then declines to write*. Worth checking for directly whenever a routing
or emission predicate grows.

### Basic-syntax formulas were never being recognised

**168 fatal, 296 Severity-8 occurrences (was 276/464) — 108 files.** The parser
sets `Syntax = FormulaSyntax.Crystal` unconditionally; the dialect flag is not
decoded from the binary. So `NormalizeBasic` had **never run on a real file**, and
every Basic-syntax body reached the engine as raw text. Basic returns a value by
assigning to a pseudo-variable `formula`, inside `If … ElseIf … Else … End If` —
none of which RDL has — so these now become nested `IIf` calls, with the dialect
detected from the body's own markers (`formula =`, `End If`) rather than the flag.
Whole-line apostrophe comments are dropped too, and a body whose every assignment
was commented out degrades to an empty string instead of leaking an `if … then`
skeleton with no branches.

**This round also produced the campaign's only silent-corruption bug, and the
file-count metric hid it.** Detection first ran over the *whole* body — but these
reports routinely keep an older Basic version of a formula commented out with
`//`, and its `End If` classified the live Crystal-syntax body as Basic. That ran
apostrophe-comment stripping over Crystal code, where an apostrophe is the
*string delimiter*: every branch value beginning a line — `'In Account with: ' +
trim({x})` — was deleted as a comment. It cost 69 new error occurrences while the
fatal **file** count still improved by 108, because the affected files were
already failing for other reasons. Detection now judges only comment-stripped
code, and the shape is pinned by a test.

**So track Severity-8 occurrence totals alongside file counts.** File counts
cannot see a new error inside an already-failing file, which is exactly where a
correctness regression hides: 464 → 366 → **296** occurrences across this round
made the damage and its repair obvious where 276 → 168 → 168 files did not.

### Section formulas are reference sites too

**107 fatal, 172 occurrences (was 168/296) — 61 files.** Both numeric-usage
inferences — the parser's, which types a String parameter as `Float64` when it is
used arithmetically, and the converter's, which picks `0` over `""` for a
degraded formula used as a number — scanned only *formula fields*. But a table's
suppress and page-break hooks are formulas too, and they are exactly where
page-count arithmetic lives (`{@rowcount} - ({?PerPage} - 1)`). A parameter or
formula used numerically *only* there was never inferred as numeric. The parser
now scans `FormulaTexts`, which holds every formula including the section hooks
the field list deliberately skips as internal, and the converter scans the
sections' resolved formulas alongside the field texts. Numeric-operator errors
fell 87 → 25.

Also maps a bare `recordnumber` identifier to `RowNumber()`: Crystal's "Record
Number" special field, spelled without the space when referenced inside a formula
rather than placed as a field (62 occurrences). Both rules are pinned by tests.

### Two field type codes were wrong

**72 fatal, 136 occurrences (was 107/172) — 35 files.** The boolean-context
cluster ("AND/OR operations require both sides to be boolean expressions",
"NOT requires boolean expression") was not a transpiler problem at all: Crystal
value-type code **8 was mapped to DateTime when it means Boolean**, so every
yes/no column reached the engine as a non-boolean and any `{active} And {other}`
failed. Code **15 had no mapping at all** and fell through to String, hiding real
date columns from the date-function overloads.

Both were settled by dumping the raw type codes across a 120-file sample and
reading the *column names* each code carries — the same "check the actual value
rather than infer it" step that has paid off repeatedly here. Code 8 is
exclusively flags (`active`, `approved`, `isEft`, `sendEftEmail`, `namealtered`);
code 15 is exclusively dates (`asOfDate`, `dueDate`, `chqDate`, `changeDate`).
AND/OR errors fell 21 → 3 and NOT 21 → 4.

**Not unit-pinned, deliberately**: `MapCrValueType` is private, no public-corpus
file uses code 8 or 15, and the private corpus cannot be referenced from a
committed test. The evidence above is the record — re-derive it the same way
before changing the table. A public corpus file exercising these codes would be
the thing to add.

### Engine: the aggregate scope scan ran past the end of its own call

**54 fatal, 69 occurrences (was 72/136) — 18 files, and the occurrence total
halved.** `scope must be a constant` (67 occurrences) was an engine bug, not a
conversion one. On meeting an aggregate, `Parser.cs` scans forward for a scope
argument after the first top-level comma — but the scan had no upper bound, so it
ran past the aggregate's own closing paren into the enclosing expression and took
whatever followed *that* comma as the scope. Hence "True function's scope must be
a constant" for `IIf(RowNumber() = CountRows(), IIf(…, True, False), Nothing)`:
the reported "function" is the stray token the scan landed on. A scope argument
can only appear inside the call, so the scan now stops when the paren level goes
negative. Cluster to zero; 288 engine tests still green.

**Not minimally reproduced.** A synthetic report reaching the engine with that
exact expression *passes* without the fix — verified by disabling the fix and
running the candidate test, which is why no test was added rather than one that
cannot fail. The corpus failures are all on `ColumnName`*NN*` expression` items
(report-item expressions, not DataSet field values), so the trigger depends on
parse context that the synthetic case does not reproduce; that is the lead for
anyone constructing a real regression test. Evidence for the fix is the corpus:
67 → 0 occurrences, 18 files, no regressions in either corpus.

### The visual-regression suite was not measuring anything

**The metric could not fail.** It scored mean absolute pixel difference over the
whole page — `1 - totalDiff / (pixels x 765)` — but these pages are **2-9% ink on
white**, so a *pure white image* scores **93.9-98.5%** against all six committed
references, every one above the 85% floor. Those are the same numbers the suite's
own comment cited as evidence that "5/6 land at 93-98% similarity". It would have
passed a completely blank render for every case.

Replaced with **ink agreement**: both images reduced to a non-white mask on a
coarse grid (tolerating the subpixel glyph offsets between two render engines),
scored as intersection over union. A blank render scores 0 by construction. The
real numbers: **0.0% for five of six cases, 1.5% for the sixth.** Our renders
share essentially no content with the references.

**Root cause — no saved data.** The generated DataSet is a SQL query
(`SELECT … FROM [Customer]`) against a data source that does not exist at test
time, so `RunGetData` returns no rows and every data-bound item renders empty
(`Top5USAsubCanada` exports a 1,250-byte, zero-text PDF). The references were
produced by the real engine with a bare `Data()`, which renders the **saved data
embedded in the .rpt** — and nothing in this pipeline extracts that. Until it
does, no amount of layout work can move these numbers. Two ways forward:
implement saved-data extraction (a new binary-format area), or regenerate the
references against a live data source both engines can read.

The suite now asserts against **recorded per-case baselines** rather than a
threshold, so a regression is caught and a genuine improvement fails loudly
telling you to raise the baseline. A missing page is also reported as itself
("our render has 1 page(s); the reference has at least 2") instead of surfacing
as an `ArgumentOutOfRangeException` from the rasterizer.

**One real defect found and fixed along the way**: a subreport in a ReportFooter
was dropped entirely whenever a Details table existed — `freeFormSections` skips
ReportFooter then, and the table's footer band is built by joining the section's
*TextObjects*, so the subreport reached no path at all. That was the whole of
`Top5USAsubCanada`'s missing second page. Routing only *subreports* this way, on
purpose: including charts and images emitted them twice ("Duplicate Grouping
name") because those already reach the output another way.

That fix then exposed two latent bugs, both fixed: literal text beginning with
`=` (a bare `=` used as a separator label) was written straight into a `Value`,
where RDL reads it as "an expression follows" and fails; and a String-typed
*parameter* used as a `Not`/`And`/`Or` operand needs the same boolean inference
the numeric one already had (`Not {?ShowRecInfo}`). AND/OR errors 3 → 0, NOT 4 → 2.

### Saved report data: where it is, and why it is not readable yet

**Investigated, not solved.** Recorded so the next attempt does not repeat it.

*Established:*
- Saved data lives in an OLE stream named `SavedRecordsStream <n>l` — present in
  **74 of the 88** public corpus files (16,756 bytes in `CustomerList`, whose
  reference render is 9 pages of customer rows). Subreports carry their own, at
  `Subdocument <n>/SavedRecordsStream <m>l`.
- It is **encrypted**, not merely compressed: the byte histogram is flat (all 256
  values present, most common 0.53% against 0.39% uniform), no known plaintext
  from the reference render appears anywhere in the .rpt in ASCII or UTF-16LE,
  and raw/zlib/gzip inflate fails at every offset in the first 256 bytes.
- The `Contents` pipeline does **not** carry over. Streams from different files
  share no prefix, so there is no plaintext header. A known-plaintext probe over
  **189 framings** — IVs taken from `Contents` (offsets 0/10/16, both XOR 0x00 and
  0xFF, i.e. including the per-file IV `Contents` itself uses) and from the target
  stream (offsets 0/4/8/10/16/24/34, both XORs), each against ciphertext starting
  at 9 different offsets — produced no hit on `"City Cyclists"` or
  `"Sterling Heights"`, the first data values the reference render shows.

*What the container looks like:*

- The saved data is a **family** of streams, not one: alongside
  `SavedRecordsStream` sit a memo-values stream, a formula-records stream, an
  index stream, and two "spilled fields" streams. Row values live in the records
  stream; long strings and blobs spill into the others, so a complete reader needs
  more than the one stream.
- The records stream is **not a single blob**. It is a series of independently
  encrypted, independently deflated **batches**, each read by seeking to a byte
  offset and taking a length, with those offsets held in the index rather than in
  the stream itself. Per batch the order is: raw seek → decrypt → zlib inflate →
  skip a further byte count *within the inflated output*.
- The cipher and key are the **same ones this repo already implements** for the
  `Contents` stream (`ContentDecryptor`) — only the framing around them differs.
  So no new cryptography is needed, just the right per-stream initialisation
  vector and the batch offsets.

*The one remaining unknown — the initialisation vector these streams use.*
Decrypting the records stream at **every** offset in the first 4096 and inflating
produced no hit for either candidate tried: the per-file IV taken from the
`Contents` header, or an all-zero IV. Both were tested against the real cipher
rather than a reimplementation, so the cipher construction is not in doubt — the
IV is.

*How to close it:* determine the IV empirically rather than by inference — drive
the licensed engine over a corpus file and observe the initialisation vector it
uses for these streams. Everything else in the pipeline above is already
understood well enough to implement once that value is known.

*Cheaper route to the same testing goal:* the licensed engine already on the dev
machine exports `CharacterSeparatedValues` (`CrystalCmd`'s `Exporter`), and with
a bare `Data()` it exports **the saved data** — the same source the reference PNGs
render from. Dumping each corpus report's rows to a committed CSV fixture and
pushing it through `RuntimeOverrides.Data` (already a `DataTable` for the
flattened DataSet, keyed on raw Crystal column names, already applied by
`ReportEngine.ExportAsync`) makes the visual comparison apples-to-apples with **no
new format work**. Decrypting the stream remains the better prize — it would let
any consumer render a .rpt with its own data, no database and no SAP runtime —
but it is not on the critical path for making the suite meaningful.

**Remaining top clusters** (exact counts in the scan output): the numeric residue
(~23 — genuine String columns and date subtraction), `Split` returning an array
(7, deliberately deferred), and a long tail of one- and two-offs. **50 of 2,324
files remain (2.2%), 1 crash, 64 total Severity-8 occurrences.** Verified at every
step: public corpus 0/88, 853 crystal tests + 288 engine tests green, fatal,
exception *and* occurrence counts all tracked.

### Custom functions implemented (tag 335): corpus now 0/88 fatal

**Implemented — the campaign's last item; the public corpus now converts and
renders with ZERO fatal files (from 55/88 at the campaign's start),
deterministic across repeated runs.** Building on the scoping below:
`RptParser.ExpandCustomFunctionCalls` decodes each tag-335 record (name from
the same 118>113 child walk as formulas; source located by scanning for
"Function" XOR 0x76 and decoding until the XOR'd NUL terminator), parses the
`Function ([Optional] TypeVar [range] name [:= default], ...) <body>`
signature — including Optional parameters, whose defaults fill omitted
trailing arguments at call sites — and **inlines** each call in every
formula body: argument text substituted for parameter names word-boundary,
result wrapped in parens, iterated to a fixed point so functions that call
other functions (the `cdExpandRegionAbbreviation` dispatcher →
USA/Canada variants) expand fully. Bodies that *assign* (`:=` outside the
signature) are procedures, not expressions — those calls degrade to their
first argument (identity beats blank for the format-style functions this
shape is; both beat fatal). The souvik file's full Crystal Decisions sample
library (24 `cd*` functions) decodes and round-trips.

Inlining surfaced one last grammar gap: function bodies use parenthesized
*statement blocks* — `then ( select ...; )` with statement semicolons before
the `)` — so the parenthesized primary now accepts a `stmtList` with an
optional trailing semicolon (the emitter already takes a block's value from
its last statement).

Verified: 843 tests green; corpus 2 → 1 → **0** of 88 across the three steps,
deterministic; visual-regression still 5/6 (same single pre-existing
`Top5USAsubCanada` page-2 failure).

### Corpus fatal-error campaign context: custom-function scoping notes (tag 335, XOR-encoded)

**The full-corpus fatal-error campaign ends here at 2 fatal files of 88**
(from 55/88 when the campaign's first scan ran). The string `in` operator was
the last contained fix: Crystal's `{X} in "USA"` is a substring test — added
an `expr In expr` grammar alternative emitting `InStr(rhs, lhs) > 0` beside
the existing `[list]` membership form (cleared `CustomerProfileReport`; the
list form was verified still emitting the `OrElse` chain unchanged).

**The last 2 files (`souvikduttachoudhury__CustomFunctions`,
`benbrahim777__function`) need custom-function extraction — scoped, not
started.** Findings from the binary: a custom function lives in a **tag-335**
record with the same `118>113` inner layout as a tag-119 formula. The payload
is XOR-obfuscated in zones: the name region decodes with **XOR 0x07**
(`Dhidfsbifsb4Tsuni``t` → `Concatenate3Strings`) and the source region with
**XOR 0x76** (bytes `5E 25 02 04 1F 18 11` → ` (StringVar`), with `0x76`
runs acting as zero/filler. So the full Crystal source of each function is
recoverable; the remaining work is mapping the zone offsets/lengths, then
either inlining calls with argument substitution in the transpiler or
emitting the bodies as RDL `<Code>` VB functions. A real multi-session
project — deliberately not rushed at the tail of this one.

Verified at the stopping point: corpus scans **deterministically at 2/88**
fatal; 843-test suite green; visual-regression 5/6 (the same single
pre-existing `Top5USAsubCanada` page-2 failure it has shown all session).

### Loop batch: case-insensitive engine field lookup, Crystal's `Select expr Case`, 1-arg `Date()` (corpus 6 → 3)

**Implemented, four fixes.** (1) **Engine `Fields` dictionary made
case-insensitive** (Reporting repo): expressions routinely reach it with
different casing than the `<Field Name>` declaration (formula `TotalFeeLC`
vs. column `TotalFeeLc`, formula `region` vs. column `REGION` — the
converter's dup-guard matches case-insensitively, the engine lookup didn't),
and a mismatch was a hard "Field not found". Nothing legitimately declares
two fields differing only by case (that already logs "has duplicates").
(2) **Grammar accepts Crystal's own `Select <expr> Case v: r` spelling** —
the rule previously required the VB-style `Select Case <expr>`, so native
Crystal Select formulas fell through to the regex fallback and reached the
engine as raw text. (3) **1-arg `Date(x)` maps to `CDate`** — the FunctionMap
sent every `Date()` to `DateSerial`, which has no 1-arg overload ("DateSerial
is not known"); arity now picks the coercion vs. constructor form.
(4) **Trailing-dot number literals normalized** (`Case 15000. To 1000000.:`)
— the grammar's NumberLiteral rejects them. Cleared `ChinaOrders-Grouped-dsct`
and `Dunning` ×2. Verified: 843 tests green, visual-regression 5/6 (same
pre-existing failure), fatal-set diffs pure removals.

**Remaining 3 files (all custom-function / operator gaps):**
`souvikduttachoudhury__CustomFunctions` + `benbrahim777__function` call
functions whose bodies live in the .rpt's custom-function library
(`cdExpandRegionAbbreviation`, `cdFormatCurrencyUsingScaling`,
`cdDateDiffSkipHolidays`, `Concatenate3Strings`) — extracting those bodies is
its own binary-research project; `CustomerProfileReport` uses Crystal's
string `in` operator (substring/set membership) in a record-selection
formula, which leaks through the fallback as literal "in".

### Loop batch: comment stripping, TextObject routing, {#RunningTotal} refs (corpus 13 → 6)

**Implemented, four small fixes.** (1) `RegexTranspile` now strips `//` and
`/* */` comments — the grammar skips them as NonGrammarTerminals, but the
regex fallback only runs when that parse *failed*, and failing formulas
routinely open with Crystal's "// This conditional formatting formula..."
boilerplate, which leaked into the RDL as literal slashes. (2) A formula that
was *nothing but* comments now degrades to `=""` instead of emitting a bare
`=` (placeholder conditional-format hooks are commonly saved that way;
cleared `Documents`, `ProductionOrder` ×2). (3) `NeedsTableRouting` now also
routes sections whose *TextObjects* embed `{Field}` references — they resolve
to the same `Fields!` expressions a placed FieldObject does and need the same
data scope (cleared `iPaymentCreditCardStatement`, `StatementOfAccount`).
(4) Braced `{#RunningTotal}` references never had the `#` marker stripped
(bare `#X` and `{@X}` both did), so they emitted `Fields!_RTotal0.Value`
against a declared `RTotal0` (cleared `AccountBalance` ×2). Verified: 843
tests green, visual-regression still 5/6 (same pre-existing failure),
fatal-set diffs pure removals.

**Remaining 6 files**: `CustomFunctions`/`function` (.rpt-embedded custom
functions — bodies live in the file's custom-function library, unparsed),
`Dunning` ×2 (`TotalFee*` formulas referenced but absent — likely defined in
a subreport), `ChinaOrders-Grouped-dsct` (Select Case with trailing-dot
number ranges `15000. To 1000000.`), `CustomerProfileReport` (`in` operator
inside emitted IIf + `region` dup-guard/casing interaction).

### Engine: Fields.FinalPass parse order was a per-process coin flip (calculated-field cross-references)

**Implemented (engine-side, Reporting repo, uncommitted per convention).**
The `boyum__Documents*` files flip-flopped between clean and fatal across
*identical* converter output — proven by diffing the generated RDL between
two scan runs that disagreed (byte-for-byte identical) after two innocent
suspects (a PageBreakCondition guard, the numeric-typing pass) were each
bisected and cleared. Root cause: `Fields` stores DataSet fields in a
`Hashtable`, and .NET randomizes string hashing per process, so
`Fields.FinalPass` parsed calculated fields in a different order every run.
A `<Value>` expression referencing another *calculated* field type-checks via
`FunctionField.GetTypeCode → Field.Type → _Value.Expr.GetTypeCode()`, which
is `Object` until the referenced field has itself been FinalPass'd — so
`Switch(Fields!X_Is_AR_Order.Value Or ..., ...)` failed AND/OR's boolean
requirement whenever the big Switch happened to parse before its `X_Is_*`
operands, and passed otherwise. Fixed by dependency-ordering the walk:
DataField-bound fields first, then Value fields topologically (scanning each
`Value` source for `Fields!Name` references; cycles fall back to leftover
order). Corpus now scans **deterministically at 13/88** across three
consecutive runs — lower than either flaky reading. Also of note: my own
PageBreakCondition aggregate guard (drop the condition when it needs
`RowNumber()`/`CountRows()`, which Grouping context bans) and the
numeric-usage typing for synthesized columns (`'-'` operands → Float64) both
landed this round and cleared `SalesByCustomer-Grouped`,
`USA-Orders-Pct-colored`, and `AccountBalance`'s `-`-operator errors, but the
Documents mystery was the ordering bug, not either of them.

### Follow-up wave from the formula-extraction fix: missing columns synthesized, DateDiff added

**Implemented (first two items of task #49's wave; corpus 28 → 22).**

1. **Formula-referenced columns absent from the DataSet — synthesized.**
   The ~239 `Field 'X' not found` errors came from newly-extracted formulas
   (SAP `CompanyInfo_*` blocks especially) referencing `{CompanyInfo.
   AddressFull}`-style columns that had no `DatabaseField` entries. Extended
   `RptParser.BackfillTableNamesFromFormulas`: a braced `{Table.Column}`
   reference whose column exists nowhere in the field list now synthesizes a
   DataField-bound `DatabaseField` (String-typed — the real type isn't
   recoverable there), since Crystal treats these as ordinary queried columns.
   Braced references only; the bare `Table.Column` shape stays
   backfill-only — it's too loose to invent fields from. One self-inflicted
   bug caught by the test suite on the first run: the synthesis `Add`s to
   `report.Fields` while the outer loop enumerated the same collection —
   26 tests failed with parse errors before a `.ToList()` materialization
   fixed it. Cleared 6 files (`InventoryTransferRequest`, `Payments`,
   `SalesOpportunity`, ×2 HANA each).
2. **`VBFunctions.DateDiff` added engine-side** (Reporting repo, uncommitted
   per convention — 16 corpus occurrences, e.g. the AgingDate buckets in
   `Dunning`). Mirrors `DateAdd`'s interval codes; `object`-typed arguments
   for the same exact-match-reflection reason as `IsNothing(object)`. Error
   class went to zero corpus-wide; the affected files remain fatal on their
   other, separate errors.

**Further items landed in the same wave (corpus 22 → 15):**

3. **`OnFirstRecord`/`OnLastRecord`** mapped to `(RowNumber() = 1)` /
   `(RowNumber() = CountRows())` in both the emitter's `BareIdentMap` and the
   regex fallback. First attempt used `Globals!RowNumber`, which introduced a
   *new* error ("Globals 'RowNumber' not found") — the engine exposes
   RowNumber/CountRows as *functions* (`ExprParser/Parser.cs`), not Globals
   entries. That also exposed the same latent bug in the two pre-existing
   `"record number" → Globals!RowNumber` special-field mappings; all four
   sites corrected. Clearing the untyped identifier also fixed the five
   `Documents*` files' giant-Switch "AND/OR requires boolean" errors — the
   bare `OnFirstRecord` inside them was the non-boolean operand all along.
4. **`CurrentFieldValue` degrades the whole formula to `=""`** — it's Crystal's
   conditional-formatting context variable ("the value this format rule is
   attached to"), which a DataSet expression has no equivalent for; letting it
   through broke enclosing calls' reflection binding with misleading
   "Function Month is not known" errors (`B1Budget_M`/`_Q` cleared).
5. **Referenced-but-undeclared parameters synthesized** (`{?ObjectId@}` —
   injected by SAP at print time, never declared in the .rpt): same treatment
   as the missing-column synthesis, a `ParameterField` is synthesized for any
   `{?Name}` reference with no declaration.

**Remaining in this wave** (exact expressions in the scan output): custom
functions stored in the .rpt (`cdExpandRegionAbbreviation`,
`cdFormatCurrencyUsingScaling`, `cdDateDiffSkipHolidays`,
`Concatenate3Strings` — their bodies live in the file's custom-function
library, unparsed today), `RowNumber()/CountRows()` now rejected specifically
*inside Grouping expressions* ("Aggregate function cannot be used within a
Grouping expression", 4 occurrences — the record-position mapping needs a
non-aggregate form or suppression at that call site), residual
`Field not found` (10 + 2 + 3 singles), `DateSerial` binding (2), a `Mod`
type error (2), and `//` comments reaching the regex fallback (4).

Verified: 843-test suite green (after the enumeration fix); corpus fatal-file
set diffed both steps (pure removals); visual-regression suite still 5/6
(same pre-existing failure).

### Parser: tag-119 formula records with ≠1 dependency were silently dropped (45 of 76 formulas in one file)

**Implemented — and it deliberately made the corpus count worse before it can
get better.** Read this entry fully before "fixing" the count regression.

The trigger: `boyum__ProductionOrder.rpt`'s `Origin` formula has body
`@Title_Manual`, but no formula named `Title_Manual` existed in
`report.Fields` — while sibling formulas (`Title_Production`,
`Title_Assembly`) extracted fine. Tag-119 record counts confirmed the scale:
76 records in the file, only 24 formulas extracted.

Root cause in `RptParser.ExtractFields`: after a formula's name block, the
record holds a **2-byte big-endian count of dependency strings** (the fields
the formula references), then that many length-prefixed strings each followed
by 3 filler bytes, then the body. The old code hard-assumed exactly one
dependency (`2 bytes | alias | 3 bytes | body`), so a **zero-dependency**
formula (pure literal like `'Manual'`, `whileprintingrecords;` counters) read
its own body as the "alias" and found nothing where the body should be, and a
**multi-dependency** formula read dependency #2 as the body or nothing at
all. Both were then dropped by the `IsNullOrEmpty(formulaText)` guard —
silently. 45 of ProductionOrder's 76 formulas, including every `Title_*`
localization label. (Getting the multi-dependency stride right took two
passes: hex-dumping showed 3 filler bytes after *every* dependency string,
not 4-between/3-after as first guessed — `Line_Item` dep at 36+15+3 = body at
54, `ParentPrice` dep at 38+23+3 = dep2 at 64, both verified against raw
bytes before keeping it.)

**Immediate downstream consequence #1 — engine stack overflow.** One newly
extracted formula (`SeriesName`, body `{SerialNumbers.SeriesName}`, whose
underlying column is not part of the DataSet) transpiled to a *self-
referencing* DataSet field: `<Field Name="SeriesName"><Value>=Fields!
SeriesName.Value</Value>`. The engine's `Field.Type` ↔
`FunctionField.GetTypeCode()` pair recurses through field references with no
cycle guard, so `RDLParser.Parse` dies with an uncatchable
`StackOverflowException` — it killed the whole corpus-scan process, which is
itself worth remembering: a scan that crashes mid-run produces a truncated,
misleading fatal-file diff (the first post-crash diff looked like 7 files
were "fixed" that were simply never scanned). Guarded in
`RdlConverter.WriteDataSets`: a formula field whose entire transpiled
expression is exactly its own `Fields!X.Value` self-reference is emitted as
`=""` instead — the column isn't in the DataSet, so no faithful translation
exists, and valid-but-empty beats an uncatchable crash.

**Immediate downstream consequence #2 — the corpus fatal count went UP, 9 →
28, and that is the honest number.** The previously-dropped formulas were
hiding real conversion gaps; extracting them (correctly) exposed every one:

- **~239 `Field 'X' not found` errors across ~19 files**: the new formulas
  (SAP `CompanyInfo_*` blocks especially) reference columns from tables
  (`CompanyInfo.PathBitmap`, `.AddressFull`, `.Phone2`, ...) that have no
  `DatabaseField` entries at all, so `Fields!AddressFull.Value` resolves
  against nothing. Likely fix: synthesize DataField-bound DataSet fields for
  formula-referenced columns that are missing (they're real database columns
  Crystal would query); tracked as its own follow-up.
- **`DateDiff` not in `VBFunctions`** (16 occurrences), plus `Month`/
  `DateSerial` failing to bind in some argument shapes.
- **`NOT`/`AND-OR` boolean-typing failures** on newly-extracted suppress
  formulas (`=Not (onFirstRecord)` — `onFirstRecord` needs mapping to a
  boolean the engine knows).
- **`Report parameter 'ObjectId_' not found`** — a `{?$[...]}`-wrapped SAP
  parameter shape surviving inside a `Switch(...)` body.

The extraction fix is *correct* — verified at the byte level, and BOM went
41 → 44 formulas with all 41 originals intact, ProductionOrder 24 → 66 —
so it stays; the count regression is newly-visible pre-existing debt, exactly
the situation this file's earlier entries warn about (an aggregate count
moving the "wrong" way while the underlying truth improves). The follow-up
work above is task #49.

Verified: full 843-test suite green; corpus scan completes without crashing
(45 of 88 → after this session's other fixes 9 → 28 for the reason above);
per-file extraction diffs checked for BOM (nothing lost, 3 gained) and
ProductionOrder before accepting the layout change.

### Three field-resolution bugs: chart display names, summary captions, group sections without a Details table

**Implemented.** Batch of three independent fixes taking the corpus from 15
fatal files to 9. Grouped here because they were triaged and fixed together,
not because they share a cause.

**1. Chart field references used Crystal's *display* name — 1 file**
(`benbrahim777__Top3-Employee-Sales`). The DataSet declares `Last_Name` and
every other reference in the file correctly reads `Fields!Last_Name`, but the
chart emitted `Fields!Employee_Last_Name`. The chart's category field arrives
from the parser as `Employee Last Name` — Crystal's display form of a
table-qualified field, **space-separated, not dotted** — and sanitizing that
whole string produces a name no `<Field>` ever matches. Added
`ResolveDisplayFieldName`, which maps a raw name back to the declared column
when it matches a known `"{TableName} {ColumnName}"` pair, and returns it
untouched otherwise (the common case: `Order Amount` already *is* the column
name). Threaded `ReportDefinition` into the chart writers to make the lookup
possible.

*False start worth recording*: the first attempt assumed the qualifier was
dotted (`Employee.Last Name`) and changed `NormalizeFieldName` to strip a
`Table.` prefix globally. Full test suite and corpus scan both came back
completely unchanged — no file fixed, no file broken — which is what prompted
dumping the actual parsed value and finding it space-separated. That change
was reverted rather than kept: it was plausible, harmless, and entirely
unverified, and nothing in the corpus exercises it.

**2. Summary *captions* stored as formula bodies — 4 files** (`boyum__BOM`,
`boyum__Dunning`, ×2 HANA each). Crystal's auto-generated label for an
inserted summary field — `Sum of DunningData.OpenSum` — is sometimes saved as
the formula's actual body. It's prose, not syntax, so it can't parse, and the
words leaked through the regex fallback into `=Sum of Fields!OpenSum.Value`
("End of expression expected. At column 7"). `RewriteSummaryCaption` rewrites
the whole-body caption into the call it describes (`Sum(DunningData.OpenSum)`)
and hands it back to the normal pipeline, so field resolution is not
duplicated. Covers Sum/Average/Count/Distinct Count/Maximum/Minimum/Standard
Deviation/Variance, anchored to the entire body so an expression that merely
contains `" of "` inside a string literal is never touched (verified). Clears
`Dunning` ×2 outright; `BOM` ×2 keep a separate `Line#`→`Line__` bug, which is
exactly what the scan now shows.

**3. Group sections with no Details table — 3 files** (`BigCells`,
`BigCells-Mexico`, `ProductTypeSales-Grouped`). Same no-data-scope class as
the Page/Report header-footer fix above, reached by a fourth path.
`GroupHeader`/`GroupFooter` content normally lands in TableGroup Header/Footer
rows, which are inside the data region — but these cross-tab reports have an
**empty Details section**, so `hasTable` is false, there are no TableGroup
rows to land in, and the section falls through to the free-form Body path
where `Fields!` can't resolve. Extended the existing routing to cover
`GroupHeader`/`GroupFooter` *only* when there's no Details table, leaving the
normal tabular path untouched.

One open risk was worth checking rather than assuming: the same section holds
the cross-tab, so routing it moves a `<Matrix>` into a `TableCell`. Diffing
the generated RDL before/after confirms the Matrix moves intact — carrying its
own `<DataSetName>`, wrapped in the `Rectangle` the single-child `TableCell`
rule requires — and that both it and the group-name textbox are *moved, not
duplicated* (exactly one of each afterward). The file renders clean.

Verified after each of the three: full 843-test suite green throughout; corpus
fatal-file count 15 → 14 → 12 → 9, fatal-file set diffed at every step (pure
removals, zero regressions); visual-regression suite still 5/6 (same
pre-existing `Top5USAsubCanada` page-2 failure).

### Crystal statement syntax: trailing `;`, scopeless `stringvar`, and a `varDecl` rule that never matched

**Implemented.** Three related gaps in how formula *statements* (as opposed to
expressions) are handled, all confirmed by direct parse tests before touching
anything. Together they were the largest remaining fatal cluster — 6 files
(`boyum__Picklist`, `InventoryGoodsIssueAndReceipt`,
`ProductionIssueAndReceipt`, ×2 HANA variants each).

1. **A trailing `;` failed the whole parse.** `stmtList` is built with
   `MakePlusRule(stmtList, ToTerm(";"), stmt)`, which permits `;` only
   *between* statements — but Crystal allows one on the last statement too, and
   `CStr({X.PickListNumber}, '#');` is a complete, valid formula. The parse
   failed, `FormulaTranspiler` fell through to `RegexTranspile`, and the stray
   `;` went straight into the emitted RDL (`=CStr(Fields!X.Value, '#');` —
   rejected with "End of expression expected. At column 41"). Fixed by
   spelling the trailing form out explicitly: `program.Rule = stmtList |
   stmtList + ";"`.
2. **Crystal's scope prefix on a variable declaration is optional**, and
   `FormulaTranspiler.CrystalVarDecl` — the guard that degrades untranslatable
   variable formulas to `""` so the RDL stays valid — required
   `Local|Global|Shared`. So `stringvar timeString := CStr(...)` slipped past
   it and reached the emitted RDL as a reference to a variable RDL has no
   concept of. Made the scope group optional.
3. **The `varDecl` grammar rule had never matched anything**, and *fixing it
   would have made things worse.* It spelled the declaration as three tokens
   (`varScope + varType + "Var"`) while the lexer reads `StringVar` as a
   single identifier — so even `Local StringVar t := 'a'` failed to parse.
   That accident is exactly what makes these formulas work as well as they do:
   the guard in (2) lives inside `RegexTranspile`, which **only runs when the
   grammar fails**. Repairing the rule would have let the parse succeed and
   emit an expression referencing an undefined `timeString`, *bypassing* the
   guard entirely. Removed the rule (and its now-dead `varScope`/`varType`
   non-terminals, `VarDeclRule` constant, and `RdlEmitter` case) rather than
   repairing it, leaving one mechanism for variable declarations instead of
   two competing ones — with a comment at the rule site explaining why the
   absence is deliberate, so it doesn't get "fixed" back.

**Unplanned improvement, worth noting**: dropping the variable-declaration
keywords from `MarkReservedWords` (`Number`, `String`, `Boolean`, `Date`,
`DateTime`, `Time`, `Currency`, `Local`, `Global`, `Shared`, `Var`) also
unblocked them as ordinary identifiers. `funcCall.Rule` is `id + "(" + ... +
")"`, and a reserved word can't match `id` — so `Date(2020,1,1)` (mapped to
`DateSerial` in `FunctionMap` for exactly this case) could never actually
parse before. It does now, as do field/function names called `Time`,
`Currency`, `Number`, etc.

**Not attempted**: actually *translating* variable-using formulas rather than
blanking them. The single-assignment case (`stringvar t := expr; ...t...`) is
inlinable — substitute the definition at each use site — which would turn
these fields from empty into correct. Worth doing if a report surfaces where
the blanked field matters; the degrade-to-`""` behavior is a deliberate
"valid but incomplete beats fatal" tradeoff, not a claim that it's right.

Verified: full 843-test suite green; corpus fatal-file count dropped from 21
to 15 of 88 — all six predicted files cleared, zero regressions (fatal-file
set diffed, pure removals); visual-regression suite still 5/6 (same
pre-existing `Top5USAsubCanada` page-2 failure).

### Crystal special fields written as a formula's entire bare body ("Page Number", not `{Page Number}`)

**Implemented.** After the Page-Header/-Footer fatal cluster above was cleared,
re-categorizing the corpus scan's remaining fatal messages by shape showed
`Expression '=Page Number' failed to parse: End of expression expected. At
column 8` as the single largest remaining cluster — 10 files
(`boyum__BOM`, `Payments`, `Picklist`, `ProductionOrder`, `ServiceContract`,
×2 HANA variants each).

Root cause: a Crystal *formula* (e.g. one named `PageXofY`) whose entire
`FormulaText` body is literally the two bare words `Page Number` — Crystal's
special-field name written with no `{...}` wrapper, as if it were valid
syntax on its own. `RdlConverter.SpecialFieldExpression` already maps
`"page number"` correctly, but only for a placed `FieldObject`'s own
`FieldName` — this is a different code path (`FormulaTranspiler.
ToRdlExpression`), and neither the Irony grammar (two bare identifiers with
nothing joining them is a genuine parse error) nor the regex fallback (whose
`bareMap` only matches the no-space `PageNumber` spelling) recognized the
two-word phrase, so it passed straight through into the emitted RDL as the
literal `=Page Number` — invalid VB.NET, rejected by the target engine with
exactly the observed error.

Fixed with a `BareSpecialFieldExpression` check at the very start of
`FormulaTranspiler.ToRdlExpression`, before the grammar is even tried —
recognizes the same phrases `SpecialFieldExpression` does (`Page Number`,
`Total Page Count`, `Page N of M`, `Print Date`, `Print Time`, `Modification
Date`, `Record Number`), minus the two report-context-dependent ones (`Report
Title`/`Report Comments` — not observed in this bare-body shape in the
corpus, and this call site has no `ReportDefinition` to resolve them from
anyway). Kept as a small duplicated switch rather than reaching for
`RdlConverter.SpecialFieldExpression` directly — `FormulaTranspiler` has no
existing dependency on `RdlConverter` (the reverse is already true), and
seven duplicated lines is cheaper than introducing that coupling.

Verified: full 843-test suite green; full 88-file corpus scan's fatal-file
count dropped from 25 to 21 (four of the ten affected files cleared
outright; the other six still have their *own*, separate, already-distinct
bugs the scan surfaced once this one stopped masking them — confirmed by
checking each file's remaining error text changed, not just that the count
moved less than ten); visual-regression suite still 5/6 (same pre-existing
`Top5USAsubCanada` page-2 failure).

### Field-bound PageHeader content fails with "Field 'X' not found" (SAP document-card templates)

**Implemented.** By far the largest remaining fatal-error cluster after the fixes
below — categorizing the full 88-file corpus scan's fatal messages by shape
showed hundreds of `Field 'X' not found` occurrences (`TitleDate1`,
`CustomerName1`, `ContactPerson1`, ...), dwarfing every other error class,
concentrated almost entirely in `boyum__*` SAP Business One "document card"
templates (invoices, transfers, sales orders — ~15+ files affected).

Root-caused via `boyum__CustomerEquipmentCard.rpt`: the referenced field
(`Title_Date`) genuinely exists in the generated `<DataSet>` with a valid
`Switch(...)` expression — the *field* isn't the problem. The `Textbox`
referencing it (`TitleDate1`) sits directly in RDL's `<PageHeader>`
(`WritePageHeader`'s free-form path), and reading
`Reporting/RdlEngine/Definition/Expression.cs`'s `FinalPass` shows why that
can never work: it walks the expression's parent chain looking for an
enclosing `DataRegion`/`DataSetDefn` to source `Fields` from, and while it
*records* `PageHeader`/`PageFooter` as it passes through, it keeps climbing
past them rather than stopping — a top-level `<PageHeader>` is never itself
a `DataRegion`, so `fields` stays `null` and every `Fields!` lookup inside it
fails, regardless of whether the field is valid. Same restriction real SSRS
enforces (Page Header/Footer expressions can only see `Parameters!`/
`Globals!`/`ReportItems!`). Crystal has no equivalent restriction — its own
Page Header can bind to database/formula fields freely — so these SAP
templates lean on it heavily, putting a whole customer/document "master
record" display in the Page Header with the Details section sometimes
entirely empty.

Fixed by routing a PageHeader section's content into the Details Table's own
`<Header>` band instead of RDL's `<PageHeader>` whenever it contains a
`FieldObject` *and* a Details Table exists to attach to (`RdlConverter.
WriteBody` detects this and returns the consumed section(s) so
`WritePageHeader` skips re-emitting them) — the same `DataSetName` scope
`WriteTableReportFooter` already relies on for `ReportFooter`, `RepeatOnNewPage`
matching Crystal's own "prints every page" Page Header semantics. Unlike
`WriteTableReportFooter`'s single joined-text collapse, this content is a
real free-form grid of labels *and* field values, so it reuses
`WriteFreeFormObjects`' existing Left/Top layout rather than flattening it —
which surfaced a second, unrelated hard rule the first attempt broke: a
`TableCell`'s own `<ReportItems>` permits **exactly one** child element
("Only one element in ReportItems element is allowed within a TableCell" —
unlike Body/PageHeader/PageFooter's `ReportItems`, which allow any number).
Fixed by wrapping the section's items in one containing `<Rectangle>`.

Several follow-on gaps surfaced across repeated rescans, each the same
underlying "this content has no legal place to live" bug wearing a
different hat — every one found by the same discipline: fix, rescan, check
whether the fatal count *and* the specific error text actually moved, and if
a file stayed fatal, read its *new* error rather than assuming the fix was
just incomplete in a way that didn't matter yet.

1. **Crystal splits one logical page header into several PageHeader
   sections** (one per subreport strip, in these templates) — `boyum__
   Documents.rpt` alone has four. The first version only picked the single
   section with the *most* FieldObjects (`FirstOrDefault`), so the 2nd/3rd
   field-bound section still hit the exact same "Field not found" error it
   was meant to fix. Changed to collect *every* PageHeader section with a
   FieldObject and emit each as its own Header row.
2. **The identical restriction applies to `PageFooter`**, and some of these
   templates (e.g. `boyum__Documents.rpt`'s `CompanyInfo_Style3`) put
   field-bound content there instead of (or in addition to) PageHeader.
   Mirrored the fix into the Table's own `Footer` band, piggybacking on
   `WriteTableReportFooter` (which already opens `<Footer>` for
   `ReportFooter` content) — field-bound PageFooter rows are written first,
   the once-only ReportFooter text after, mirroring the Header side's
   "repeating content first" ordering.
3. **`ReportHeader`/`ReportFooter` have the exact same restriction as
   `PageHeader`/`PageFooter`**, just less obviously — they're free-standing
   report items too, and `Expression.cs`'s ancestor walk stops at *any*
   enclosing `DataRegion`/`DataSetDefn`, never at a section type specifically.
   Found by chasing `boyum__CustomerEquipmentCard.rpt`'s Page-Header-nested
   `Subreport2` (its own separate, recursively-converted sub-report) into a
   `NullReferenceException` — its *own* field-bound content lived in *its
   own* `ReportHeader`, not `PageHeader`, and failed identically. Broadened
   detection to all four section kinds; `ReportHeader`/`ReportFooter`
   sections *without* FieldObjects (the common title/logo/tagline case) are
   left exactly where they were — only field-bound ones move.
4. **~8 files have an empty Details section** (`boyum__Activity`,
   `CustomerEquipmentCard`, `ServiceCall`, `SolutionKnowledgeBase`, ×2 HANA
   variants each) — the whole per-record display lives in Page Header/Footer
   FieldObjects instead, so there's no Table to attach that content to.
   Fixed with `WriteHeaderOnlyTable`: a minimal synthetic Table (one
   full-width column, the field-bound content as its Header, one blank
   Details row — RDL requires at least one, "For TableRows at least one
   TableRow is required", even though Crystal's own Details is empty too)
   built solely to give this content a `DataSetName` scope to live in.
5. **Subreports hit a related but distinct hard rule**: this engine
   explicitly rejects one placed directly in `PageHeader`/`PageFooter`
   ("The Subreport 'X' is not allowed in a PageHeader or PageFooter" —
   `Subreport.cs`'s own `FinalPass` check) regardless of whether the section
   also has FieldObjects — surfaced once fix 3 stopped masking it with an
   unrelated crash. `WriteFreeFormObjects` (which fixes 1-4 already reuse)
   handles `SubreportObject` correctly inside a `TableCell`, so this needed
   only a broadened routing predicate, not new emission code. (Unlike the
   Fields! restriction, this one doesn't apply to `ReportHeader`/
   `ReportFooter` — the engine's own message names only Page Header/Footer.)
6. **The same case-sensitivity bug independently blocked several files at
   this point**: a placed object's own `FieldName` doesn't always match its
   DB column's *stored* case exactly (`boyum__Activity.rpt`'s `Personal1`
   FieldObject vs. a `personal` column) — and this engine's `Fields`
   dictionary lookup is plain case-sensitive (`Hashtable`/`ListDictionary`,
   no comparer), so `Fields!Personal.Value` against a declared `<Field
   Name="personal">` fails outright even though it's unambiguously the right
   field. Fixed in three places that each independently derived a
   `Fields!X.Value` reference from an object's own casing rather than the
   DataSet's declared casing: `WriteFreeFormObjects`'s `FieldObject` case,
   `ResolveTextWithFieldRefs` (both now resolve through a shared
   `BuildKnownFieldsMap` — a case-insensitive-keyed map to each field's real
   declared name), and `WriteDetailsTable`'s own detail-row cell emission
   (same fix, scoped to just DB/formula/running-total fields to match its
   narrower existing behavior).

**Still not fixed** — 25 of 88 files remain fatal, now a genuinely
heterogeneous tail rather than one dominant class: roughly even split
between more `Field 'X' not found` (distinct per-file causes — e.g.
`souvikduttachoudhury__CustomerProfileReport.rpt`'s `region`/`phone` bare
lowercase fields, noted back when the string-slicing fix was verified) and
`End of expression expected` (Crystal syntax the transpiler doesn't cover
yet, e.g. `=Sum of Fields!DocTotal.Value` — an English-language summary
phrasing, not the `Sum(...)` function call form). Each is its own
individual-file investigation now, not a shared root cause — no more
single fix is likely to move more than one or two files at a time from
here.

Verified after every pass above: full 843-test suite green throughout; full
88-file corpus scan's fatal-file count went 51 → 45 → 38 → 34 → 30 → 28 → 25,
confirmed via diffing the exact fatal-file set at every single step (zero
regressions at any point — every diff was pure removals, never a new
addition, including the one point mid-sequence where a fix's fatal-file
*count* didn't move at all — the diff still confirmed zero regressions
before moving on); visual-regression suite stayed 5/6 throughout (same
pre-existing `Top5USAsubCanada` page-2 failure, confirmed identical error
text before/after every single time, never a new one).

### Formula-transpilation gaps found by the full-corpus fatal-error scan

**Implemented — the confirmed, addressable ones.** Direct follow-on to the
corpus scan above: with the two universal fatal bugs fixed, the same 88-file
scan surfaced a `Function X is not known` / `'X' is an unknown identifer`
class of failures — a `switch()`/case scan showed all of these to be either
(a) valid Crystal syntax our transpiler didn't yet map, or (b) a target
function our transpiler correctly mapped *to* that simply didn't exist in
`VBFunctions.cs` (same shape as the earlier `IsNothing` fix). Fixed:

1. **Bare (unbracketed) `Table.Column`, `@FormulaName`, `#RunningTotalName`
   references.** Crystal allows a formula's *entire* body to be just
   `Lines.TaxDate` or `@DateToAgeBy` — no `{...}` wrapper. The braced forms
   (`{Table.Column}`, `{@Formula}`) already resolved correctly via
   `RdlEmitter.EmitFieldRef`; the bare forms didn't parse at all (the Irony
   grammar had no primary rule for a dotted identifier or an `@`/`#`-prefixed
   one), so `CrystalFormulaParser` failed and the regex fallback's patterns
   only matched the braced forms too — the bare text passed through
   unchanged and the target engine choked on it as a literal expression.
   Added `dottedRef`/`atRef`/`hashRef` productions to
   `CrystalFormulaGrammar.cs` (each resolving through the same
   `Fields!X.Value` convention `EmitFieldRef` already uses) and matching
   regex patterns to `FormulaTranspiler.RegexTranspile` for defense in depth.
   Caught one bug in the process: `MarkPunctuation` never actually listed
   `.`/`@`/`#`, so the punctuation tokens stayed in the tree and every
   resolved reference emitted `Fields!_.Value` (sanitizing the bare
   punctuation character itself instead of the identifier that followed it).
2. **`switch(cond1, val1, cond2, val2, ..., True, default)`** — SAP Business
   One templates favor this function-call form over Crystal's native
   `Select Case`. It parses fine as an ordinary `funcCall` already; just
   needed a `FunctionMap["switch"] = "Switch"` entry, since the target
   engine's parser recognizes `Switch` as a real built-in construct
   (`Parser.cs`: `case "switch" -> new FunctionSwitch(args)`) with the exact
   same alternating-pairs argument shape `EmitSelectCase` already builds by
   hand for native `Select Case`.
3. **`?$[SAPInternalId]`-wrapped parameter names.** SAP Business One
   parameter fields are named `$[BOY_AB_TODATE]` rather than a plain
   identifier. `EmitFieldRef`'s `{?...}` handling stripped only the leading
   `?`, sanitizing the surviving `$[BOY_AB_TODATE]` into a mangled
   `__BOY_AB_TODATE_` that could never match the parameter's real declared
   name. Added `FormulaTranspiler.StripSapParamWrapper` and applied it
   consistently everywhere a parameter name is read or written:
   `EmitFieldRef`, `RdlConverter.WriteReportParameters` (the declaration
   site — was sanitizing the raw `$[Id]` form directly), and both directions
   of `WriteSubreportParameters`'s parent/child parameter-name matching
   (same wrapper, same mismatch, would have silently broken the instant the
   declaration site got fixed without also fixing these).
4. **Missing/wrong-arity `VBFunctions` methods** (same root cause as the
   earlier `IsNothing` fix — mapped correctly, target method didn't exist):
   added `CDec(object)`, `Color(r,g,b)` (returns a `"#RRGGBB"` string, the
   same convention already used for `crRed`-style named-color constants —
   not a `System.Drawing`/`Majorsilence.Drawing` `Color` value, since
   BackColor/ForeColor style expressions are evaluated as strings),
   `DateSerial(object,object,object)` (was mapped from both `dateserial` and
   the newly-added `date` — Crystal's `Date(y,m,d)` constructor — but neither
   pre-existed), and a 2-argument `CStr(value, format)` overload (Crystal's
   own `CStr(number, decimalPlaces)` / `CStr(value, formatString)`, distinct
   from VB.NET's 1-arg `CStr`). Went in via the same `Reporting` repo,
   uncommitted (your call whether/when).
5. **`if(cond) THEN ... ELSE ...` regex-fallback bugs**, found chasing one
   specific formula (`if({?$[BOY_AB_TODATE]} = DATE(9999,12,31)) THEN
   \nCurrentDate\nELSE\n{?$[BOY_AB_TODATE]}`) through to the *real* cause —
   two separate, pre-existing bugs in `FormulaTranspiler`, neither related to
   the bare-reference work above: (a) `TranspileIfThenElse`'s regex required
   `\s+` (at least one space) directly after `If`, so Crystal's common
   `if(cond)` — no space before the paren — never matched; and its `.+?`
   groups didn't match across newlines (missing `RegexOptions.Singleline`),
   so a Then/Else clause split across lines (near-universal in these
   templates) also failed. (b) Separately, `ApplyFunctionMappings`'s bare-
   identifier replacement (`CurrentDate` → `Today()`, etc.) used a regex
   ending in `\s*(?!\()` — consuming the trailing whitespace as *part of the
   match* rather than just checking it — so `Regex.Replace` silently deleted
   it: `"CurrentDate\nELSE"` became `"Today()ELSE"`, destroying the very
   whitespace boundary fix (a) depends on. Changed to a pure zero-width
   lookahead (`(?!\s*\()`) that checks the same "not followed by a call"
   condition without consuming/deleting anything.
6. **`Month(Fields!X.Value)` (and by the same mechanism, any strongly-typed
   `VBFunctions` overload — `Year`, `Day`, `Weekday`, ...) never matching a
   field reference.** Root cause, not a mapping bug this time: the target
   engine resolves bare function calls via *exact-type* reflection
   (`Parser.cs`: `argTypes[i] = XmlUtil.GetTypeFromTypeCode(args[i].GetTypeCode())`),
   and a `<Field>` with no `<TypeName>` child defaults its `Type` to
   `TypeCode.String` (confirmed in the engine's own `Field.cs`) regardless of
   the column's real data type — so `Month(Fields!STATEMENT.Value)` looked
   for `Month(string)`, which doesn't exist, instead of `Month(DateTime)`,
   which does. Fixed by emitting `<TypeName>` for every `DatabaseField`
   (`RdlConverter.RdlFieldTypeName` maps `RptParser.MapCrValueType`'s type
   strings to the handful `DataType.GetStyle` spells differently — `Float32`
   → `Single`, `Float64` → `Double`, `Currency` → `Decimal`; everything else,
   `Boolean`/`Int16`/`Int32`/`DateTime`, already matches verbatim). Verified
   no regressions: full visual-regression suite scores are byte-for-byte
   identical before/after (this touches every `DatabaseField` in every file,
   so that was worth checking beyond just the unit suite).
7. **Duplicate `<Field Name="X">` when a formula's name collides with a
   real database column of the same name** — very common in these
   templates, where an author names a formula after the exact column it
   pulls (formula `Status` with body `{Header.Status}`; formula `Address`
   with body `BusinessPartner.Address`). `WriteDataSets` emitted *two*
   `<Field Name="Status">` entries — the correct `<DataField>`-bound one from
   `dbFields`, then a second, self-referential one from `formulaFields`
   (`<Value>=Fields!Status.Value</Value>`, i.e. referencing itself) that
   shadowed/broke the first. This is a real, pre-existing bug, unrelated to
   items 1–6 above — it was simply invisible until those fatal errors
   stopped masking it. Fixed by skipping a formula field whose sanitized
   name collides with an existing database column's sanitized name; the
   real `DataField`-bound entry already covers that name correctly.
8. **`DatabaseField.TableName` empty for every field in most `boyum__*`
   files, making `WriteDataSets`' generated query the literal, never-resolved
   placeholder `SELECT * FROM <TableName>`.** Root-caused:
   `TableName` is only ever backfilled from a *placed* `FieldObject`'s
   `"Table.Column"` reference (`ParseFieldObject` → `ExtractFieldRefFull`).
   These SAP templates are formula-driven — the report body places
   FieldObjects bound to *formulas* (e.g. `Status1` → formula `Status`), and
   the formula's own body is what references the raw column
   (`{Header.Status}`) — no object anywhere references `Header.Status`
   directly, so that backfill path never triggers and `TableName` stays
   empty for every field. Fixed by adding a second backfill pass
   (`RptParser.BackfillTableNamesFromFormulas`, run right after field
   extraction) that scans every `FormulaField.FormulaText` for the same
   `{Table.Column}` / bare `Table.Column` shapes and backfills from those
   too. Verified directly: `boyum__SolutionKnowledgeBase.rpt`'s generated
   query went from `SELECT * FROM <TableName>` to a real, fully-qualified
   `SELECT [Header].[UpdateBy], ... FROM [Header]`. Full 843-test suite and
   the visual-regression suite (byte-for-byte identical scores) both stay
   green — this touches the parser's field-extraction path for every file,
   not just the SAP ones, so both were worth re-checking.

   **Caveat**: fixing the query construction doesn't by itself clear any of
   these files from the corpus scan's fatal list — every one of them carries
   *additional*, independent issues layered on top (the scan's fatal-file
   count is unchanged: still the same 53 files). It's a necessary
   precondition, not a complete fix, for reports in this shape — worth
   confirming precisely because the aggregate count not moving could
   otherwise look like the fix did nothing.

Verified end-to-end after every step above: full 843-test suite green; full
88-file corpus scan re-run after each fix (`ErrorMaxSeverity` per file) to
confirm the specific error class actually disappeared corpus-wide, not just
in the one file that surfaced it. By the end, **zero** `is not known` / `is
an unknown identifer` errors remain anywhere in the 88-file corpus (down from
several dozen spread across ~30 files).

**Not fixed — found but out of scope for this pass:**

- **Formula-language features the grammar still doesn't parse**: Crystal's
  string-slice syntax (`Fields!X.Value[1 to 3]`) has no grammar rule at all.
  (Comments and `Select Case` are *already* handled — `//`/`/* */` via
  `NonGrammarTerminals`, `Select Case` via `EmitSelectCase` — confirmed
  working correctly across the corpus; only string-slicing remains genuinely
  unaddressed from that original list.)
- **Remaining `boyum__*` failures beyond the TableName fix above** are each
  their own distinct, compounding issue (e.g. `boyum__SolutionKnowledgeBase.rpt`
  still fails on a `Title_Status`-chain formula whose real cause wasn't fully
  isolated — it depends on `X_Language`, which compares a `String`-typed
  `CURRENT_LANGUAGE` parameter against integer literals; not confirmed
  whether that mismatch is the actual failure or a red herring). Each of
  these 53 files likely needs its own individual triage pass rather than
  one more shared mapping fix — there wasn't a second universal cause left
  to find here.

### Free-form object Left/Top position not extracted — everything collapsed to (0,0)

**Implemented (workaround, not a true byte-level fix — see caveat).** Triggered
by a user report that the Avalonia viewer showed garbled/overlapping text for
`benbrahim777__CustomerList.rpt`'s title/logo/tagline area. Confirmed via a raw
hex dump of `RptParser.ExtractObjectBounds`'s tag-158 payload that **`Left`
and `Top` are literally `0x00000000` in the .rpt file bytes for every
free-form object** — `TextObject`, `FieldObject`, `ImageObject` alike, across
every section type. Byte content past the object's name string is also
byte-for-byte identical across every object regardless of name/type/section —
not a per-object field either. Position for these object kinds does not
appear to be recoverable from this record; the real encoding (if any) is still
unknown.

Rather than block on finding that byte-level answer, three targeted fixes
close the actual user-visible gap:

1. **`RptParser.ParsePictureObject`** read bounds directly off the tag-175
   wrapper, which has no tag-158 child — always yielding an all-zero
   (invisible) image. Bounds/name are nested one level deeper
   (175 → 174 → 158), the same shape `ParseChartObject` already unwraps one
   level further down (179 → 174 → 158) for charts. Fixed by unwrapping the
   same way; the report's logo now gets its real size instead of 0×0.
2. **`RdlConverter.WriteFreeFormObjects`** now detects the degenerate case —
   more than one object in a section, all with `Left == 0` — and lays them out
   left-to-right by declaration order using `Width` (the one dimension that
   *does* parse correctly), the same convention `WriteDetailsTable` already
   uses for the Details table's own columns. Fixes PageHeader's column labels
   (previously all stacked at `Left=0`, scrambled together) and the
   ReportHeader's logo/title pair. A section with only one object, or where
   `Left` already varies, is left untouched.
3. **ReportFooter was landing on page 1.** Turned out to be a second, distinct
   bug, not a position bug: comparing against the real-Crystal reference image
   showed the tagline ("Xtreme Mountain Bikes takes you higher!") doesn't
   appear on page 1 at all in the real render — it's a genuine Crystal Report
   Footer, meant to print once at the very end of a report that spans many
   pages (this file's reference render says "Page 1 of 9"). `WriteBody` was
   dumping `ReportFooter` section content into the same fixed-position free-form
   `Body` list as `ReportHeader`, landing it at the same absolute (0,0) as the
   title on page 1 every time. Fixed by routing `ReportFooter` content into a
   new top-level `Table` `Footer` (sibling to the existing top-level `Header`;
   `WriteTableReportFooter`) when a Details table exists, spanning the full
   row via `ColSpan` — RDL's native "print once, right after the last detail
   row" mechanism, matching Crystal's own semantics. Falls back to the old
   free-form placement when there's no table to attach to.

Verified: full 843-test suite still green; visual-regression suite still 5/6
(same pre-existing, documented failure as before — `Top5USAsubCanada` page 2);
rendering our own PDF for `CustomerList.rpt` directly (not just the diluted
aggregate similarity score) confirms the logo, title, and page-header column
labels no longer overlap, and the tagline no longer appears on page 1.

**Caveat**: this doesn't fix the general case — a section with legitimately
different, meaningful non-Left-0 layouts (e.g. a logo positioned *beside* a
multi-line address block rather than a single flow-in-order row) will still
render wrong, since the real per-object position still isn't recoverable.
Revisit if a corpus file surfaces that pattern.

### Detail table / cross-tab missing on page 1 in `VisualRegressionTests` (by design, not a bug — but makes the suite's score unreliable)

**Root-caused.** Rows are missing because `VisualRegressionTests` renders
every case with `new RuntimeOverrides()` — no `Data` — and per
`RuntimeOverrides.Data`'s own doc comment, "Null means render with no data
(structure and static content only)." That's intentional push-model behavior
(confirmed via `git stash`: identical on unmodified `main`, so this predates
this session), not a bug: the real-Crystal reference images were rendered
from the .rpt's own *embedded/saved* sample rows, which this repo's converter
deliberately never extracts (see `RuntimeOverrides`' "two known gaps" note —
data comes from the caller, not from the .rpt). Comparing an intentionally
empty render against a data-bearing reference is an apples-to-oranges test.

**Why this matters more than it looks**: the aggregate similarity score
barely moves either way — `SalesByCustomer-Grouped` went from a ~1KB
essentially-blank PDF (94ish%) to an 88KB PDF with real, correct content
(97.8%) after the `ReportItems` fix below, a *smaller* number despite being a
massive real improvement, purely because a blank page coincidentally matches
a mostly-white reference about as well as a correctly-rendered one with
genuine font/anti-aliasing differences. The score is not a reliable pass/fail
signal here — always render and look, per this file's existing precedent.

**Not fixed this session**: making the suite push real representative data
(either by reverse-engineering the .rpt's embedded saved-data records, or by
hand-transcribing a small dataset per corpus file from its reference image)
would make the comparison meaningful and would very likely surface more real
bugs the same way the fixes below were found — every bug in this section was
found by *looking at a real render*, not by the score. Worth doing before the
next fix pass.

**Update, later in the same session**: that "not a bug" conclusion was too
hasty — it only covers `VisualRegressionTests`, which genuinely pushes no
data. The Avalonia demo (`samples/.../MainWindow.axaml.cs`) *does* push a
real one-row `DataTable` via `RuntimeOverrides.Data` (deliberately marked
`"ZZZ-PUSHED-CUSTOMER-ZZZ"` so a real row is easy to spot), and the user
confirmed — by looking at the live viewer, not a score — that the row still
didn't render correctly. That was a real, separate, confirmed bug; see below.

### Details `<Table>` has no `Top` — collides with Report Header content whenever there's real data to show

**Implemented.** Root cause of the above: `WriteDetailsTable` never emitted a
`<Top>` for the `<Table>` element, so it defaults to `Top=0` — the exact same
Body-relative position as the Report Header's title/logo/tagline block
(itself correctly at `Top=0`, since it's meant to be the first thing on the
page). With no data this was invisible (empty table, nothing to collide with,
per the entry above); the instant real rows exist, they render stacked
directly on top of the title. Confirmed via the Avalonia demo's pushed row: it
appeared, but jammed into the "Customer List" title/tagline area instead of
below the page-header column labels.

Fixed by computing the total height of the Report Header section(s) in
`WriteBody` (`report.Sections.Where(s => s.Type == SectionType.ReportHeader).Sum(s => s.HeightTwips)`,
0 when there is none) and passing it to `WriteDetailsTable` as an explicit
`<Top>` on the `<Table>` element — pushing it down below the Report Header
block instead of overlapping it. `Top` is a generically-handled `ReportItem`
element in the engine (confirmed in `ReportItem.cs`), so this needed no
engine-side change. Verified by reproducing the exact demo scenario (same
pushed `DataTable`, same file) in isolation: the row now renders below the
logo/title, and the tagline (now the Table's own `Footer`, see the position
fix above) correctly follows right after it instead of overlapping the title.

**Known minor residual, not fixed**: the demo's placeholder text
(`"ZZZ-PUSHED-CUSTOMER-ZZZ"`) is long enough to visually overflow into the
neighboring column — a text-overflow/column-width cosmetic issue with the
deliberately-oversized test string, not a positioning bug. Not investigated
further since it's specific to that placeholder value.

### Two fatal (Severity 8) converter bugs found via a full-corpus scan — both universal, not file-specific

Triggered by chasing the CustomerList investigation above into a full sweep:
wrote a throwaway tool that runs every one of the 88 public `tests/rpt-corpus`
files through `RptParser` → `RdlConverter` → the real
`Majorsilence.Reporting.RdlEngine` (`RunGetData` + `RunRender`) and reports
`Report.ErrorMaxSeverity`. **55 of 88 files (63%) hit a fatal error** — a
Severity-8 `LogError` doesn't just skip the one broken thing, it cascades:
once `MaxSeverity` hits 8, later, *unrelated* expression evaluations across
the whole render start throwing `NullReferenceException` (logged as more
Severity-4 noise), so one bad section can quietly blank out an entire page
that would otherwise render fine. `VisualRegressionTests`' aggregate score
didn't flag any of this (see above) — these were only found by checking
`ErrorMaxSeverity` directly and by looking at actual renders.

1. **Empty `<ReportItems>` is fatal, and it wasn't confined to `PageHeader`.**
   The engine's `ReportItems` constructor (`RdlEngine/Definition/ReportItems.cs`)
   logs Severity 8 — "At least one item must be in the ReportItems." — the
   instant a `<ReportItems>` element parses to zero recognized children.
   `WritePageHeader`/`WritePageFooter`/`WriteBody` all wrote it unconditionally,
   but a section can have `Objects.Count > 0` and still emit nothing —
   `WriteFreeFormObjects`'s switch silently skips unresolved embedded images,
   subreports with no linked report, and cross-tabs missing a row/column/cell
   axis. Fixed with a shared `HasRenderableContent(Section)` predicate mirroring
   those same skip conditions: `WritePageHeader`/`WritePageFooter` now omit the
   whole section (confirmed optional at the Report level — the engine
   null-checks `_ReportItems` everywhere) rather than emit an empty shell, and
   `WriteBody` only opens `<ReportItems>` when the Details table or at least
   one free-form section actually has something real to show.
2. **`ValidValues` wrapped in a `<NonQueried>` element the engine doesn't
   recognize.** `WriteReportParameters` emitted
   `<ValidValues><NonQueried><ParameterValues>...` for every parameter with a
   Crystal pick-list. Real SSRS 2008+ uses `<NonQueried>`; this engine's
   `ValidValues.cs` only recognizes `DataSetReference` or `ParameterValues` as
   *direct* children — the unknown `NonQueried` wrapper gets skipped (Severity
   4, "Unknown ValidValues element"), so `ParameterValues` never attaches,
   both `_DataSetReference` and `_ParameterValues` stay null, and the ctor logs
   Severity 8 ("...either DataSetReference or ParameterValue must be
   specified, but not both" — misleading wording; it also fires when *neither*
   is present). Fixed by dropping the `NonQueried` wrapper. This alone affected
   nearly every `boyum__*` (SAP Business One template) file in the corpus —
   any parameter with a static pick-list.

Verified corpus-wide: both exact error messages ("At least one item must be
in the ReportItems." / "ValidValues element either DataSetReference...") now
have **zero occurrences** across all 88 files (previously ~15+ files each).
The overall "55 fatal" count didn't move much because most of those files
carry *several independent* fatal issues (see next entry) — fixing one doesn't
clear a file that has three — but each of these two specific, confirmed bugs
is gone corpus-wide. `benbrahim777__SalesByCustomer-Grouped.rpt` (this
repo's own visual-regression suite) went from an ~1KB blank-page PDF to an
88KB fully-rendered one as a direct result of fix #1.

### `IsNothing`/`isnull()` Crystal formulas fatal-crash the target engine (fixed in Majorsilence.Reporting)

Found via the same corpus scan: `benbrahim777__Canada-CrossTab.rpt` hit
Severity 8 — `Expression '=IIf(IsNothing(Fields!Region.Value), 2, 2)' failed
to parse: Function IsNothing is not known.` `RdlEmitter.FunctionMap` maps
Crystal's `IsNull`/`IsNullOrEmpty` to VB.NET's `IsNothing`, a real VB.NET
*language* construct — but this engine resolves bare function calls purely by
reflecting for a matching static method on `VBFunctions`
(`Parser.cs` → `XmlUtil.GetMethod`), and no such method existed. `Is Nothing`
as an operator only exists internally for one narrow aggregate-scope-argument
check (`Identifier.IsNothing`), not as a general expression.

**Fixed in the engine, not by avoiding the call**: added
`VBFunctions.IsNothing(object value) => value == null || value is DBNull;` —
real support, not a defensive rewrite — since the mapping itself is correct
Crystal→VB.NET semantics; the target function just didn't exist yet. One
static method, `object`-typed so reflection's exact-match `GetMethod` matches
any runtime argument type (mirrors `IsNumeric(object)` already in the same
file). Verified: `Canada-CrossTab.rpt`'s `MaxSeverity` dropped from 8 to 4
(only the pre-existing benign "no DataSource"-style Severity-4 warnings
remain); full 271-test `ReportTests` suite on net10.0 still green; the fix
applies uncommitted on `dev` (Reporting repo — user's call whether/when to
commit, per this project's standing convention).

### `Sum(expr, groupFieldExpr)` — scope argument is a field reference, not a group name

**Fixed, but not the way it first looked.** Crystal's "sum grouped by field"
shorthand (`=Sum(Fields!ORDER_AMOUNT.Value, Fields!CUSTOMER_NAME.Value)`)
passes the group-by field as `Sum`'s 2nd argument. The naive transpile passes
that field reference straight through, and RDL's `Sum(expr, scope)` requires
`scope` to be a *constant*, so the engine rejected it: `"{0} function's scope
must be a constant."`

The first fix attempt resolved the field to the matching declared group's RDL
name (`Sum(Fields!ORDER_AMOUNT.Value, "Group2")`) — syntactically a constant,
so it should have worked. It didn't: the engine now failed with `"Scope
'Group2' does not reference a known DataSet."` Reading
`RdlEngine/ExprParser/Parser.cs` (`~line 596-612`) explains why — for every
aggregate *except* `RunningValue`, a quoted scope is resolved via
`idLookup.ScopeDataSet(...)` and must name an actual **DataSet**; only
`RunningValue`'s scope argument is treated as a Grouping name. This engine
simply doesn't support a Grouping-name scope on `Sum`/`Count`/`Avg`/etc. —
there's no constant string that would have made the original fix's approach
work.

Separately, the two files that surfaced this (`souvikduttachoudhury__
StatementOfAccount.rpt`, `benbrahim777__USA-Orders-RWB-colored.rpt`) both
place the offending formula as a flat `<DataSet><Fields><Field><Value>`
calculated column — every current formula-field usage in this converter
routes through the DataSet this way, never inlined at the report-item's
actual point of use. A DataSet field is evaluated per-row, before any
`TableGroup` rendering context exists, so *no* scope argument — Grouping name
or otherwise — could ever be valid there; the placement itself, not just the
scope value, rules out a real per-group total for this class of formula.

**Actual fix**: drop the scope argument entirely and emit the unscoped 1-arg
form (`Sum(Fields!ORDER_AMOUNT.Value)`) whenever `Sum`/`Count`/`CountDistinct`/
`Avg`/`Min`/`Max`/`First`/`Last` is called with a 2nd argument that's a plain
field/column reference (`RdlEmitter.EmitFuncCall`, using the existing
`TryGetPlainColumnName`/`GetTwoArgNodes` helpers). This isn't a fully faithful
translation — Crystal's per-group total becomes a report-wide grand total
when evaluated in this flat DataSet-field context — but it turns a
corpus-blocking fatal error into a renderable (if occasionally imprecise)
report, which is the same tradeoff already accepted elsewhere in this backlog
(e.g. the Left/Top-position workaround above). The earlier, incorrect
attempt's group-name-matching plumbing (`[ThreadStatic]` group field list
threaded from `RdlConverter` through `FormulaTranspiler`/
`CrystalFormulaParser`/`RdlEmitter.Emit`) was removed along with it, since
groups never entered into the correct fix.

Verified: full 843-test suite green; full 88-file corpus scan's fatal-file
count dropped from 53 to 51 (both files above cleared, zero new fatals
elsewhere — confirmed by diffing the fatal-file set before/after, not just the
count); visual-regression suite still 5/6 (same pre-existing, documented
`Top5USAsubCanada` page-2 failure as always).

### Remaining formula-transpilation gaps found by the same corpus scan (not fixed — separate, larger effort)

Distinct from the two fixes above; each of these is its own root cause and
would need its own investigation:

- **Bare `Table.Column` identifiers inside Crystal formula text** (e.g.
  `=CUSTOMER.COUNTRY`, `=ORDERS.ORDER_AMOUNT`, `=FINANCIALS.BUILDINGS`) pass
  through the transpiler unresolved — the RDL expression grammar has no
  concept of a bare qualified identifier like that; it needs rewriting to
  `Fields!Column.Value`. Affects most `souvikduttachoudhury__*` files.
- **Bare `@ParameterName` references** (e.g. `=@DateToAgeBy`, `=@X_Language`)
  — look like un-transpiled Crystal parameter syntax (`{?Name}` → should
  become `Parameters!Name.Value`) left as literal `@Name` text. Affects most
  `boyum__*` files (SAP Business One templates lean heavily on parameters).
- **Functions present in Crystal formulas but missing/mismatched in
  `VBFunctions`**: `CDec`, `Color`, and notably `Month` — `Month(DateTime)`
  *does* exist, but the engine resolves function calls via an exact-type
  reflection `GetMethod` (`Parser.cs`: `argTypes[i] =
  XmlUtil.GetTypeFromTypeCode(args[i].GetTypeCode())`), and a `Fields!X.Value`
  argument's inferred `GetTypeCode()` doesn't resolve to `DateTime` unless the
  DataSet field itself carries type information our converter doesn't
  currently emit — so `Month(Fields!STATEMENT.Value)` fails to bind even
  though a same-named method exists. Likely affects every strongly-typed
  `VBFunctions` overload (`Year`, `Day`, `Weekday`, ...) whenever called on a
  field reference rather than a literal. `IsNothing`/`IsNumeric` dodge this by
  taking `object`; the general fix is probably emitting field type info into
  the RDL `<Field>` definitions, not adding more `object` overloads one at a
  time.
None of these were attempted this session — flagging them here, with the
exact failing expressions, so the next pass doesn't have to re-derive them
from scratch. (Formula-*language* feature gaps like multi-line `Select`/`Case`,
`//` comments, string-slicing, and expression-level `if/then/else` turned out
to already be handled or have since been fixed — see below.)

### Crystal string-slice syntax (`Field[n]` / `Field[n To m]`)

**Fixed.** `souvikduttachoudhury__CustomerProfileReport.rpt` hit `Invalid
function arguments. Found '[' At column 33` on
`{CUSTOMER.CUSTOMER_NAME}[1 to 3]`. Crystal's postfix `[n]` (single character,
1-based) / `[n To m]` (inclusive substring) has no equivalent RDL expression
syntax at all — this needed a real grammar addition, not a mapping fix.

Added a `sliceExpr` rule to `CrystalFormulaGrammar` (`primary + "[" + expr +
"]"` and `primary + "[" + expr + "To" + expr + "]"`, both alternatives folded
into `primary` itself so slicing applies to any string-valued primary —
`{Table.Column}`, `{@Formula}`, a parenthesized sub-expression, etc.). `[`,
`]`, and `To` were already punctuation (reused from the existing `In [...]`
list syntax and `Case` value ranges), so no grammar-error/conflict risk there.
`RdlEmitter` maps both forms onto VB.NET's existing `Mid(str, start, length)`
— same 1-based `start` convention as Crystal, so the index needs no shifting:
`Field[5]` → `Mid(Field, 5, 1)`, `Field[1 To 3]` → `Mid(Field, 1, (3) - (1) +
1)`. `Mid(string, int)` / `Mid(string, int, int)` already exist in
`VBFunctions.cs`, so no engine-side change was needed this time.

Verified: full 843-test suite green; full 88-file corpus scan's fatal-file
set unchanged in every file except this one (51 → 51 net, this file's
`Invalid function arguments`/`'['` error gone; it still fails for an unrelated
reason — bare lowercase `region`/`phone` field references not resolving to
`Fields!region.Value`, a separate gap, not a slicing one); visual-regression
suite still 5/6 (same pre-existing `Top5USAsubCanada` failure).

### Non-Sum group footer aggregates
**Implemented.** The earlier hypothesis (tag-237 → tag-236 child, byte 22 =
function code) was wrong — tag-237 is a per-object *field format* record that
appears inside every object wrapper, and byte 22 of its tag-236 child is 0x01
for plain fields too. The real mechanism: a summary FieldObject's tag-159
wrapper embeds its field reference as a plain MUTF-8 string of the form
**"&lt;Function&gt; of Table.Column"** (e.g. "Sum of Orders.Order Amount",
"Count of Employee.Code"), followed by a small metadata block whose byte 2
tracks the group level. Observed prefixes across a large real-world corpus:
Sum, Count, DistinctCount, Max, Min (Average/StdDev/Variance mapped too).

The parser splits the prefix into `FieldObject.SummaryFunction`; the converter
emits the matching SSRS aggregate (`Count`, `CountDistinct`, `Avg`, `Max`,
`Min`, `StDev`, `Var`) in group footer cells, fills group-header columns from
matching summary FieldObjects (Crystal often places counts there), and wraps
free-form (report header/footer) summaries as whole-DataSet aggregates. This
also fixes the previous behaviour of emitting `Sum()` over *string* columns
whenever any group footer existed.

**Caveat**: the function prefix is an English literal in the file; reports
authored with a localized Crystal Designer would carry translated prefixes and
fall back to the numeric-column `Sum()` heuristic.

**Percentage-of-total summary — implemented.** Found via a new public-corpus
file (`souvikduttachoudhury__CustomerProfileReport.rpt`): Crystal's "Percentage
of Total" summary is a *compound* prefix — `"Percentage of <Function> of
Table.Column"` (e.g. `"Percentage of Sum of ORDERS.ORDER_AMOUNT"`) — which the
single-prefix parser didn't recognize at all, silently falling through to an
unparsed, polluted table name and no summary function (would have rendered
the raw column value instead of a percentage). `ParseSummaryPrefix` now
detects and strips the `"Percentage of "` wrapper first, recursing to resolve
whatever function/table chain remains (the inner function itself is
discarded — RDL emission always divides by the DataSet-wide sum via
`AggregateFunction.Percentage`'s two-part expression
`=Sum(...) / Sum(..., "DataSet1") * 100`, since Crystal's optional custom
"divide by" field isn't otherwise distinguishable here).

**Bug found and fixed along the way**: Crystal allows two summaries of the
*same* underlying column side by side in one group footer (here, both a plain
`Sum` and a `Percentage` of `ORDER_AMOUNT`) — the table-column model only has
one cell per column name, and the column-matching loop's `FirstOrDefault`
silently picked the first one, leaving the second orphaned with no path to
ever be emitted. Fixed by extending the existing "leftover positioned item"
overflow mechanism (already used for subreports/images/charts that don't fit
a table cell) to also catch orphaned `Percentage` fields.

**Two more bugs found and fixed, discovered by actually rendering converted
RDL through the target engine (not just schema-checking it) while building an
unrelated downstream render-engine prototype:**

- `RptParser.ParseFieldObject`/`ParseTextObject` extracted each object's real
  Crystal-assigned name (via the same generic tag-158 `ExtractObjectName`
  helper Line/Box/Image/Subreport already use) but then **discarded it** —
  `ParseFieldObject` reused the same local variable for the field reference,
  overwriting the object name before it reached the constructor;
  `ParseTextObject` only used it as fallback display text, never as `.Name`.
  Net effect: `FieldObject`/`TextObject` — by far the two most common object
  types — always had an empty `ReportObject.Name`, making any by-name
  reference to one of them (e.g. a runtime suppress/resize/move/text
  override) impossible. Fixed by capturing the extracted name separately and
  assigning it to `Name` on both constructors. Verified byte-identical
  corpus-wide behaviour otherwise: the full private-corpus output file list is
  unchanged (diffed old vs. new parser, identical 3,222 files), and both
  corpora still convert/verify 100% clean.
- `RdlConverter`'s `TableGroup` sort-order emission wrote a bare
  `<SortExpressions>` directly under `<TableGroup>` — not a schema element
  that container recognizes at all (only `Grouping`/`Sorting`/`Header`/
  `Footer`/`Visibility` are, confirmed from the engine's own `TableGroup.cs`).
  The engine silently ignored it as an "unknown element" warning (Severity 4,
  not Error/Fatal — invisible to every prior Error/Fatal-only verification
  pass), meaning **every grouped report's sort direction has been dropped at
  render time** until now. Fixed: now emits
  `<Sorting><SortBy><SortExpression>/<Direction></SortBy></Sorting>` (the
  same shape used for `<Details>`'s sort order). Confirmed at scale: 2,202
  private-corpus RDLs now correctly emit `<Sorting>` where they previously
  emitted the silently-dropped `<SortExpressions>`.

---

### Section-level suppress formula
**Implemented.** After the tag-254 child block, the tag-255 SectionProperties
payload holds a sequence of *formula hook entries* — one per formula-drivable
section property, in tag-254 flag order (entry 0 = suppress, 2 = newPageBefore,
3 = newPageAfter, 9 = background colour, …). Each entry is a MUTF-8 formula
name (empty when no formula is attached, e.g. `@Section_Visibility`) plus 3
trailer bytes. The referenced formula is an ordinary tag-119 definition whose
text the parser already decodes; all formula texts are now recorded by name
(including internal ones not exposed as fields) so the section can resolve its
suppress formula to `Section.SuppressFormula`.

The converter transpiles it and emits `<Visibility><Hidden>=expr</Hidden>` on
the details row and group header/footer rows (and per-item in free-form
sections — page header/footer, report header/footer). The formula supersedes
the static suppress bit when both are present — Crystal keeps the stale
checkbox value set alongside an attached formula, so static-wins would
permanently hide the section. Untranspilable formulas (variable-based, `=""`
fallback) emit no Visibility rather than hiding content.

**newPageBefore/After formulas — implemented.** Emitted as `<PageBreakAtStart>`
/ `<PageBreakAtEnd>` + `<PageBreakCondition>` on the `TableGroup`'s `<Grouping>`
element (confirmed valid there via the engine's own `Grouping.cs`; **not**
valid on `<Details>`, which silently ignores those two elements entirely — a
separate pre-existing latent bug in this converter's Details-level static
page-break emission, now identified but not yet fixed). RDL allows only one
`PageBreakCondition` per `Grouping`; when a group has formulas on *both*
directions, the before-formula wins (rare in practice). Validated with a
targeted unit test (real corpus examples found so far all resolve to `null` —
one had no formula body at all, meaning the option was set via the plain
checkbox with no custom condition; another used a Crystal function
(`OnFirstRecord`) the formula transpiler doesn't map — both are correct,
safe "no override" outcomes, not bugs).

**Bug found and fixed along the way**: when a group's footer/header area is
split into multiple named sub-sections (Crystal's "Section B", "Section C",
etc. — seen as e.g. `GroupFooterArea1` containing both `TSection9` and
`TSection10`), the formula-hook table can be attached **once at the area
level** rather than duplicated per sub-section — the per-section table is
present but entirely empty in that case. The area-level tag-255 was
previously skipped outright (comment said "Skip SectionProperties at area
level"); it's now parsed and its hooks fall back onto every section in the
area when that section's own table has none.

**Back colour formula — parsed, not yet emitted.** `Section.BackColorFormula`
is fully wired on the parser side (same entry-9 hook). Converter emission
needs more design work: the target engine's `TableRow` element does not
support `<Style>` at all (confirmed from its own definition source) — a
row-level background colour would need to propagate into every `TableCell`'s
own style individually. Deferred rather than shipped half-done.

---

### Parameter pick-lists and validation ranges
Crystal Reports parameters can carry an allowed-values list or a range
constraint. These are stored in the parameter field definition records near
tag-122/123 (adjacent to tag-113 which already gives name+type).

**Implementation**: The tag-122 record's raw bytes are scanned for MUTF-8
strings (BE-Int32 length + UTF-8 + null terminator). The pick-list entries are
identified as the longest consecutive run of such strings (each starting
immediately where the previous ended), filtering out the parameter name, its
bare form (without `@`/`$`/`[`/`]` sigils), COM object refs (`crobj://`),
prompt text (ends with `:`), and dot-notation field references. A value+label
pair heuristic detects when the list is even and each second-half string is a
longer form of its first-half counterpart. The bug that caused all parameter
fields to be silently dropped (two-level `ParseChildren` grandchild lookup when
tag-113 IS the direct child of tag-122) was also fixed. Parameter type code 6
is mapped to String for parameters (not Currency as for DB fields).

The RDL converter now always emits `<Prompt>` and adds
`<ValidValues><NonQueried><ParameterValues>` when pick-list is non-empty.

**Status: done** — implemented, tested against 85-file corpus (benbrahim777 +
Boyum IT), 5 targeted unit tests added.

---

### Image / OLE picture objects
**Implemented.** Two distinct object types, both decoded from corpus binaries:

- **tag 175/176 = PictureObject** (static embedded image — previously
  mis-guessed as a chart tag). The wrapper contains the usual nested tag-158
  bounds record; a flat tag-189 record before the end tag holds Int32 BE at
  offset 0 = index N of the OLE storage `Embedding N`, whose `CONTENTS` stream
  is the raw image file (BMP observed most often). The parser resolves the
  bytes, sniffs the MIME type (bmp/png/jpeg/gif), and the converter emits an
  RDL `<EmbeddedImages>` entry plus an `<Image Source="Embedded">` item.
- **tag 177/178 = BlobFieldObject** (database blob rendered as image —
  barcodes, photos). The wrapper payload embeds the `Table.Column` reference;
  the converter emits `<Image Source="Database">` with
  `=Fields!column.Value` (MIMEType defaults to image/bmp).

Images in detail sections become extra table columns; images in free-form
sections (page/report header/footer) are positioned report items.

**Metafiles**: WMF (placeable `D7 CD C6 9A` / standard `01 00 09 00`) and EMF
(EMR_HEADER with " EMF" signature at header offset 40, sometimes behind a
small prefix) are rasterized to PNG on Windows via System.Drawing/GDI+
(`WmfRasterizer`, capped at 2000×2000, white background) and embedded as
`image/png`. OLE "package" embeddings without a `CONTENTS` stream fall back to
the metafile inside their `\x02OlePres000` presentation stream. On
non-Windows platforms metafile logos are still skipped with a warning.

---

### RepeatGroupHeader binary bit
**Implemented.** Not in tag-254 (a byte-level variance scan of its
undocumented tail, offsets 29–52, found no variance on GroupHeader records
across a large real-world corpus — PageFooter byte[30], Detail byte[45], and
ReportFooter byte[46] varied instead, for unrelated properties not yet
identified). The real bit lives in **tag-229**, the group-condition record: a
2-byte slot immediately after the known `Int16 condCode` + `Int16 sortCode`
fields, right before the `"Others"` MUTF-8 strings. A corpus-wide scan
(`crystalcli scan`'s `group-condition-tail` detector) of 3,350 real report
groups found `{0x0000 ×3035, 0x0202 ×296, 0x0101 ×19}` — zero variance in the
85-file public corpus, all variance in the private corpus, concentrated in
multi-page financial-statement/budget report templates where repeating the
group header on each page is a common real need. Treated as a boolean
(either non-zero value → repeat);
the two distinct non-zero values *might* be separate related options that
happen to always be set together in this corpus (e.g. RepeatGroupHeader vs.
"reprint after horizontal page break") — not fully disambiguated, but the
risk is low (worst case, a cosmetic `RepeatOnNewPage` set when a different,
closely-related option was actually intended).

Parser sets `GroupDefinition.RepeatGroupHeader` in `ExtractGroups`, propagated
to the matching `GroupHeader` `Section` by level; converter emits
`<RepeatOnNewPage>` on the `TableGroup` `Header` (previously always
hardcoded `false`).

**Bug found and fixed along the way**: tag-229 is shared by real report
groups (marked `"@Group #N Order"`) and cross-tab/chart axis definitions
(marked `"@Row #N Order"` / `"@Column #N Order"` / `"@Detail Value Grid #N
Order"`) — `ExtractGroups` had no marker check, so any report with a
cross-tab or chart got **phantom groups** injected into `report.Groups` for
each axis/category field (confirmed on `Canada-CrossTab.rpt`, which has no
real grouping at all but produced 2 bogus `GroupDefinition` entries). Fixed
by requiring the `"@Group #"` marker.

---

### Object-level conditional formatting (tag 266–270 bracket)
Every report object wrapper is immediately followed by a flat `266 … 267`
bracket (not nested inside the object's own children). Initial hypothesis was
that this mirrors the tag-255 section formula hooks — i.e. a per-object
suppress/colour *formula* reference.

**Investigation result**: this is not a formula hook. The bracket's contents
are fixed-size numeric records (tag 269, len 22 = one per object; tag 274,
len 30 = one per "format slot", repeated 0–22+ times depending on object
complexity) with **zero embedded MUTF-8 strings** — checked across 6,471
brackets total (144 in the public corpus, 6,327 in a large real-world private
corpus). A corpus-wide per-offset byte histogram shows only 3–4 offsets in
each record type ever vary at all, and every varying offset has one dominant
value (1,000+ occurrences) with a narrow range (e.g. 0x00–0x26, 0x00–0x5F) —
the signature of small ordinal/slot indices, not colours or thresholds (which
would spread across the full byte range with no dominant clustering). The
`274` count scales with object complexity (more columns/fields → more slots),
consistent with a fixed per-object-type template of format-property slots
(e.g. Crystal's internal Format Editor tabs) rather than user-configured
Highlighting Expert conditions.

**Status: dead end, closed** — no extractable data; not a viable conversion
target. The `object-format-hook` scan detector is kept in `crystalcli scan`
in case a future corpus file reveals different bracket content.

---

## Significant effort (multi-session projects)

### Subreports
**Implemented.** The placed subreport object wrapper is **tag 163/164**: the
nested tag-158 child carries bounds and the subreport name, and the Int32 BE
immediately after the tag-158 block (8-byte header + data length) is the index
N of the `Subdocument N` OLE storage. That storage contains its own `Contents`
stream, parsed recursively by the existing TSLV pipeline (images inside
subreports resolve against `Subdocument N/Embedding M`; nesting is capped at
3 levels). The converter emits an SSRS `<Subreport>` element whose
`<ReportName>` is `<parentStem>_<SubreportName>`, and the CLI `convert` verb
writes each inner report as a companion `.rdl` under that name next to the
parent.

**Parameter links (implemented as a heuristic)**: Crystal stores the actual
parent→child link table in encrypted streams (PromptManager/QESession), but
linked child parameters are conventionally named after the parent thing they
bind to — `Pm-Table.Column` (wizard links), `@FormulaName` (formula links), or
the bare parent field/parameter name. The converter resolves each child
ParameterField against the parent's formula fields, DB columns, and
parameters, emitting `<Parameters>` bindings inside `<Subreport>`
(`=Fields!X.Value` / `=Parameters!X.Value`). Unresolvable names (custom-named
links, e.g. some third-party report packs) stay promptable.

**Placement**: subreports/images in group header/footer sections of tabular
reports are placed into empty group-row cells when available, otherwise
emitted as positioned body items after the table (may visually overlap — an
acceptable fidelity trade-off vs dropping them). Free-form section items now
receive per-item `<Visibility>` from static or formula suppression.

**Newly found gap — subreport content isn't rendering at all in at least one
real case.** Found via the visual-regression harness (`tests/Majorsilence.Crystal.VisualRegression.Tests`,
comparing against real-Crystal-rendered references in `tests/reference-renders/`):
for `benbrahim777__Top5USAsubCanada.rpt`, the real engine's PDF has 2 pages
(page 1 a chart, page 2 the "Canadian Orders" subreport table + an embedded
flag image); our converted RDL's rendered PDF has only 1 page — the
subreport's content is entirely absent, not just placed differently. The
`<Subreport>` element and its companion RDL are present and structurally
correct (confirmed via `RenderPrep.ConvertWithSubreports`), so this looks like
either a page-break/pagination gap (the subreport section not forcing its own
page) or the subreport not being invoked by the render engine for this
report's specific structure. Not yet root-caused — the visual-regression test
for this case is left intentionally failing (not skipped) as a tracked,
visible gap rather than silently passing.

**Remaining gaps — on-demand subreports, investigated further.** Not a
separate tag pair — it's a flag *within* the existing tag-163 wrapper.
Isolated by diffing `benbrahim777__USAvsFranceOnDemand.rpt` against two
structurally-identical non-on-demand subreports: **byte[88]** of the tag-163
payload is `0x01` only in the on-demand file, `0x00` in the others, and this
holds cleanly across the entire corpus **for the dominant 107-byte wrapper
shape** (0 false positives in 835 private-corpus instances of that shape,
plus the 1 public-corpus positive). The wrapper isn't fixed-length overall,
though — other shapes exist (105, 108–119, 126, 127, 146 bytes, presumably
carrying extra data for linked-parameter subreports), and absolute offset 88
does **not** land on a clean boolean for most of those (e.g. length 127 reads
`0x65`, length 126 reads `0x70` — filler, not a flag); a `Length - 19`
end-relative offset was tried as an alternative and didn't resolve those
either. So the byte is confirmed for the common case but not for every shape.

Moot regardless: the target engine's `Subreport` definition supports only
`ReportName`, `Parameters`, `NoRows`, and `MergeTransactions` — no
interactive/on-demand rendering concept exists to map this to, the same
category of gap as ResetPageNumber and Crystal variables (see "Upstream
work planned" below). Not implemented; the `subreport-ondemand-byte` scan
detector is kept for whoever eventually resolves the non-107-byte shapes.

### Cross-tab / OLAP grid objects
**Implemented (v1).** The tag-185/186 cross-tab wrapper contains, in order:
grid-geometry records (tag 323 + a run of tag-325 + tag 324, not needed for
conversion), one block per axis group — tag-206 → 223 → **tag-229** (the
standard group-condition record whose payload also carries an axis marker
string `"Row #N Name"` / `"Column #N Name"`) → 230 → 224 → a label object —
and **tag-161/162 cell objects**, each wrapping a nested tag-159 whose field
reference is either an axis placeholder or a summary (`"Sum of Table.Column"`,
same prefix scheme as summary FieldObjects; repeated total-cell references are
deduplicated).

The parser produces `CrossTabObject { RowGroupFields, ColumnGroupFields,
Cells(field, function) }`; the converter emits an SSRS 2005 `<Matrix>` with
dynamic row/column groupings and the aggregate cell expression.

**v2**: all row/column group levels and all cells are now emitted. RDL Matrix
nests `<ColumnGrouping>`/`<RowGrouping>` elements outermost-first in document
order (one per axis field); multiple cells become an extra innermost *static*
column level (`<StaticColumns>`, one `<StaticColumn>` per cell, labelled
`"<Function> of <Field>"`), with one `<MatrixCell>`/`<MatrixColumn>` per cell
(confirmed against the engine's own cardinality rule: MatrixCells/MatrixColumns
count must equal `max(1, ColumnGroupings.StaticCount)`). Verified schema-valid
against the actual Majorsilence.Reporting engine parser via a synthetic
2-row-level × 1-col-level × 2-cell report (no corpus file exercises more than
1 row field × 1 column field × 1 cell — the public corpus's "BigCells" files
are large *grids*, not deeper axes or multiple metrics — and the private
corpus contains **zero** cross-tab objects at all).

**Grand totals / corner label — investigated further, still inconclusive.**
Each axis's tag-229 group-condition record is preceded by a paired tag-229
record with no field reference, carrying two `"Others"` strings. Initial
hypothesis was that this pair's presence indicates a grand total is enabled,
but "Others" is also Crystal's label for its unrelated "group remaining
values as Others" cross-tab option, and every corpus cross-tab file has the
pair regardless — with no counter-example (a cross-tab confirmed to have
grand totals *disabled*), the signal can't be attributed to either feature
with confidence.

A real-Crystal-rendered reference image is now available for one cross-tab
file (`tests/reference-renders/benbrahim777__Canada-CrossTab/real-crystal-page-1.png`,
from the new visual-regression harness) confirming grand totals **are**
enabled for that file (a `Total` row and, off the visible page, presumably a
matching `Total` column) — a useful positive data point, but not by itself the
disambiguating negative counter-example (a cross-tab confirmed *off*) this
section still needs, since every known cross-tab file already carries the
"Others" pair regardless.

Re-examined the full tag-185…186 block for other candidates: tag 167/168
(x4, previously guessed as "a label object") is in fact the same generic
per-object idle format-slot template already established as a dead end for
tag 266–270 and tag 253 — byte-identical filler, not a label. A new
single-occurrence record, **tag 382/383** (4 bytes, positioned right before
the block's closing tag-186 — a plausible "grid options" record), was found
but reads all-zero in the one available sample, giving no directional
signal without a non-default counter-example. No tag-165 (TextObject) exists
inside the block in any corpus file, so the corner cell likely has no
distinct object when left blank (the common default) — consistent with,
but not proof of, either hypothesis.

**Status: still needs a disambiguating corpus file** — specifically a
cross-tab with grand totals confirmed *off*, or one with explicit corner
text, to determine whether tag 382/383 (or the "Others" pair) is the totals
flag and to locate a real corner-label object if one exists.

### Charts / graphs
**Implemented (v1 — field-bound charts).** The tag candidates previously
listed here (170/171, 172/173, 175/176) are Line, Box, and Image/PictureObject
wrappers (see above), not charts. The real chart wrapper is **tag 180/181**,
identified from scratch this round via MUTF-8 name strings ("Chart1"/"Graph1")
found three levels deep in its nested bounds record (180 → 179 → 174 → 158 —
one level deeper than every other object type). Confirmed present in 15
public-corpus files and 8 private-corpus files (`crystalcli scan`'s
`chart-object` detector).

Flat sibling records between 180 and 181 carry the chart's real content —
verified against both corpora, not guessed:
- **tag 284** (5 bytes): byte[2] is the chart-type discriminator. `0x01` was
  confirmed across 15 independent samples (all rendering as pie charts by
  filename/context); `0x02` was seen once, on a chart auto-generated from a
  cross-tab (Crystal defaults such charts to a bar/column layout, but this is
  a single unconfirmed sample). Byte[4] is an unrelated per-object ordinal
  index (increments across charts in the same file). Any value other than
  `0x01` defaults to Column.
- **tag 289**: the chart's title (first MUTF-8 string), the bare category
  (X-axis) field name (second string), and an *unqualified* `"<Function> of
  Column"` series reference (third string — fallback only, ambiguous when the
  column name itself contains " of ").
- **tag 287** (when present): the *fully-qualified* `"<Function> of
  Table.Column"` series reference — same prefix convention as summary
  FieldObjects (`ParseSummaryPrefix`) — and is preferred over tag 289's third
  string.
- **tag 253** and the other siblings (9, 237, 296, 284's low bytes, 288, 285,
  297) are the same generic per-object idle format-slot template already
  established as a dead end for tag 266–270 — byte-identical across every
  chart regardless of type or fields, carrying no chart-specific content.

The converter emits an RDL `<Chart>` with a single dynamic `CategoryGrouping`
and one `ChartData/ChartSeries` value expression — schema confirmed directly
against the Majorsilence.Reporting engine's own `Chart`/`ChartData`/
`DynamicCategories` definition source (not an assumed/guessed schema), and
verified end-to-end through the real engine parser in
`EngineCompatibilityTests`.

**Implemented — on-change-of-group charts.** The other Crystal chart
data-source mode ("on change of group": the chart plots the report's own
existing group structure rather than independent field bindings) is now also
decoded, unlocking all 10 chart instances across all 8 private-corpus files
(previously only some charts in 4/8 files converted).

- **Category axis** (1 or more levels, outermost first): one flat tag-229
  group-condition record per level — the same record cross-tabs use for their
  row/column axes — each carrying a `Table.Column` field reference and an
  `"@Detail Value Grid #N Order"` marker string that distinguishes it from the
  report's own unrelated groups (marked `"@Group #N Order"` instead).
- **Series**: an *unaggregated* per-row value, nested tag-127 → tag-126
  (analogous to the tag-128 → tag-126 running-total chain, but with no
  function code — Crystal charts the raw detail value, not a summary). The
  reference is either a plain `Table.Column` or an `"@FormulaName"` reference
  to a calculated field. The converter still wraps it in `Sum()` on emission —
  required by RDL's grouped-scalar-expression rules
  regardless of Crystal's own unaggregated semantics, and harmless when each
  category combination has exactly one underlying row.
- **Title heuristic**: tag 289 holds only a single (redundant) axis-label
  string — identical to the category field's own name — when no custom title
  was set; a real title is only present when a *second* string follows it (as
  in the field-bound mode). Treating a lone string as a title in group-based
  mode produced a wrong, duplicated caption for 2 of the 4 investigated files
  and was corrected.
- **Placement bug found and fixed along the way**: `ChartObject` was missing
  from both the group-row table-cell placement switch and the tabular
  "leftover positioned body item" fallback (`RdlConverter.cs` — the same
  mechanism already handling `SubreportObject`/`ImageObject`). Every
  group-based chart lives in a `GroupFooter` of a tabular report, so this
  silently dropped **all** of them regardless of data-source mode — a bug in
  the existing chart feature, not specific to this mode.

**Not implemented — re-investigated, no further data found.** tag 287 and
tag 127 (the series-reference records) never occur more than once per chart
object across every corpus file checked (public and private) — the count
always matches the chart-object count exactly, 1:1 — so there is no
multi-series evidence to decode. tag 289's string list was re-verified
exhaustively (title, category, series, then only font names) with nothing
resembling a legend-visibility flag or a custom axis title. Multi-series
charts (RDL `SeriesGroupings`), legend, axis titles/formatting, 3D
properties, and the corner/legend colour palette remain unimplemented —
all optional per the engine's schema, and genuinely unsupported by any
available corpus evidence rather than merely deferred.

---

## Upstream (Majorsilence.Reporting engine) work planned

These two items are blocked by a missing capability in the target
**Majorsilence.Reporting** engine itself, not by information availability in
the `.rpt` format — the .rpt side is either already parsed or well
understood. Rather than working around them in this converter, the plan is to
contribute the missing capability upstream
(github.com/majorsilence/Reporting), then wire up emission here once it
lands.

### ResetPageNumber RDL emission
`Section.ResetPageNumber` is parsed from tag-254 bytes [17..18] but never
emitted — SSRS 2005 schema has no group-level `<ResetPageNumber>` element.

**Investigation result**: confirmed against the Majorsilence.Reporting engine's
own `Grouping` definition source (`RdlEngine/Definition/Grouping.cs`) — it
parses `PageBreakAtStart`, `PageBreakAtEnd`, and `PageBreakCondition` from a
group's XML, but has no `ResetPageNumber` property, element, or equivalent
anywhere.

**Status: upstream work planned** — propose adding a group-level page-number
reset feature (e.g. a `ResetPageNumber` property on `Grouping`, mirroring
`PageBreakAtStart`) to Majorsilence.Reporting. `Section.ResetPageNumber` stays
parsed here in the meantime; once the engine supports it, add emission in
`RdlConverter`.

### Crystal variable declarations (`Local NumberVar`, etc.)
Crystal multi-pass variables (`Local`/`Global`/`Shared` + `NumberVar` etc.)
have no SSRS VB.NET equivalent — SSRS evaluates expressions in a single pass
against the DataSet, so today these always emit `=""`.

**Status: upstream work planned** — propose adding multi-pass variable
evaluation support to Majorsilence.Reporting itself (e.g. a running-value
variable store scoped like Crystal's Local/Global/Shared, evaluated across
the render passes the engine already performs for running totals/page
numbering) so declared-variable expressions become emittable rather than
always blank. Until that support exists, the converter's fallback (`=""`)
stays in place.

---

## Documentation

### Public .rpt format specification
Everything reverse-engineered so far (OLE compound document layout, the TSLV
record stream, the tag catalog and per-tag payload shapes, MUTF-8 string
encoding, the AES-CFB128/zlib-compressed stream handling, the formula-hook
entry tables, the various object wrapper conventions) currently lives only as
scattered comments in the parser source and prose in this file. Write it up
as a standalone, public markdown specification document (not just internal
BACKLOG notes) — a reference for the format itself, independent of this
project's specific conversion goals, that others could use to build their own
tooling against .rpt files. Should cover, at minimum: OLE storage layout
(`Contents`, `Subdocument N`, `Embedding N` streams), the TSLV record header
format, the full tag catalog with confirmed/unconfirmed status per tag, string
encoding, and the encryption/compression scheme. Not started yet.

---

## Blocked / by design

### Connection strings
The `QESession` OLE stream is encrypted with a proprietary 16-byte key not
present in the decompiled runtime JAR. Cannot be decoded. Every converted
report requires the user to fill in `<ConnectString/>` manually. No fix
possible without the key.
