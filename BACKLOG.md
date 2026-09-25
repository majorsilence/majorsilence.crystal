# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information the file format does not expose.

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

### Fixture coverage swept across the corpus: 2 measurable reports became 7

The suite's ink-agreement score is the project's only fidelity signal, and it can
only speak for reports that have a data fixture — without one the report renders no
rows and scores zero however correct the layout is. Two reports had one. The whole
88-file public corpus was put through the fixture pipeline to find the rest.

**Both halves of the pipeline were wrong in ways that produce plausible output.**

`ReferenceRenderer --data`, the first fixture route, guesses which columns of
Crystal's section-interleaved CSV are the detail values by looking for a run of
constant columns (the labels) immediately followed by an equally long run of varying
ones. Run over the corpus it claims success on 41 of 88 reports, and the claims are
not trustworthy. For `SalesByCustomer-Grouped` it emits a fixture headed
`"Order Amount","Date"` whose rows are `"7 Bikes For 7 Brothers","$53.90"` — labels
over the wrong values, off by the group-name column that sits between them. Others
are headed with report-header prose (`Custom Functions Demo`) or the print date.
That route is now removed; `--csv` remains as an untouched diagnostic dump, and the
comment on it says why it must not be used to build a fixture.

`FixtureBuilder`, the route actually in use, names columns from our own parsed field
list and takes the most common type-shape among full-width export rows as the detail
rows. Two failure modes, both silent:

- **A label row can win the shape vote.** `Bottom5USA` produced a five-row fixture
  whose cells were the literal strings `Customer Name`, `Order Amount`, `Region` —
  the page-header labels, repeated on every exported line, the same width and the
  same all-text shape as a detail row. `InventoryStatus` and `Top5USAsubCanada` did
  the same with one row each. A cell holding a field's own name is now treated as a
  label, and a row where half the cells do is not a detail-row candidate. All three
  reports now fail cleanly instead of emitting fiction.
- **A row carrying a null is dropped without a word.** A BIFF row omits an empty
  cell, so a detail row with a null field exports one value short and never reaches
  the candidate list. `ProductPriceList` kept 76 of its 115 rows and said only
  "wrote 76 rows". Nothing here can tell such a row from a header or a group line, so
  the count is now reported rather than guessed at — a fixture quietly missing a
  third of its rows renders a shorter report than the reference it is measured
  against, and that gap reads as a layout fault.
  *(Since fixed: those rows are recoverable after all — see the entry below. The
  "nothing here can tell" was wrong; a detail row's column index says so.)*

Both guards were checked against the two committed fixtures: they regenerate
byte-identical, so nothing that was already right changed.

**What the sweep yielded.** Five reports added, taking fixture-backed cases from 2
to 7, and they divide sharply:

| Report | Ink agreement | Note |
|---|---|---|
| `boyum__SampleReport` | **37.1%** | best in the suite, and the first case from a different report author |
| `CustomerList` | 35.0% | existing |
| `SalesByCustomer-Grouped` | 32.4% | existing |
| `Country-Region-Sort` | **28.7%** | ordinary list report, 269 rows |
| `BeforeTV` | **3.2%** | renders a near-blank page |
| `Orders10k` | **1.6%** | renders a near-blank page |
| `Orders5-150` | **0.8%** | renders a near-blank page |

The two good ones render about as much ink as their references do — 5.5% against
5.3%, 0.7% against 0.6% — so what is left between them and 100% is placement, not
missing content.

*Reports still without a fixture, and why.* Of 88, only 13 got past `FixtureBuilder`
before the guards and 10 after. The rest either export nothing usable (a report that
shows only group summaries or a cross-tab has no detail rows in the export at all,
which is the saved-data problem above), or drop enough null-bearing rows to be
untrustworthy. Four of the ten were left out for that last reason:
`ProductPriceList` (76 of 115), `ProductPriceList-xs` (17 of 115),
`TenPct-DiscountDays`, `InventoryStatus`.
*(Since fixed for three of them: with null-bearing rows recovered by column index,
`ProductPriceList`, `ProductPriceList-xs` and `TenPct-DiscountDays` each come out at
exactly the 115 rows the report was saved with, and the first two are committed
fixtures. `FixtureBuilder` now checks every fixture against that saved count — see
"The saved row count" below.)*

### An unanswered report parameter filters every row away

Three of the five new cases render an almost blank page — 0.14% ink against
references carrying 2.3–8.0%. Their fixtures are complete and correct; the rows
arrive and are then filtered out.

All three have a record-selection formula testing a field against a report
parameter, which is how Crystal spells a range:

```
{Orders.Order Amount} = {?Order_Amt_Range}      (Orders10k, Orders5-150)
{Orders.Order Date}   = {?Date Range}           (BeforeTV)
```

That converts faithfully to a DataSet `Filter` of
`=(Fields!Order_Amount.Value = Parameters!Order_Amt_Range.Value)`. No parameter
value exists at render time, so the comparison is false for every row and the report
selects nothing. The real engine renders these because it uses the parameter value
saved in the .rpt.

**This is a question before it is a defect, and the answer is not obviously "drop the
filter".** Parameters are a render-time concern here — `RunGetData` reads them
directly and `RuntimeOverrides.Parameters` is applied after conversion — so the
converter cannot know whether a value will arrive. Dropping the filter at conversion
time would silently un-filter reports for callers who *do* supply one, which is worse
than a blank page: a blank page is obviously wrong, and a report showing every row
when it should show five is not. Making the emitted expression tolerate a missing
value instead (`Parameters!X.Value Is Nothing OrElse ...`) keeps both behaviours, but
turns an unanswered parameter into "show everything" for every report in both
corpora, and whether the engine's expression language supports that spelling is
unverified.

Worth noting the size: it is not three reports. Any report whose selection formula
references a parameter renders empty through this pipeline today.

The three cases are recorded at their measured baselines so that whatever is decided
can be measured against them.

### Saved report data: where it is, and why it is not readable yet

**Investigated, not solved — but the unknown is now one 16-byte value per batch,
and everything around it is readable today.** Recorded so the next attempt does not
repeat the dead ends.

#### Where the data lives

Saved rows live in an OLE stream named `SavedRecordsStream <n>l`, present in
**74 of the 88** public corpus files (85 stream instances, counting the ones
subreports carry at `Subdocument <n>/SavedRecordsStream <m>l`). Saving data with a
report is common in that corpus and rare elsewhere: a separate corpus of 2,324
production reports yielded only **21** such streams in total.

It is one of a family. Counted over the 88 files, by number of files carrying at
least one:

| Stream | Files | Stream | Files |
|---|---|---|---|
| `Contents`, `QESession`, `ReportInfo` | 88 | `SavedRecordsStream` | 74 |
| `ViewInformationStream` | 78 | `ConstantRecordsStream` | 33 |
| `TotallerStream` | 78 | `CHART` | 15 |
| `DataSourceManager` | 78 | `FormulaRecordsStream` | 14 |
| `AnalysisGridsStream` | 78 | `MemoValuesStream` | 10 |
| `ReportParametersStream` | 78 | `PromptManager` | 47 |

Long strings and blobs spill out of the records stream into the memo and value
streams, so a complete reader needs more than the one stream.

#### `DataSourceManager` is already readable, and it is the index

This is the useful new fact. `DataSourceManager` and `AnalysisGridsStream` open
with the **same `FC 00 FF FF` TSLV stream-header prologue as `Contents`** — ten
header bytes, twenty-four data bytes, the per-file AES initialisation vector at
bytes 16..31 XOR 0xFF, ciphertext from byte 34. Only the record schema differs
(0x0701 / 0x0702 against `Contents`' 0x0700). `ContentDecryptor.Decrypt` therefore
reads both streams unmodified, and the per-file IV in a report's
`DataSourceManager` is byte-identical to the one in its `Contents`.

Inflated, `DataSourceManager` carries:

- **The saved dataset's column list, in order.** A tag-7 record holds a tag-65
  record holds a tag-64 record, whose payload is a big-endian Int32 length followed
  by the qualified column name in MUTF-8 (`Customer.Customer ID`), then the
  column's ordinal. For `CustomerList` this yields the six database columns in
  exactly the order of the committed fixture, followed by Crystal's thirteen
  special fields (`Print Date`, `Report Title`, `Record Selection Formula`, …).
- **A batch index.** One top-level tag-45 record (schema 0x0701). Its payload's
  big-endian **Int32 at offset 6 is the row total**. After a header whose layout is
  not decoded — 96 bytes in 172 of 173 reports — come tag-109 records (schema
  0x0800) laid out as: Int32 row count, Int32 (unidentified), **Int32 byte offset
  into the records stream**, **Int32 byte length**, UInt16 count, Int32[count]. A
  descriptor's size therefore varies with its count; there is no fixed stride.
- **Tag-115 records naming the logical sub-streams**: `DBBatchIndexStream`,
  `DBBurstValueStream`, `FormulaBatchIndexStream`, `FormulaBurstValueStream`,
  `DynamicGraphicIndexStream`, `DynamicGraphicValueStream`.

**The batch descriptors come in runs, and the first run tiles the records stream.**
A run is the descriptors up to the next one whose byte offset starts again at 0; each
run covers one stream of the family end to end, and the first covers
`SavedRecordsStream`. That holds for every main-report index in the public corpus
(74 of 74) and the third-party one (78 of 78), and for 20 of 21 in a 2,324-file
corpus. Taking the descriptors flat instead is what made it look like 60 of 84: the
later runs belong to sibling streams (`boyum__Activity`: `[0+166, 166+32]` tiles its
198-byte records stream, and `[0+34]`, `[0+20]` are two further runs). Where a stream
has several batches the arithmetic is exact — `Bottom5USA` 8790+4409 = 13199,
`SalesByCustomer-Grouped` 10955+10650+2144 = 23749, each the stream's own length.

**Within the first run, the rows fall into whole sections.** A batch holds at most
1,000 rows, so `SalesByCustomer-Grouped`'s 2,191 are 1,000 + 1,000 + 191. But in 20
of the 172 reports the run's row counts sum to exactly *twice* the total: the stream
holds two consecutive sections, each covering every row once —
`boyum__Activity` is `[1 | 1]`, one third-party report `[540 + 260 | 800]`, another
`[1000 x 6 + 997 | 213 x 32 + 181]`, 6,997 twice. What a section holds that a
second one needs is not known; the arithmetic is.

#### The saved row count, which is read and checked today

`SavedRecordsIndex.ReadRowCount` returns the header's total **only when the
descriptors confirm it**: the first run must tile the records stream's bytes exactly
and divide into consecutive sections that each sum to the total. It surfaces as
`ParseResult.SavedRowCount`. Confirmed for **74 of 88** public files, **78 of 114**
third-party and **20 of 2,324** private — every report saved with data but one, whose
descriptors start at offset 384, neither tile nor sum, and read as unknown rather
than as a number. Every file in all three corpora still parses.

**It is independently corroborated.** All nine committed fixtures were built from
Crystal's export of their reports' saved data, and each one's row count equals the
saved count exactly: 26, 269, 269, 27, 607, 115, 115, 2,191, 10. That is the test
that pins it (`SavedRowCount_MatchesTheCommittedFixture`).

**`FixtureBuilder` now refuses a fixture that disagrees.** A fixture short of rows
renders a shorter report than the reference it is measured against, and the gap
reads as a layout fault rather than as missing data, so a mismatch is an error and
no file is written. Disabling the null-row recovery reproduces the old
`ProductPriceList` defect exactly, and the builder stops with *"Recovered 76 detail
rows, but the report was saved with 115"*. Where the count cannot be confirmed it
says so and writes the fixture unchecked. Subreports keep their own saved data and
index under their `Subdocument` storage, which this does not read.

#### The cipher is settled; only the IV is not

The records stream uses the **same Crystal AES-128-CFB128 and the same fixed key
this repo already implements** for `Contents`. That is now measured rather than
assumed. In CFB-128 only the first keystream block depends on the initialisation
vector; every later block's feedback is the ciphertext itself, which is in hand. So
a stream can be decrypted **from byte 16 onward with no IV at all**, and doing that
with the published key over 451,078 bytes across 45 streams gives a repeated-3-gram
rate of **0.0176**, against **0.0006** for each of eight random keys — a 28x
separation, per-stream ratios 4x to 40x. A wrong key cannot do that.

The consequence is sharp: **every byte of every batch except its first sixteen is
readable today**, and those sixteen are exactly where the batch's packed payload
begins.

*What the IV is not* — each claim measured over the 85 public-corpus streams unless
stated:

- Not the per-file `Contents` IV, not all-zero, not all-0xFF, not the key, not the
  byte-reversed key. None produces a zlib header or a TSLV control byte at
  plaintext offset 0 on any stream.
- Not stored anywhere in the same file. Sweeping **every** 16-byte window of every
  stream in `CustomerList` — raw and, where the prologue allows, decrypted and
  inflated: 143,494 candidates — gave 5 hits against a zlib-header oracle, all at
  the chance rate and none of which inflated. A second sweep with a
  deflate-*validity* oracle produced only runs of a few bytes, the noise floor.
- Not fixed across streams, and not per file either. Several unrelated reports
  share a byte-identical records stream (the nine `Top5USA` variants share one of
  11,340 bytes; `X` and `X_HANA` sibling pairs share theirs), while reports with
  different per-file IVs share ciphertext prefixes — so the value is attached to
  the batch, not to the file.
- **Re-initialised per batch, not once per stream.** In the multi-batch files,
  decrypting continuously across a batch boundary yields nothing inflatable at that
  boundary, and the ciphertext prefixes of two batches in one stream do not XOR to
  a common plaintext prefix. So each batch loses its own first sixteen bytes.

#### What the payload is not

- **Not plain records.** `CustomerList`'s decrypted 16,740 bytes contain none of
  the six values its own committed fixture starts with (`City Cyclists`,
  `Sterling Heights`, `Pathfinders`, `DeKalb`, `Kingsway`), in ASCII or otherwise;
  the longest printable run anywhere in the stream is 12 characters.
- **Not fixed-width rows.** Autocorrelation over lags 1..512, on every stream of
  512 bytes or more, peaks at 1.2x to 4.3x baseline with no lag recurring across
  files. There is no row stride to find.
- **Possibly not plain deflate either, and this is worth flagging.** The decrypted
  payload's repeated-3-gram rate of 0.0176 is about **six times** that of genuine
  zlib output over comparable report data (0.0027 mean, 0.0039 max over 10
  samples), and 28x random. That is a statistical indication, not proof — deflate
  of unusually repetitive input could land there too — but it is in mild tension
  with treating "independently deflated batches" as settled, and a future attempt
  should not assume the inner layer is a zlib stream before checking.

#### What would close it

The IV, and nothing else. It cannot be recovered by inference from file contents —
that space is now exhausted — so it has to be established empirically. Everything
downstream of it is understood well enough to implement the moment it is known.

**There is nothing partial worth shipping in the meantime**: a reader that decodes
every byte of a batch except the sixteen the payload starts at decodes no rows at
all. The two pieces that *are* worth landing on their own merit are the batch index
and the column list, because both read cleanly today. The row count has landed (see
above). The column list has not; it would give the cross-tab and chart-only reports
something to line an export up against.

*Route taken instead — data fixtures (done for one case, 1.5% → 8.9%).*
`ReferenceRenderer --data` exported a report's saved rows through the licensed
engine, and the visual suite pushes the result through `RuntimeOverrides.Data`,
so both sides render the same data without reading the encrypted stream at all.

> **Everything from here to the end of this section describes a route that was
> superseded and has since been removed.** Its conclusion — that naming the columns
> from our own parsed field list cannot rescue the other report shapes — is wrong,
> and the section below on the corpus-wide fixture sweep is the current account. The
> CSV analysis is kept because the shape table is still an accurate description of
> what Crystal's CSV export contains.

Two wrinkles worth knowing. Crystal's CSV export is **section-interleaved**: every
row carries the whole report line — report-header text, the page-header column
labels, that row's detail values, then the footers. The labels sit immediately
before the values and there is one label per value, which is what makes the detail
columns recoverable; the tool finds the widest such pairing and rejects blank
labels, since a run of empty constant columns otherwise matches and yields
nameless columns that bind to nothing. `ExcelDataOnly` was tried first and is
legacy BIFF8, needing a spreadsheet parser to read back.

**This route caps out at plain list reports, and the reason is structural.** The
CSV export flattens the *rendered sections*, not the underlying dataset, so column
identity only survives when the page-header labels happen to sit directly above
the detail values. Dumping the raw export for each shape (`--data-raw`) shows
where that breaks:

| Report | Raw shape | Why no fixture |
|---|---|---|
| `CustomerList` | title, 6 labels, 6 values, 2 footers | works — the committed fixture |
| `SalesByCustomer-Grouped` | title, group name, 2 labels, **group name again**, 2 values, subtotal, footers | the repeated group-name column sits *between* labels and values, so label→value alignment lands one column off — this is the misaligned fixture that was caught and discarded |
| `Top5USAsubCanada` | title, 3 blank constants, 3 varying | no labels at all, and two of the varying columns are formulas (`Total for X:`, a percentage) rather than data columns |
| `Canada-CrossTab` | 2 constant columns (a date, `1`) | the cross-tab's data is not in the export at all |

So naming the columns from our own parsed field list does not rescue the other
shapes *from this export*: the varying columns are not a 1:1 positional match for
the detail fields once group names, subtotals and formula columns are interleaved.
It does rescue them from the **data-only Excel** export, which was mis-dismissed
above as merely needing a parser — it writes a cell grid rather than flattened
sections, and that grid survives grouping. `--xls` plus `FixtureBuilder` is the
route in use today. Getting data for those shapes needs the **actual
dataset**, which means the saved-data stream above or a live database — not this
export. Only verified fixtures belong in `tests/reference-data/`; a wrong one is
worse than none, because it makes the suite assert against fiction.

*The original cheaper-route note, for reference:* the licensed engine exports
`CharacterSeparatedValues`, and with a bare `Data()` it exports **the saved data**
— the same source the reference PNGs render from. Pushing that through
`RuntimeOverrides.Data` (already a `DataTable` for the
flattened DataSet, keyed on raw Crystal column names, already applied by
`ReportEngine.ExportAsync`) makes the visual comparison apples-to-apples with **no
new format work**. Decrypting the stream remains the better prize — it would let
any consumer render a .rpt with its own data, no database and no SAP runtime —
but it is not on the critical path for making the suite meaningful.

### The regex fallback was emitting Crystal as if it were RDL

**31 fatal, 40 occurrences (was 50/64) — 19 files.** `TranspileIfThenElse` only
rewrites the single-branch shape, so a deeply nested `else if` chain — with `//`
comments *inside* the branches, which is how these reports are written — defeats
both it and the grammar, and the fallback returned the body only partly
translated. Two guards, both degrading rather than leaking:

- **Leftover keywords.** `Then` is not VB.NET expression syntax at all, so one
  surviving outside a string literal proves the body was never translated.
- **Juxtaposed expressions.** The same failure with the keywords consumed leaves
  two complete expressions either side of a line break
  (`(RowNumber() = 1)` then `Fields!DebitOpening.Value`). Detected as a
  value-ending character followed by a value-starting one with *no operator
  between*, so an expression legitimately wrapped after an operator or inside
  parens is untouched.

Crystal's rule is that the last statement of a sequence is the result, but by
this point the branch structure is already lost — taking the trailing fragment
would report a plausible **wrong** number where degrading reports nothing. Both
guards check string-literal-stripped text so report wording ("paid, and then
cleared") cannot trip them; that case is pinned by a test.

**`Split` was also a correctness bug, not just a missing function** (now 0).
Crystal's `Split` returns an array and `Split({x}, "-")[2]` selects a *field*,
but the subscript rule turned it into `Mid(...)` — the second *character* of the
whole string, wrong even had `Split` existed. Since the expression language has
no array indexing, the pair now collapses into one `SplitPart(text, delimiter,
index)` engine function, 1-based, yielding empty for an out-of-range index the
way Crystal treats a short row.

### Coercing text columns at the arithmetic site (the long-planned fix)

**14 fatal, 23 occurrences (was 31/40) — 17 files.** This is the item this file
has carried since the numeric-inference round: Crystal types plenty of
numeric-looking columns as text (they arrive with no RDL `TypeName`, which the
engine defaults to String), and arithmetic on them is rejected outright. Retyping
the column was tried and **measurably regressed both corpora**, because a column
used as a number in one formula is routinely a genuine string in another — so the
coercion belongs at the *reference site*, leaving the declared type alone.

Text-typed column references are now wrapped in `Val()` when the expression is
doing arithmetic: it must contain `-`, `*` or `/` and hold **no string literal and
no `&`**. Those two exclusions are the whole rule — Crystal's `+` is concatenation
whenever a string is in reach, so they are what separates "summing text columns"
from "joining them". Same heuristic the parser's numeric inference already uses
and which the corpora already validated. `Val` rather than `CDbl` because it is
total: blank or non-numeric yields 0 instead of throwing mid-render. Both
directions are pinned by tests — coerced under arithmetic, untouched when a
literal is present. Numeric-operator errors fell 23 → 6.

**Remaining at the time**: nothing above 6. The numeric residue was down to 6
(String-typed *parameters* and date subtraction), and the rest read as one- and
two-offs — residual field resolution, a subreport-compile cascade, `Picture`,
`GroupingLevel`, two unterminated-string oddities. **14 of 2,324 files (0.6%),
1 crash, 23 total Severity-8 occurrences.** Verified at every step: public corpus
0/88, 857 crystal tests + 288 engine tests green, fatal, exception *and*
occurrence counts all tracked. The section below closes all of it — the "two
unterminated-string oddities" in particular were not oddities but a lexer bug
affecting every string literal that ends in a backslash.

### The tail cleared: private corpus 14 → 0 fatal, 0 crashes, 0 occurrences

**Done.** The residue named above turned out to be eight distinct defects, not a
long tail of one-offs. All 2,324 files now convert and render with no
Severity-8 error and no exception. The scan carries a positive control so the
zero is not read as a plumbing failure: 43 MB of PDF is genuinely produced and
every file still logs its non-fatal "no data source" error, so error detection
is demonstrably alive.

Five were engine gaps, three converter ones.

**A backslash is not an escape (engine).** The expression lexer treated `\` as
a C-style escape inside string literals — a fyiReporting-era extension carried
since the 2011 fork. RDL expressions are VB.NET, which has no backslash escapes
at all, so this was wrong in general and not merely inconvenient: a literal
*ending* in a backslash had its closing quote swallowed and failed as an
unterminated string. Any Windows path or hierarchy separator hit it. Removed;
the only escape is now the doubled quote. Nothing in the engine's own 298 RDL
files or its tests relied on the old behaviour — checked before changing shared
code.

**`Like` was missing entirely (engine).** The converter already emitted the
correct RDL operator; the engine's grammar had no such token, so the expression
died at `')' expected but not found. Found 'Like'`. Added as a proper
relational operator — token, lexer keyword, `FunctionRelopLike`, parser case —
rather than as a function call, because `Like` is real VB.NET/RDL syntax and
emitting `Like(a, b)` would have made our output non-portable to any other RDL
consumer. The pattern language is VB's, translated to a regex so the `[a-z]`,
`[!abc]` and `#` forms work, not just Crystal's `*` and `?`.

**`Picture` and `Roundup` (engine + converter).** Crystal's `Picture(text,
template)` fills each `x` in the template from the text and copies everything
else through. `Roundup` is Ceiling in its one-argument form, but the
two-argument form rounds up at a *decimal place*, which is not what `Ceiling`'s
existing second argument means (a multiple) — so it got its own function rather
than a caller-side conversion that would have been easy to get subtly wrong.
`Len` also gained the `object` overload its siblings already had.

**`Nullable` was parsed and then ignored (engine).** A report rendered with no
parameter values — the normal case for one converted from a format that prompts
— relayed an empty value to a subreport, and `SetRuntimeValue` tried to convert
null to the declared numeric type. Worse, the *error path itself* dereferenced
the null value, so the whole render died with a bare `NullReferenceException`
naming nothing. Both fixed: the diagnostic is null-safe, and `Nullable` is now
honoured. The converter now declares parameters `Nullable` (a converted report
has no way to prompt) and emits the `DefaultValue` the model already carried
but nobody wrote out.

**Numeric use is transitive (converter).** A formula can be subtracted from
while the values it is built out of are only ever added together, and `+` alone
proves nothing because Crystal concatenates with it too. The seed set now
carries down one reference at a time through bodies containing no string
literal — where every `+` must be an addition. Without it a degraded operand
two hops down stayed `""` and poisoned the expression above it.

**Numeric inference was case-sensitive (converter).** Crystal resolves `{?Name}`
without regard to case, and a report in this corpus declares `UpToYear` while
subtracting from `{?UptoYear}`. The boolean inference beside it already matched
case-insensitively; the numeric one did not, so that parameter stayed String and
the subtraction was rejected. One-word fix, found only by reading the emitted
RDL rather than the error.

**Negation needs the same treatment as arithmetic (converter).** `Not {flag}` on
an untyped column is rejected before any data is read. Same rule as the `Val()`
coercion above and for the same reason — coerce at the use site with `CBool`,
never retype the column, which would change how every other reference reads it.

**`Group #N Name` is a built-in, not a column (converter).** The placed-object
paths already resolved it; section formulas are transpiled straight from Crystal
text and so emitted `Fields!Group__2_Name.Value`, which no DataSet declares —
fatal when the expression is a table's visibility. Resolved as a post-pass over
every section formula.

**Testing.** Nine new tests: an engine report exercising the backslash literal,
both `Like` forms, `Picture` and `RoundUp` through a real render, and a
parent/subreport pair for the nullable relay; four converter tests and three
transpiler tests. Each was checked to *fail without its fix* — the lexer change
and the nullable change by temporarily reverting them, the propagation rule by
disabling it — after an earlier round in this campaign produced a test that
passed with its fix removed. The one fix with no unit test is the
case-insensitivity change: it runs inside `RptParser`'s post-parse pass over a
real `.rpt` and there is no seam to drive it from a synthetic report, so its
evidence is the corpus rather than a test that would only restate the regex.

Verified: **0 of 2,324 private, 0 of 88 public**, 861 crystal + 290 engine tests
green, visual regression 5/6 (the same pre-existing `Top5USAsubCanada` page-2
failure, unchanged).


### After the fatals: triaging what they were hiding (33,506 → 14,334 non-fatal)

**In progress.** With Severity-8 at zero in both corpora the scan was lowered to
report *non-fatal* errors, which every one of the 2,324 files logs. Two real
defects came straight out of the top of the list; the rest of the categories are
recorded below rather than fixed.

**Crystal format hooks were being emitted as DataSet fields (14,000+ → 0, 969
files).** The largest category by far was `Field X has duplicates`, and the names
gave it away: `Tool_Tip_Text`, `Font_Style`, `Date_Order`, `Currency_Symbol`,
`Running Total Condition Formula`. These are Format Editor properties, not
formulas — they arrive through the same record tag as user formulas, and a report
carries one copy per formatted object, so a report with four tooltips emitted
four `<Field Name="Tool_Tip_Text">`. That is not cosmetic: RDL has no duplicate
field name, and the engine keeps the first and drops the rest, so any real
collision was silent data loss.

The evidence that these are never user formulas is strong rather than assumed.
Crystal requires formula names to be unique within a report, so a name appearing
twice cannot be one — and across the private corpus **every** duplicated name was
a format property, with no user formula among them. Going further, a probe over
all 2,324 reports found that across the 4,227 occurrences of those names, not one
was ever referenced by another formula or placed as an object, so none could have
contributed to a rendered report. The filter is a name list alongside the
existing `_Visibility` / `Group #` one, because nothing in the record separates a
hook from a user formula structurally — a question left open below.

The list is explicitly **not** claimed complete: running the same probe over the
*public* corpus immediately turned up two more (`Display_String`,
`Section_Back_Color`), which is the whole argument for the safety net that went
in with it — the converter now refuses to write the same `<Field Name>` twice, so
an unlisted hook leaks one unused field rather than causing a duplicate. That
guard also covers a case the filter cannot: real columns from different tables
differing only in case (`prov` / `Prov` / `PROV`). Only one survives, which does
lose a column — but the engine matches field names case-insensitively, so it
could not have told them apart either, and renaming would break every reference.

That the removed fields were inert is confirmed rather than argued: the corpus
renders **byte-for-byte the same 43,182,861 bytes of PDF** before and after.

**The report body width was never emitted (3,058 → 0).** Without `<Width>` the
engine warns and assumes the full page, letting body content run underneath the
margins. Crystal has no equivalent field — its sections are the page less its
margins by construction — so that is what is now written.

**Still open, in descending size** (all non-fatal, none investigated yet):

- `Size 'X' is larger than the RDL specification maximum of N inches` (2,039) —
  geometry overflow, cause unknown.
- `Exception evaluating =IIf((Parameters!X.Value = True), True, Nothing).  Object
  must implement IConvertible` (44) — `Nothing` as an `IIf` branch in a boolean
  expression.
- `Unknown Details element PageBreakAtEnd ignored` (9) — a genuine silent drop:
  `Details` accepts only TableRows/Grouping/Sorting/Visibility, so a Crystal
  "new page before/after" on the Details section goes nowhere. Expressing it
  needs a Details `<Grouping>` keyed per row, which is a layout change on a rare
  path and was left rather than guessed at.
- The datasource errors (5,387 × 2) are the scan's own doing — it renders without
  a database — not a conversion defect.

**Open question: is there a structural marker for a format hook?** The filter is
a name list because none was found at the record level, but the search was not
exhaustive — the tag-113 payload was not probed for a kind byte, and record
ordering relative to object definitions was not examined. A structural rule would
make the list unnecessary. A second candidate rule — drop any formula field that
nothing references and nothing places, since it cannot affect output — is
attractive but was rejected for now: the reference scan would have to cover
formula bodies, section hooks, record and group selection, placed objects, group
definitions, sort fields and running totals, and under-counting any one of them
silently deletes a field that matters.

Verified: **0 of 2,324 private, 0 of 88 public** fatal, non-fatal 33,506 → 14,334
(−57%), 867 crystal tests green, visual regression 5/6 (the same pre-existing
`Top5USAsubCanada` page-2 failure). The scan carries a positive control — PDF
bytes rendered and non-fatal errors still logged — so a falling count cannot be
mistaken for a scan that stopped working.


### A page that is not a whole number of points is drawn higher by the fraction (#22)

**The symptom.** `boyum__SampleReport` sat 3-4px low at 300dpi, a constant offset with
an exact row pitch. With both engines' PDF text positions, every piece of text on the page
was **+0.70 to +0.75pt** low, horizontal positions agreed to 0.02pt, and the page number
at the foot of the page was out by as much as the title at its head. A margin, a bound or a
row pitch cannot move the top and the bottom of a page by the same amount. The font is
Arial 10, as in the reports that line up.

**The cause is Crystal's, and it is the page box.** It is the suite's only A4 page:
16,836 twips, **841.8pt**. Both engines write the PDF box truncated to whole points
(`[0 0 595 841]`, read from Crystal's PDF directly). They differ in where they anchor.
Crystal lays out at the true height and anchors from the bottom, so its whole page lands
`frac(height)` higher than its twips say. This engine lays out on the true height too
(`Pages.PageHeight`), truncates the box (`RenderBase`), and places from the top, so its
text lands where the twips say.

**Established from Crystal's own output, not from ours.** For each report with one page
footer, the distance from the footer object's nominal top (page height − bottom margin −
section height + object top) to the top of the text Crystal draws there:

| page height | fraction | reports | offset | offset + fraction |
|---|---|---|---|---|
| 792 / 612 (whole) | 0 | 26 | +2.51 median | +2.51 |
| 841.80 (SampleReport, Arial 10) | 0.80 | 1 | **+1.71** | **+2.51** |
| 595.20 (Picklist: Arial 8, 240-twip box) | 0.20 | 2 | −0.61 | |
| 841.70 (Payments, ProductionOrder: same design) | 0.70 | 4 | −1.11 | |
| 595.20 (JournalEntry, Arial 10) | 0.20 | 2 | +2.23 | +2.43 |

SampleReport's +1.71 was predicted (+2.51 − 0.80) before it was measured, and it lands
to the hundredth. The two Boyum designs match each other, not the benbrahim baseline, and
sit 0.50 apart for fractions 0.50 apart. Two other footers (ServiceContract +5.14,
iPaymentCreditCardStatement −3.92) have designs unlike any other and compare with nothing.

**The emulation** is in the converter and exact in twips. `PageHeight` becomes the whole
points (`HeightTwips − HeightTwips mod 20`), and `TopMargin` is lifted by the same
fraction. Every top-anchored position rises by it, the page footer rises with the shorter
page, and the body keeps its height, so pagination does not change.

**What it changed.** SampleReport **75.0 → 96.0**, its text from +0.73pt to −0.06pt of
Crystal's. Every Letter case is unmoved. It applies to 44 public reports and 154 of the
2,324 private ones, the pages whose height is not a whole number of points.


### An amount formatted with its own currency format ends in a space

**The measurement.** The visual suite now keeps our PDF beside the PNGs it writes, so the
two engines can be compared by where their text sits rather than by pixels. Across every
fixture-backed case, left-aligned columns agree with Crystal's to 0.1pt. Right-aligned
amounts did not: every one sat **+2.57 to +2.70pt** right of Crystal's, identically on
every row: `$14.50`, `$41.90`, `$7,339.35`, and Product ID's bare `1101`.

**The cause is a character, not a position.** Crystal's PDF has a real space glyph after
the value (`$14.50 `, 2.49pt wide) ending on the object's right edge. A right-aligned amount
therefore stops one space short of the edge, where the closing bracket of a `($14.50)`
negative would go. The engine emits no such space, so ours ran to the edge.

**When it is there.** Three conditions, each separated by a real object rather than
inferred:

| object | own formats (tag 241) | symbol enabled (`data[2]`) | symbol stored | Crystal |
|---|---|---|---|---|
| Order Amount ×3, Price, `@TenPct`, `@Sum Orders`, a group total | yes | yes | `$` | **space** |
| Product ID (symbol not printed: `data[4]` off) | yes | yes | `$` | **space** |
| `@Calendar Days Between` (CustomFunctions) | yes | yes | `$` | **space** |
| SalesByCustomer's detail amount, both pages checked | **no** | yes | `$` | none |
| CustomFunctions' `@Order Amount, $US` and `$Cdn` | **no** | yes | `$` | none |
| `@AccountSize` (SalesOpportunity) | yes | **no** | `kr. ` | none |
| CustomFunctions' `ORDER_ID`, `@Business Days Available` | yes | yes | **none** | none |

The machine-formats condition has a reason: Windows' en-CA currency format writes a
negative as `-$53.90`, with nothing to reserve. Two traps worth recording, because both
nearly produced a wrong rule:
- Byte 13 of the *first* numeric record matched the first five cases perfectly, and
  `SalesByCustomer`'s detail and group-total objects falsified it in the opposite
  direction. The two differ in exactly three records: font, position and tag 241.
- The benbrahim reports' Order ID and Customer ID store no symbol and have no space, which
  looked like evidence for the "symbol stored" condition. A teeth-check (dropping the
  condition changed nothing) showed they are all on the machine's formats, which already
  rules the space out. `ORDER_ID` in CustomFunctions is the case that separates it.

All eight objects with the space have their symbol before the number, so a suffix symbol
(` kr.`) is left without one.

**How it is written.** The format ends in a no-break space. The engine trims ordinary
trailing spaces from each line before aligning it (`RenderBase`); that is right for a
word-wrap break, so it is left alone. U+00A0 is not trimmed and has a space's width in
the same font. A plain space was tried first and never reached the page. Side effect:
text exports carry the no-break space on those values, where Crystal's own export would
carry a space.

**What it changed.** 49 objects in 24 public files, 2 third-party, and **7,332 objects in
1,086 of the 2,324 private reports**. Visual suite: TenPct-DiscountDays 76.8 → **88.4**,
ProductPriceList 83.9 → **92.8**, BeforeTV 86.5 → **95.2**, ProductPriceList-xs 78.5 →
**86.5**, Orders10k 87.3 → **92.6**, Orders5-150 87.2 → **91.3**. SalesByCustomer-Grouped
dipped to 64.9 under the first version of the rule, which ignored machine formats, and is
back at 65.1.

**Still unexplained.** `@Calendar Days Between` stores `$` with `data[2]` and `data[4]`
both on, and Crystal prints a bare `3 `, with no symbol. By the numeric record's decoding
it should print `$3`. We do not format it (a date minus a date is not something
`FormulaResultType` calls numeric), so nothing is wrong on the page, but it is a standing
counter-example to "`data[4]` on shows the symbol".

`FixtureBuilder --grid` now prints an export's cells row by row, the thing to read when a
report will not build.


### A formula's number format was always dropped, and one byte says whether to show its symbol

**The gate.** Every field object carries a numeric record, including the ones showing
strings, where it holds whatever the object was last defaulted to. So the parser applied a
numeric format only when the field was known to be a number, and it only knew that for a
database column. A formula's result type is not in the file. The formula's record holds its
name, text and dependencies. The field object showing it has, after its field name, 12 bytes
that reference the field (`[0]` looks like the kind, `[2]` and `[10]` an index) rather than
give its type. The object's other records are its position, its time format and the
machine-formats flag. So a formula object's format was always dropped, and
`TenPct-DiscountDays` printed `13.05`, `8.982` and `10.8` where Crystal prints `$13.05`,
`$8.98` and `$10.80`.

**The type, where it is not in doubt.** `FormulaResultType` reads the formula's text and
answers "numeric" only for:
- a number literal;
- a numeric column, or a formula that is itself numeric by these rules (recursively; a
  formula that refers to itself proves nothing);
- a call to a function that returns a number whatever it is given (`CCur`, `CDbl`,
  `ToNumber`, `Round`, `Sum`, `Count` and the like);
- arithmetic joining operands that are all numeric.

Anything else is "not known", and the object is left exactly as it was. That includes a
string literal, `&`, a comparison, `If`, `IIf`, `Maximum` (whose result follows its
argument), a comment or a subscript. `TenPct`'s formula is
`CCur(CDbl({Product.Price (SRP)}) * 0.90)`. A `switch` returning captions, such as
`@Title_AccountSize`, is correctly refused.

What that changes: 112 formula objects in 38 public files, 5 in 3 third-party files, and
3,417 in 892 files of the 2,324-file corpus (13% of its formula objects), formatted
`"$"#,##0.00`, `#,##0.00`, `0.00` or `0`. Checked against the real engine:

| report | formula | Crystal prints | we now format |
|---|---|---|---|
| TenPct-DiscountDays | `CCur(CDbl(price) * 0.90)` | `$13.05`, `$8.98` | `"$"#,##0.00` |
| Formulas | `price * 0.75` | `$10.88` | `"$"#,##0.00` |
| BeforeTV | `Sum ({Orders.Order Amount})` | `$52,263.63` | `"$"#,##0.00` |
| boyum__SalesOpportunity | `@ClosingPercentage` | `6`, `20`, `50` | `0` |
| boyum__SalesOpportunity | `@PotentialAmount` | `40.000,00` | `0.00` (see below) |

Visual suite: `TenPct-DiscountDays` 74.3 → **76.8**, `BeforeTV` 85.8 → **86.5**, nothing
else moved.

**`data[2]` of the numeric record says whether the currency symbol is shown.** Applying
formats to formulas exposed the numeric record's decoding to reports it had never been
used on, and one object disagreed. `boyum__SalesOpportunity`'s `@AccountSize` stores
`kr. `, has `data[4]` (apply) on, and Crystal prints it `341.326,67`, with no symbol. Its
effective record has `data[2] = 0`, where every symbol Crystal shows has `data[2] = 1`
(Price (SRP) `$14.50`, `@TenPct` `$13.05`). `data[2] = 0` now hides the symbol. That is
settled on the only objects it changes: the only records anywhere that store a symbol with
`data[2] = 0` and `data[4] = 1` are that formula and its `_HANA` twin. There are none in the
third-party corpus and none among 162,082 private records. Values 1 and 3 both show the
symbol, and what separates them is not known.

**Still not right, and why not attempted here:**
- **Grouping with European separators.** Crystal prints `@PotentialAmount` as
  `40.000,00`. `BuildNumericFormat` groups only when the thousands separator is `,`, so we
  print `40000.00`: decimals right, grouping missing. That gap is not specific to formulas.
  It applies to every Danish-style numeric database column (3,190 public records), no
  fixture-backed case would measure a fix, and the report's `Language` would also have
  to come out as the matching culture. It is its own item.
- **One currency symbol per page.** `synthetic__currency_symbol_per_page` exists to test
  it: Crystal prints `$0.00` on the first row and bare `0.25`, `0.50` after it. Its record
  has `data[12] = 1`, and 62 private records do too. It also has `data[10] = 2`, which is
  otherwise common on `%` fields and on `$` fields that show the symbol on every row, and
  one rendered sample cannot say which byte means what. RDL has no per-page symbol either;
  the nearest thing would be to drop the symbol, which is also unmeasured. We print `$` on
  every row, as the private corpus's 62 database-column records already did.

1,025 tests green. All 88 public, 114 third-party and 2,324 private reports convert and
compile with 0 engine errors. The scan's positive control (every field reference broken on
purpose) flags 87 of the 88 public reports, so the zero is not vacuous.


### A reference render depends on the printer, so the printer is now pinned

**Found by adding a case.** `TenPct-DiscountDays` became measurable once its fixture's
row count could be checked (115 of 115 saved rows). Its first reference scored
**25.5%**, against 83.9% for `ProductPriceList`, which is the same 115 rows without the
formula column. Crystal's PDF showed why: its whole page sat **6pt further in on every
edge** than ours.

That turned out to be true of *today's* render of every report, not of this one. A fresh
`ProductPriceList` render began 25px lower and 25px further right at 300dpi than its
committed reference, while our own output still matched the reference, which is why
its 83.9 had not moved.

The cause is the printer. Crystal formats a report against a printer, and a report that
does not store its own margins (tag 398's four margin slots hold sentinels) takes that
printer's. With none named, that is the session's default printer. A Remote Desktop
session substitutes the client's redirected printer for it, and here that was an
inkjet with larger unprintable margins. Rendered against each installed printer in turn:

| printer | against the committed reference |
|---|---|
| the console default (a laser printer) | identical apart from the print date |
| the Remote Desktop redirected inkjet | whole page shifted 6pt |
| Microsoft Print to PDF | whole page shifted 6pt |

`ReferenceRenderer` now formats every report against the printer the references were
made with, loading it exactly as CrystalCmd's `Exporter` does and setting
`PrintOptions.PrinterName` before export. It prints the printer on every run, takes
`--printer` to override, and fails outright if Crystal does not accept the name, rather
than falling back to the session default. Re-rendering **all 14 committed reference
pages** against it reproduces every one: first ink on the identical pixel in all 14,
and each differs only in a band holding its print date. So no committed reference was
affected; only the first `TenPct-DiscountDays` render was, and that render has been replaced.

Rendered correctly, **`TenPct-DiscountDays` scores 74.3%** and joins the suite as its
only case with a formula evaluated per detail row. What separates it from
`ProductPriceList`'s 83.9 is visible on the page:

- **The formula column has no number format.** Crystal prints `$13.05`, `$8.98`,
  `$10.80`; we print `13.05`, `8.982`, `10.8`. The field's own numeric format is not
  reaching a formula's value.
- **One product name wraps where Crystal's does not.** "Xtreme Anatomic Ladies Saddle"
  measures slightly wider in our engine than in Crystal's. It wraps, and since #21 the
  wrapped line is clipped rather than painted over the next row. The same width gap is
  described under "The remaining disagreement is glyph width".


### A date's month, day and year each carry their own format, and the record that says to ignore all three is not the date record

The previous date entry left thirteen undecoded byte patterns and named the way to settle
them: more reports that both display a date and render. That is what this did. **Both halves
of the answer landed, and they are separate findings**, which is worth saying plainly because
one of them is a decode and the other is a reason the decode must sometimes not be used:

1. the date record's component bytes are a full per-component format — order, year, month,
   day, and two separators — and all of it now reads;
2. **a different record entirely says whether any of that applies.** Most field objects are
   on the machine's own formats, and an object in that state keeps whatever was last in its
   date record, so those bytes are stale and reading them is worse than ignoring them.

The second is why the old byte-pattern-only reading could not have been completed as it
stood: two objects can hold identical date records and render differently, and nothing inside
that record distinguishes them.

#### What the record is

`tag-243` wraps a `tag-242` payload of eight component bytes followed by five MUTF-8 strings:

```
  [0] order    0 = year-month-day, 1 = day-month-year, 2 = month-day-year
  [1] year     0 = yy,  1 = yyyy, 2 = no year
  [2] month    0 = M,   1 = MM,   2 = MMM (Apr), 3 = MMMM (April), 4 = no month
  [3] day      0 = d,   1 = dd,   2 = no day
  [4..7]       day-of-week settings, not read
  strings      prefix, first separator, second separator, suffix, (a fifth, not read)
```

The separators are **positional and not interchangeable**: the first goes between the first
and second component of the order, the second between the second and third. They are strings
rather than characters — `', '`, four spaces, and the empty string all occur — and they
differ from one another in 1,428 of the 79,249 records across the three corpora, which is
what makes `MMM' 'dd', 'yyyy` out of a space and a comma-space.

`tag-241` wraps a `tag-240` payload of **two Int16 flags**, `[0..1]` suppress-if-duplicated
and `[2..3]` take-the-machine's-formats. Reading all four bytes as one Int32 is the obvious
mistake and this made it: 816 objects in the 2,324-file corpus set the first flag, 25 of them
while leaving the second clear, and as an Int32 those read 65,536 and look set. Only the
large corpus has any, which is exactly why it had to be checked there.

#### What each claim rests on

Every component value was walked through *one at a time* on a report that renders a date,
the other two held fixed, and the rendering read back from the export each time — so each row
below is a single-variable measurement, not an inference from a pattern:

| bytes (order year month day) | renders |
|---|---|
| `0 1 0 0` | `2002/4/3` |
| `0 1 1 0` | `2002/04/3` |
| `0 1 2 0` | `2002/Apr/3` |
| `0 1 3 0` | `2002/April/3` |
| `0 1 4 0` | `2002/3` |
| `0 1 1 1` | `2002/04/03` |
| `0 1 1 2` | `2002/04` |
| `0 0 1 0` | `02/04/3` |
| `0 2 1 0` | `04/3` |
| machine formats set | `2002-04-03` |

The order byte cannot be varied that way and is pinned by reports: **0** by one rendering
`2002/04/3`, **1** by two rendering `17-Sep-2026`, **2** by four rendering `04/24/2001`. Three
values, three independent groups of reports; nothing here is filled in by elimination.

Separator placement is pinned by one report that puts two month-day-year fields side by side,
one storing three spaces and two and the other a single space for each, rendering
`03   10  2016` and `03 10 2016`. Two more render `MMMM` with a dropped day as `April 2009`
and `January 2013`.

**The machine-default trap, again.** This box runs en-CA, whose short date is `yyyy-MM-dd`,
so a field rendering `2019-04-03` may mean nothing was decoded at all. That is precisely what
the machine-formats flag turns out to be: `2002-04-03` above is this box, not the report, and
the dashes appear while the record stores a `/`. Every format in the table is a *different*
shape from the machine's, which is what makes it evidence.

Read against what the reports themselves say their objects are set to, the flag agrees on
**2,136 of 2,136** objects in the 88-file public corpus, **702 of 702** in the third-party
corpus and **71,327 of 71,348** in the 2,324-file corpus; all 21 disagreements are fields the
report does not call a date. The component bytes agree on **1,316 of 1,316** date fields not
on the machine's formats in the large corpus, plus 14 in the public and third-party corpora —
no exceptions at any byte.

#### The patterns, and which are now read

The 2,324-file corpus holds **1,641 date-field records in 39 distinct patterns** once the
flag and both separators are counted, or 17 distinct component-byte quadruples. The largest:

```
  721 recs / 432 files  2 1 1 1  explicit  '/' '/'     MM/dd/yyyy
  142 / 108             2 1 0 0  explicit  '/' '/'     M/d/yyyy
  141 /  66             2 1 2 0  explicit  ' ' '/'     MMM d/yyyy
  128 /  94             2 1 2 1  explicit  ' ' ', '    MMM dd, yyyy
  106 /  81             2 1 0 0  machine   '/' '/'     (deferred)
   55 /  52             2 1 2 1  explicit  ' ' ' '     MMM dd yyyy
   43 /  31             2 1 1 1  explicit  ' ' ' '     MM dd yyyy
   35 /  35             2 1 3 2  explicit  ' ' ''      (not written — see below)
```

All of them are now read except two classes, both left deliberately:

* **A dropped component with two unequal separators.** Two components share one separator and
  which of the stored pair survives is not established. Two reports say the *first*, but two
  samples is not a law — this project has reverted a rule calibrated on two before — so these
  are left to the machine. 37 of the 1,641 records, none in the public corpus.
* **A prefix or a suffix.** Literal text around the date, in 4 of the 79,249 records across
  the three corpora and none of them on a date field. Nothing has measured how it is placed,
  so such a record is left alone rather than half-honoured.

Counted over the date-field records themselves rather than over the conversion output, the
decode now writes a format for **1,433 of the 1,641** in the 2,324-file corpus and stands
aside for 208: 171 because the object is on the machine's formats and 37 for the separator
ambiguity above. No record anywhere holds a component byte this does not know. The public
corpus is 7 written and 6 deferred of 13; the third-party corpus 7 and 30 of 37.

Also still untouched: the same machine-formats flag governs the **numeric** record, and this
change does not act on it there. Nothing here measured what a number on the machine's formats
renders as, and guessing it would be the same mistake in a different record.

#### Measured

Conversion is unchanged in health and changed in output exactly where expected. Both corpora
were converted and verified twice, once on this branch's parent and once with the change:
**88/88 and 2,324/2,324 convert with 0 failures**, and the generated RDL loads with
**0 engine errors in 110 and 3,223 files**, the same four numbers before and after.

The public corpus changes **one format in one file**, `yyyy'/'MM'/'dd` → `yyyy'/'MM'/'d` —
the one public report whose day is stored without a leading zero, and the one whose export
renders `2002/04/3`. The 2,324-file corpus changes **315 files**, 17,976 format elements
becoming 17,935: eighteen formats appear that could not be expressed before, 300 occurrences
in all (`MMM' 'dd', 'yyyy` in 106 files, `MMM' 'dd' 'yyyy` in 52, `MMM' 'd', 'yyyy` in 26,
`MMM' 'dd'/'yyyy` in 19, `dd'-'MMM'-'yyyy` in 10, `MMMM' 'dd', 'yyyy` in 13, down to one
occurrence each of `d'-'MMM'-'yyyy` and `dd'-'MMMM'-'yyyy`), while `MM' 'dd' 'yyyy` goes from
302 occurrences in 265 files to none and `yyyy'-'MM'-'dd` from 33 to 5 — the latter being
fields that print the machine's short date and now say so by carrying no format at all.

**975 tests green**, of which 19 cases cover the date record and 4 the common-format record,
and visual regression **12 passed, 1 skipped, no baseline moved**. Each group was checked by
reverting the code it covers and confirming it goes red:

* old byte reading restored → **13 of the 19** date cases fail: `yyyy'/'MM'/'d` comes back as
  `yyyy'/'MM'/'dd`, every month-name and dropped-component case collapses to a flat
  `MM'/'dd'/'yyyy` or `yyyy'/'MM'/'dd`, the day-month-year case returns null, and the
  three-spaces-and-two separators come back as one space each;
* machine-formats gate removed, decode kept → the **3** corpus cases fail, two of them
  writing `M'/'d'/'yyyy` where the machine's own short date belongs;
* flag read as one Int32 again → **1 of the 4** common-format cases fails, the one where
  suppress-if-duplicated is set and the machine-formats flag is not.

The largest movement this suite has ever recorded, from an arithmetic error in two halves.

**What gave it away was a report that looked perfect and scored 62%.** `Country-Region-Sort`
matches the reference row for row and column for column — every value, every position,
indistinguishable by eye. Measured, its rows start aligned with Crystal's and finish **13px
above** them by the bottom of the page: about a quarter of a pixel per row, compounding.

**Half one: the converter was rounding away a fraction of every dimension.** `TwipsToRdl`
emitted `{inches:F3}`, and a thousandth of an inch is 0.3px at 300dpi. On a one-off that is
invisible; on a table row it is a *pitch*. And adding decimal places cannot fix it: a twip is
1/1440 inch, whose decimal expansion repeats, so no finite number of digits is exact.

Points can be. A twip is exactly **0.05pt**, so `twips/20` always terminates within two
decimal places and the conversion is lossless for every integer input. The proof is in the
test suite: 34 assertions changed from inches to points, and every new value is an exact
integer twip count where the inch form never was — a 68-twip object was `0.047in`, which is
67.68 twips. One test could drop its tolerance entirely, because column widths now round-trip
exactly.

**Half two, and this is the part the first half exposed.** Emitting points alone moved six
reports sharply up and **two down**, and the two down were not quantisation — they dropped at
8px, 16px and 24px cells alike.

`Majorsilence.Reporting`'s `RSize` stores every size as an integer count of parts of 1/2540
inch, via `_Size = (int)(d * PARTS_PER_INCH)` — a **truncating** cast. So every dimension in
every report came out up to one part short (0.0004in, about a tenth of a pixel), always in
the same direction. `BeforeTV`'s 289-twip row is 509.76 parts, truncated to 509; Crystal's own
measured pitch is 509.69 parts, which *rounds* to 510. The old three-decimal inches produced
`0.201in` → 510.54 → truncated to 510, landing on the right answer **by accident**. Exact input
removed the accident and left the truncation visible.

The fix is `decimal.Round` in `RSize` — five call sites in one switch, in the Reporting repo.
Truncation was also rewarding imprecise callers, which is why nobody had noticed.

**Measured, with both halves:**

| report | baseline | exact units only | + rounded RSize |
|---|---|---|---|
| Country-Region-Sort | 62.3 | 79.3 | **94.9** |
| CustomerList | 90.6 | 95.8 | **98.6** |
| Orders10k | 74.6 | 81.2 | **87.3** |
| Orders5-150 | 60.0 | 74.9 | **87.2** |
| ProductPriceList | 63.6 | 74.3 | **83.9** |
| ProductPriceList-xs | 59.9 | 69.8 | **78.5** |
| SalesByCustomer-Grouped | 64.8 | 64.7 | 65.1 |
| BeforeTV | 86.4 | 81.5 | 85.8 |
| boyum__SampleReport | 81.1 | 78.7 | **75.0** |

88/88 public, 114/114 external and 2,324/2,324 private reports convert. The engine's own
suites stay green (293 net8, 293 net10, 261 net48).

*The one that goes down and stays down.* `boyum__SampleReport` renders its whole content block
**3-4px low at a constant offset**, with the row pitch exact — so the truncation bias had been
cancelling a different, pre-existing defect. The first ink on the page is already 3px low, so
whatever is wrong sits above all the content rather than accumulating through it. That is now
the worst-aligned fixture-backed report in the suite, it has its own issue, and its baseline is
recorded at the measured 75.0 rather than papered over.

*Worth keeping in mind.* Two reports were scoring well for the wrong reason, and a correctness
fix is what revealed it. A number that improves is not automatically a fix, and a number that
worsens is not automatically a regression — both need the pixels checked before they are
believed. The 8px/16px/24px comparison is what separated the two cases here.

### A detail field sits at its own Top inside its row, and that was worth 11 points

**BeforeTV 75.3 → 86.4%**, the second largest single move this suite has recorded — from the
change the previous entry dismissed as *"3px, and the detail path is the widest in this
converter"*. The 3px was the right number for the wrong quantity.

**It is not a per-object error, it is a per-row one.** A detail band is one row as tall as the
whole section, and its objects sit at their own `Top` inside it. Written as plain cells they
were all flushed to the top of the row — so a band whose fields sit at *different* Tops had its
whole row's ink collapsed into the height of the topmost one.

`BeforeTV` is the case. Its three detail fields are 221 twips tall in a 289-twip band, at
`T=0`, `T=68` and `T=0`:

| | row band heights |
|---|---|
| real Crystal | 45, 46, 46, 47, 46, 45 px |
| before | 33, 37, … |
| after | 45, 46, 46, 46, 46, 45 px |

Every band now lands **within 1px** of the reference, at an identical 60px pitch. The row pitch
was already right; what was wrong was the height of the ink inside each row.

The fix is the vertical half of the padding the detail row already had on the right:
`PaddingTop` from the object's own `Top`, `PaddingBottom` from what is left of the row beneath
it. It carries the same guard as the horizontal half, and for the same reason — a section
reporting no height gets a 240-twip fallback, which is a guess rather than a measurement, and
padding against a guess would move text that is currently in the right place. A test pins that.

**Measured.** 73 of the 110 public RDL files change — the detail row is the widest path in this
converter, which is exactly why it was left alone until there was a reason. 88/88 convert.
`SalesByCustomer-Grouped` 64.4 → 64.8 for the same reason, smaller because its detail objects
sit only 15 twips down. Nothing else in the suite moves and nothing drops.

*Worth recording as a lesson about this suite's own estimates.* The 3px figure came from
measuring one report's offset (`SalesByCustomer-Grouped`, `T=15`) and assuming it generalised.
It did not: the quantity that matters is not how far down an object sits, it is how far apart
the objects in a band sit *from each other*, because that is what sets the row's ink height. A
band whose fields are all at the same Top loses nothing from being flushed; a band whose fields
differ loses the difference on every row of the report.

### A band cell's border and background belong to the object, not to the cell

**SalesByCustomer-Grouped 61.4 → 64.4%.** This is the item the previous band entry ended by
naming as the one thing a table cell could not express, and it turned out to be expressible
after all.

Crystal draws a border on the **object**. The previous change put a group band's content at the
object's own offset inside its cell using four-sided padding, which moves the text and nothing
else — a border belongs to the cell, and a background fills it. Measured at 300dpi:

| | real Crystal | before | after |
|---|---|---|---|
| "Order Amount" underline | x 1023–1327, **305px** | part of one rule, x 791–1888, **1,098px** | x 1025–1325, **300px** |
| "Date" underline | x 1523–1778, **256px** | *(the same rule)* | x 1525–1776, **251px** |
| group-footer subtotal box | x 942–1500, **558px** | x 789–1526, **737px** | x 948–1493, **545px** |

791 and 1888 are the Order Amount column's left edge and the Date column's right edge. The rule
was a picture of the table, not of the report.

**The fix is the shape the report header and page footer bands in this same table already use.**
A band cell now holds a `Rectangle` containing a `Textbox` at the object's own
`Left`/`Top`/`Width`/`Height`, rather than a bare `Textbox` filling the cell. `CellInsetTwips`
and `BandCellInset` become `CellFrameTwips` and `BandCellFrame`, returning the object's box
instead of four paddings; the guard that made the inset conditional — the object must actually
sit in the column it was name-matched to, since these cells are matched by field name rather
than by position — is preserved unchanged. `CellInset` itself stays, because the detail row
still uses it for right padding.

Crucially this is a **per-cell** wrapper and not a whole-band one. Routing the band through
`WriteTableFreeFormRow` was rejected for a specific reason: the group header's summary-field
path needs `BuildSummaryExpression`, which the free-form writer does not call, so a group's
"Count of X" would have quietly become a plain field reference. The per-cell form leaves every
value expression exactly where it was, and a test pins that the summary survives framing.

**The open question is answered.** When padding was chosen instead, the unknown was what this
engine does with a `Textbox` wider than its cell — Crystal's group caption is often as wide as
the whole band and simply overlaps whatever labels sit further across, and a table row cannot
overlap. `Rectangle.RunPage` offsets its children by the rectangle's left and **does not clip
them**, so the 11,340-twip caption is emitted at full width inside a 3,498-twip cell and
overlaps rightward exactly as Crystal does. Verified by render: caption ink spans x 65–575
against the reference's 65–579, uncut. **No clamp was needed.**

*What that does not cover:* no public report has an oversized caption carrying a **background**,
so the overlap has only ever been observed with transparent ones. A wide filled caption's paint
order over its neighbours is untested.

**Measured.** 33 of the 110 public RDL files change. Only `SalesByCustomer-Grouped` has both a
group band and a data fixture, so it is the only visual case that can move, and nothing else
does.

*Still out, and now the leading candidates.* The header band's underlines sit **6px above**
Crystal's and the detail row **8px above**. And the subtotal box renders 47px tall against
Crystal's 43 while the object's own recorded height is 263 twips ≈ 55px — so Crystal is drawing
that box to neither our height nor the object's bounds. Both are band-height questions rather
than placement ones, and neither is touched here.

### Can Grow is tag-252 data[9], and the object kinds Crystal greys it out for prove it

`ObjectFormat.CanGrow` has existed since the row-pitch fix and nothing ever set it, so it read
as a constant `false`. The flag is **tag-253 (ReportObjectProperties) → tag-252 child,
`data[9]`** — the low byte of a big-endian Int16 bool at `data[8..9]`, in the same record the
horizontal alignment already came from (`data[2]`), three fields further in. `ExtractHAlignment`
became `ExtractObjectProps` returning `(HAlign, CanGrow)`.

**No behavioural sample identifies this bit. The distribution does.** Across the 88 public
reports only three bytes in the 43-byte payload vary at all — `data[1]` (lockToSection),
`data[2]` (alignment) and `data[9]` — and `data[9]` splits by object kind in a way nothing else
would:

| object kind | objects | flagged | |
|---|---|---|---|
| subreport | 22 | 22 | **100%** |
| cross-tab | 8 | 8 | **100%** |
| text | 621 | 29 | 4.7% |
| field | 2,136 | 67 | 3.1% |
| cross-tab cell | 109 | 0 | 0% |
| line | 189 | 0 | 0% |
| box | 70 | 0 | 0% |
| picture | 27 | 0 | 0% |
| chart | 17 | 0 | 0% |

Crystal offers Can Grow for text, field, subreport and cross-tab and greys it out for the rest,
and it forces it on for subreports — which is exactly the 22/22.

**Then the same scan over the private corpus, 27× the sample, and this is what settles it.**
133,470 objects across 2,324 reports:

| object kind | objects | flagged | |
|---|---|---|---|
| subreport | 899 | 898 | **99.9%** |
| field | 76,411 | 12,149 | 15.9% |
| text | 44,920 | 1,623 | 3.6% |
| line | 7,047 | 0 | **0.0%** |
| box | 3,313 | 0 | **0.0%** |
| chart | 10 | 0 | 0.0% |
| picture | 623 | 1 | 0.2% |

An exact zero across **10,360 line and box objects**, against 99.9% on subreports, is the
signature of an option Crystal permits on some kinds and greys out on others. Three exceptions
in the whole corpus (one subreport without it, one picture and one other object with it) —
about 0.002%.

That also disposes of the one serious rival. **"Close Border on Page Break"** is the other
default-off Common-tab boolean, and it is offered *on* boxes and lines — so it could not be
0 of 3,313 boxes. "Keep Object Together" is checked by default and would be on for most objects
rather than 3.7%. "Suppress If Duplicated" is field-only and so cannot be on 29 text objects and
22 subreports; it is also disproved behaviourally — `Top5USA-piechart`'s `Text2` carries the
flag and the real-Crystal reference plainly renders it.

The objects carrying it read like the feature: `Remarks1`, `Comments1`, `OrderRemarks1`,
`ReportComments1`, `Resolution1`, `LineDescription2`, `DocumentHeader1` — free-text columns,
several drawn exactly one line tall. In the wizard-built reports it is on `Field4` (the Report
Comments special field) and off on the `Text3` label beside it, so it is per-object, not
per-report.

**Measured.** 51 of the 110 public RDL files change; 96 objects across 49 of the 88 reports set
it. Every visual-suite number is identical — no report in that suite has a flagged object whose
value is long enough to wrap, so there is nothing for the flag to do there.

*A count that did not reconcile, and why.* 54 files contain `<CanGrow>true</CanGrow>` but only
51 differ from before. The three are `CustomerList`, `ProductPriceList` and `ProductPriceList-xs`,
and the reason is that **`WriteTableReportFooter` writes an unconditional `true` on its own
spanning cell** and has never consulted the property at all. That predates this change. Now that
the flag is decoded it is arguably wrong, but correcting it would alter that band's row height on
every report that has a report footer, which is a measurement this change cannot make — noted in
the converter and left alone.

*What this does NOT establish.* **No reference render contains a flagged object whose value is
long enough to wrap**, so growth has never been observed. The reports carrying the flag in
quantity have no checked-in real-Crystal render, and the render-backed reports that carry it hold
one-line content. The bit is established by distribution across 2,412 reports and by the object
kinds Crystal permits it on — not by watching a field grow. A real-Crystal render of any report
with a long memo field would close that gap.

### A report was declaring a culture it had only ever been told about numbers

`ReportDefinition.Language` carries the number separators the file records —
`LanguageForSeparators` maps `(",", ".")` to `en-US` and `(".", ",")` to `de-DE` — and the
model says plainly what it is: *"a separator carrier, not a claim about where the report is
from"*. It was then emitted as RDL's report-level `<Language>`, which is not a separator
carrier. It is the whole report's culture, and **it takes dates with it**.

**Measured.** `Country-Region-Sort` prints a date in its page header. The converter emits
`=Format(Globals!ExecutionTime, "d")`, and `"d"` is the current culture's short-date
pattern. Rendered on the same machine, the same day:

| | renders |
|---|---|
| real Crystal | `2026-08-28` |
| ours | `9/11/2026` |

Crystal defers to the rendering machine there. We imposed `en-US` because that report's
*numbers* happen to use a comma and a period. No number and no separator is anywhere near
that field.

This is the same trap as "a date that defers to the machine was being given a fixed format",
approached from the other side. That entry stopped the *converting* machine's locale being
written out as a literal. This one was writing an *inferred* locale into the report and
letting it reach every date the report did not format itself.

**The fix is to put the separators where the separators are.** The report now names no
culture at all, and each object whose format string is a numeric picture carries the tag on
its own `Style`. No engine change was needed for this: `Style.EvalLanguage` uses the style's
own tag and falls back to the report's, and `ReportDefn.EvalLanguage` falls back to
`CultureInfo.CurrentCulture`. With nothing named at the report level, a date goes to the
machine — which is exactly what Crystal does, so the two now agree by deferring rather than
by our guessing the same constant.

*What identifies a numeric picture.* `,` and `.` in a .NET **numeric** format string are
placeholders the culture fills in, so `#,##0.00` only renders `1.234,56` under a culture
that spells it that way. Nothing else this converter emits needs that: a date format from
here quotes its separators (`MM'/'dd'/'yyyy`), and a date that defers to the machine carries
no format string at all. A `0` or a `#` is what distinguishes a numeric picture, and no date
pattern this converter writes contains either. The rule is mildly over-broad — a bare `"0"`
has no separator and is tagged anyway — in the one direction that cannot change any output.

*Checked before removing the report tag:* every `Format(...)` expression this converter
emits is a date. Two of them are the machine's own patterns (`"d"`, `"T"`) and the rest are
culture-independent literals for group keys (`"yyyy-MM-dd"`, `"yyyy-MM"`). Numbers never go
through `Format()` here, which matters because `FunctionFormat` reads the **report**
language and ignores the style's — that asymmetry is precisely why the print date was wrong
and the numbers were not.

**Measured:** **88 of 88** public reports and **2,300 of 2,324** private ones emit different
RDL, which makes this the widest change this converter has made. The 24 private reports that
do not change are the ones whose separators name no culture this recognises, so they never
had a tag to remove. 0 engine errors and 0 crashes across both corpora, 100% parse.

Every fixture-backed case in the visual suite moved up and none moved down:

| report | before | after |
|---|---|---|
| boyum__SampleReport | 80.5 | 81.1 |
| CustomerList | 90.2 | 90.6 |
| ProductPriceList | 63.3 | 63.6 |
| Orders10k | 74.4 | 74.6 |
| ProductPriceList-xs | 59.7 | 59.9 |
| SalesByCustomer-Grouped | 61.2 | 61.4 |
| Country-Region-Sort | 62.2 | 62.3 |
| BeforeTV | 75.2 | 75.3 |
| Orders5-150 | 59.9 | 60.0 |

*A note on how this survived.* The change that introduced the report-level tag was covered
by parser tests only — they assert that `("." , ",")` yields `de-DE`, which is still true and
still passes. Nothing asserted where the converter *put* it. Both halves now have a test.

### A group band's objects were losing their own position, and its caption its own font

**SalesByCustomer-Grouped 54.0 → 61.2%**, its largest move since it got a header table of
its own. It is the only case in the visual suite with both a group band and a data fixture,
so it is the only one that could show any of this.

Four separate faults in one band. Each was measured off the reference first, and each is
explained entirely by the report's own twips — no inference:

| object | report says | Crystal renders at | we rendered at |
|---|---|---|---|
| caption (Customer Name) | L=60 T=124 W=11340 | x 65–580px | x 162–603px, 14% narrow |
| "Order Amount" label | L=4680 T=124 W=1444 | x 1027–1257px | x 792–1024px |
| "Date" label | L=7080 T=120 W=1207 | x 1528–1602px | x 1528–1601px |
| band as a whole | T=124 in a 405-twip section | y 431–469px | y 405–438px |

At 300dpi and 1440 twips to the inch, with a 0.167in page margin, every Crystal number above
is the object's own bounds rendered literally. Ours are what a plain table cell does with
them.

**1. The caption's font was being thrown away.** Crystal's default group caption *is* the
group's own field, so the caption is very often a `FieldObject`. Only the `TextObject` case
was read here, and with nothing found the *value* fell back to the group field expression —
which is right — while the *format* fell back to a synthesized `{ Bold = true }`. That
discards the object the expression came from: its font name, its size, its colour and its
alignment all went to the engine's defaults. The caption came out 441px wide against
Crystal's 515.

This one is worth dwelling on, because a 17% width difference on a bold string looks exactly
like the font-metrics story in the entry above and is nothing to do with it. It was a font
this converter never asked for. With the object's own format read, the caption measures 510px
against Crystal's 515 — 1%.

**2. The lead column is not always padding.** It exists because something in the table
reaches left of the first data column — see the `leadCols` comment in `RdlConverter` — and a
group caption is very often that thing: Crystal puts it at the band's left edge, 60 twips in,
where the first detail column starts at 532. The group row wrote an empty lead cell
unconditionally, so the caption landed in the first data column instead: a third of an inch
right of the report's own position, and *indented past the detail rows it captions* rather
than sitting out to their left.

The caption cell now spans the lead column and the first data column together. It has to
span rather than simply occupy: a caption confined to the lead column would be clipped to
that column's width, which is by construction the gap between the band's left edge and the
first data column. Crystal's caption object is as wide as the whole band and simply overlaps
whatever labels sit further across; a table row cannot overlap, so the faithful compromise is
to give the caption the columns before the first one holding something of its own. A guard
establishes that before merging.

**3. A band's objects are not at their columns' left edges.** A group band's objects do not
define this table's columns — the detail band does — so an object here starts wherever the
report drew it, generally somewhere *inside* the column that contains it. "Order Amount" sits
1,122 twips into a column 3,522 twips wide, and as a plain cell it went to the column's start:
0.78in left of the reference, a clear label-width away from its own position.

**4. And they are not at the top of their row either.** A group band is one row as tall as
the whole section, and its objects sit at their own `Top` inside it. Written as plain cells
every one of them flushed to the top: this band's objects sit 124 twips down a 405-twip
section, and the band came out 26px high.

3 and 4 are the same fix, which is to make the cell's content region the object's own
rectangle — `PaddingLeft`/`PaddingTop` from the object's offset within the cell,
`PaddingRight`/`PaddingBottom` from what is left over. This is the four-sided form of the
padding detail cells already got on the right, where the object starts at the column's edge
by construction and only the far side needed closing. All four sides clamp at zero, because
RDL has no negative padding and an object bigger than its cell must ask for none.

Cells in these bands are matched to columns by field *name*, not by position, and the two can
disagree — a group footer's Sum of a field goes in that field's column wherever the summary
happens to have been drawn. An inset measured from a column the object does not sit in
measures nothing, so `BandCellInset` requires the object to be inside the column before its
offset within it is used.

**Result:** every glyph run in that band now lands within 2px of the reference, horizontally
and vertically, where the caption had been 99px out and the band 26px high.

**Measured:** 27 of the 88 public reports and 1,360 of the 2,324 private ones emit different
RDL. 0 engine errors and 0 crashes across both corpora (110 and 3,223 RDL files including
subreports), 100% parse.

#### What is still wrong there, and what it needs

One thing, and a table cell cannot express it. Crystal draws each column label's underline
across **that label's own width** — 305px under "Order Amount", 256px under "Date". Ours
draws a single continuous 1,098px rule, because a border belongs to the cell and padding
moves only the text inside it. The grey subtotal box is wider than Crystal's and starts
further left for exactly the same reason: a background fills the cell.

The fix is to wrap a band cell's object in a `Rectangle` holding a `Textbox` at the object's
own `Left`/`Top`/`Width`/`Height`, which is how the report header and page footer bands in
this table already work (`WriteTableFreeFormRow`) and would subsume the padding above. It was
not done in the same change for two reasons worth recording: a caption whose object is as
wide as the band would then be a `Textbox` far wider than its cell, and what this engine does
with that overflow is not known; and the group header's summary-field path needs
`BuildSummaryExpression`, which the free-form writer does not call, so routing the whole band
through it would silently turn a group's "Count of X" into a plain field reference.

*Also still open, and deliberately untouched:* the detail row has the same vertical story in
miniature — its objects sit 15 twips down a 289-twip band and are flushed to the top. It is
3px, and the detail path is the widest in this converter; the group bands are the measured
case.

### A date that defers to the machine was being given a fixed format

The previous entry ended by naming `Orders5-150`'s date column as the thing that report
still got wrong: `12/02/2000` where Crystal renders `2000-12-02` beside a time. Fixing it
took **Orders10k 61.1 → 74.4%** and **Orders5-150 49.1 → 59.9%**.

**The method is the reusable part.** Crystal's CSV export writes *rendered* values, so
exporting a report hands back its formatted dates as strings and a record's bytes can be
paired directly with what the real engine does with them — no reading pixels, no decoding
font subsets. Seven public reports display a date field and all seven export cleanly:

| bytes[0..7] | stored sep | Crystal renders |
|---|---|---|
| `02 01 01 01 02 02 02 01` | `/` | `04/24/2001` (4 reports) |
| `02 01 00 00 02 01 02 01` | `/` | `2000-12-03  12:00:00AM` (2 reports) |
| `01 01 01 01 02 01 02 01` | `/` | `2000-12-09` (1 report) |

Same column of the same sample database in several of them, rendering three different ways.
**bytes[2] and bytes[3] are what separate them**: `0,0` means no date components are
configured and the field takes the machine's own short date, which is also why those rows
ignore the `/` they store.

**I nearly shipped the machine's locale as a format, and an existing test stopped it.**
Reading the ISO strings above as formats the report had asked for, I wrote
`yyyy-MM-dd  h:mm:sstt` for the `0,0` case and `yyyy-MM-dd` for `bytes[0] = 1`, and measured
Orders10k at 76.1% and Orders5-150 at 61.0%. Then
`RptParser_DateFieldDeferringToTheMachine_GetsNoFormatAtAll` failed, asserting that its
report must carry no format because "order 1 means use whatever short date the machine has".
It was right: **this machine runs en-CA, whose short date is `yyyy-MM-dd`.** Every ISO date
in that evidence table is this box, not the report. Written out as a literal it would have
baked the converting machine's locale into every report converted on it, and the visual
suite would have rewarded it for doing so.

So the fix is to emit **no format at all** for the `0,0` case, where a fixed `MM/dd/yyyy` was
going out before, and to leave `bytes[0] = 1` exactly as it was — already emitting nothing,
for the reason that test records. Deferring costs 1.1 and 1.7 points against the locale-baked
version, and it is right rather than lucky: our engine and Crystal both fall back to the
machine for an unformatted date, so the two agree by deferring instead of by matching a
constant.

*Everything else is unchanged.* `bytes[0] = 2` with components set still means
`MM{sep}dd{sep}yyyy`, confirmed by four reports. `bytes[0] = 0` still means
`yyyy{sep}MM{sep}dd` and is still confirmed by nothing — it never fires on a date field in
either corpus, being the pattern non-date objects carry, since every field object holds one
of these records whatever its type.

**Measured:** 5 of 88 public and 83 of 2,324 private reports emit different RDL. 0 fatal and
0 exceptions across both corpora.

*What is still not decoded, and why not.* The private corpus holds at least thirteen distinct
patterns here, with bytes[2] and bytes[3] taking 0–3 and separators including a space —
almost certainly per-component codes for month, day and year of the kind Crystal's Format
Editor offers:

```
   14232  0201000002010201 sep=/      1408  0201010102010201 sep=/
   12890  0000010102010201 sep=-      1096  0201010102020201 sep=/
     823  0001010102010201 sep=-       462  0201020102020201 sep=' '
     302  0101010102010201 sep=/       160  0201020002020201 sep=' '
     141  0201000002020201 sep=/       130  0201030102020201 sep=' '
```

Three renderings cannot decide between thirteen patterns, and a date printed in the wrong
order is worse than one printed in the machine's. Going further needs more reports that both
display a date and render — which the CSV route now makes cheap to collect, and is the
concrete next step here rather than an open question.

### Crystal's point size is a cell height, not an em: the 11% "ceiling" was ours

This suite had recorded an 11% glyph-width difference as a permanent limit — a renderer
disagreement neither side could fix, on the reasoning that our text matches Arial's
published metrics and Crystal's is narrower. Our text does match Arial's published metrics.
The em was the wrong measurement to match.

**Crystal's own PDF export gives the answer without any inference.** Rendering
`CustomerList` with the licensed engine and reading its content stream:

```
/d 8.95 Tf    <- the report's 10pt Arial objects
/c 14.3 Tf    <- its 16pt object
/c 29.55 Tf   <- its 33pt object
```

Every one of them is the nominal size times **0.8951**, and 0.8951 is Arial's
`unitsPerEm / (usWinAscent + usWinDescent)` = 2048/(1854+434). That is the ratio between an
em and a **character cell**. Crystal hands its point size to GDI as a *positive* `lfHeight`,
which GDI reads as cell height — ascent plus descent — and then picks whatever em makes the
cell that tall. RDL's `FontSize` is the em directly. So the converter was emitting a cell
height as an em, and every glyph came out about 11% too wide.

The ratio is the font's own, not a constant. Read from the font files:

| family | upem/(winAsc+winDesc) |
|---|---|
| Arial | 0.89510 |
| Times New Roman | 0.90300 |
| Courier New | 0.88276 |
| Cambria | 0.85298 |
| Tahoma | 0.82848 |
| Verdana | 0.82282 |
| Impact | 0.81986 |
| Calibri | 0.81920 |
| Arial Black | 0.70914 |

`usWinAscent`/`usWinDescent` rather than the `hhea` pair because GDI's `tmHeight` is built
from those; for every family here but Calibri the two agree, and Calibri's `hhea` would give
1.0 against the OS/2 pair's 0.8192.

**Checked against a second family before shipping.** `SalesByCustomer-Grouped` uses both
Arial and Verdana, and Crystal writes `6.6 Tf` for its 8pt Verdana objects and `8.25` for its
10pt ones — predicted 6.58 and 8.23. Weight does not enter into it: Arial and Arial Bold
carry identical metrics, as do Verdana's four variants.

*Crystal's numbers are not exactly the ratio either.* 8.95/10 and 29.55/33 give 0.8950 and
0.8955; Verdana's two both give 0.8250 against the font's 0.8228. GDI rounds ascent and
descent to whole device pixels before deriving the em, so the effective ratio wobbles by
about 0.3% with size and resolution. A third of a percent of a 10pt font is a fortieth of a
point. The tests compare within 0.03pt for that reason.

**A second, smaller fix rode along.** The font record carries the size twice: the byte this
parser read, rounded to whole points, and the same size in twips at `nc+13..nc+16`. The byte
loses every half-point size — **2,581 records in the private corpus** disagree with it,
among them 5.95pt (1,090 records), 7.95pt (614) and 10.5pt (258). The twips field is now
read, guarded by the byte: a couple of records hold something implausible at that offset, and
those fall back. This half is **not covered by a test and cannot be** with what is committed
here — every size in the public corpus is a whole number of points, so byte and twips agree
on all 3,142 of its font records and a test over them passes whichever is read.

**Measured, and this is the widest change this project has made:** all **88 of 88** public
reports and **2,316 of 2,324** private ones emit different RDL. 0 fatal and 0 exceptions
across both corpora.

| report | before | after |
|---|---|---|
| CustomerList | 61.0 | **90.2** |
| boyum__SampleReport | 57.0 | **80.5** |
| BeforeTV | 57.4 | **75.1** |
| Orders10k | 57.2 | 61.1 |
| ProductPriceList | 62.8 | 63.3 |
| Country-Region-Sort | 60.9 | 62.2 |
| ProductPriceList-xs | 59.5 | 59.7 |
| SalesByCustomer-Grouped | 54.8 | 54.0 |
| Orders5-150 | 51.1 | **49.1** |

That is more movement than every other change to this suite put together.

*The two that went down, honestly.* `SalesByCustomer-Grouped` −0.8 is inside the suite's
tolerance. `Orders5-150` −2.0 is not, and it is worth being precise about: its glyph widths
now match the reference to within 1.5% across six measured strings, and its rows sit within
2px of Crystal's at an identical 46px pitch. What is left is a **date column** — it renders
`12/02/2000` where Crystal renders `2000-12-02` beside a separate `12:00` object we do not
draw at all. With the text finally the right size, that column is most of what the report
still disagrees about, and the score moved against us because the mismatch it contains is now
the dominant term rather than because anything got worse. *(Done — see the entry above,
which takes it to 59.9%.)*

*Still open:* the 1.21 width ratio previously measured on `SalesByCustomer-Grouped`'s 18pt
italic title is not explained by this and is not a cell-height effect. And a family whose
metrics are not in the table above is emitted unchanged — its true ratio is its own font's,
guessing would mis-size text silently, and leaving it alone preserves the behaviour this
converter has always had. That covers every family in the public corpus and all but a few
hundred objects of the private one.

### The Highlighting Expert, declined twice for want of a case that existed

Tag 191 is Crystal's Highlighting Expert, and it was written down twice as understood but
not worth attempting — partly on rarity, partly because the operator codes could not be
pinned down, and partly because there seemed to be no report whose render could check the
answer. The last of those was simply false: `SalesByCustomer-Grouped` carries two rules, has
a fixture, and is measured by the visual suite.

Its page holds one detail row and one subtotal, and the subtotal renders **red on grey** in
Crystal against black on white in ours. On a page that sparse that was most of the remaining
disagreement.

**The layout,** two length-prefixed ASCII numbers and two colours:

```
  [0..3]  comparison operator, little-endian: 3 = less than, 5 = greater than
  [4]     length of the threshold string, including its terminator
  [5..]   the threshold as ASCII decimal, NUL-terminated ("1000.00")
  +3      padding
  [.]     a second value, same framing - "0.00" in every record in either corpus
  +1      padding
  [..]    font colour, flag,B,G,R - flag 0 sets it, 2 leaves it alone
  [..]    background colour, same encoding
  [.]     one trailing byte
```

**The operator codes are the whole difficulty, and one report settles them.** With a single
sample, operator 3 against threshold 1000.00 on a subtotal of $53.90 is equally consistent
with "less than" and with "not equal" — and a rule applied with the wrong comparison
highlights the wrong rows, which is worse output than no highlight. `USA-Orders-RWB-colored`
resolves it: one rule, operator 5, threshold 100000.00, colour bytes `00 00 80 00`, and the
real engine renders **every one of its 20 state subtotals above $100,000 green and every one
below in the object's own colour**. Twenty samples on one side of one threshold is a
greater-than and nothing else. The same render fixes the channel order, because `00 00 80 00`
read as flag,B,G,R is exactly the `#008000` on the page and no other split of those bytes
is. Operator 3 is then the complement, confirmed by the red-on-grey subtotal.

Anything but 3 or 5 is dropped rather than guessed at, which is the same rule that made this
implementable at all.

**In RDL** it becomes one nested `IIf` per colour over the object's own value expression,
first rule outermost because Crystal applies the first match, with the object's own colour as
the innermost else:

```
<Color>=IIf(Sum(Fields!Order_Amount.Value) &lt; 1000, "#FF0000",
        IIf(Sum(Fields!Order_Amount.Value) &gt; 100000, "#008000", "#000000"))</Color>
<BackgroundColor>=IIf(Sum(Fields!Order_Amount.Value) &lt; 1000, "#C0C0C0", "Transparent")</BackgroundColor>
```

A rule that sets only one colour is left out of the other expression — the second rule above
has background flag 2, so the background expression names only the first. Resolving it to
white there would paint over whatever sits behind the object. A cell whose value is literal
text has nothing to compare and its rules are dropped.

**How rare it actually is, corrected twice.** The original note said 3 records in 2 files
across 2,526, then a survey this round said 7 public files and 117 private and I called the
original wrong. The original was right and the correction was the error: that survey counted
**empty** tag-191 records. The census by length:

| | records | files | carry a rule |
|---|---|---|---|
| public | 8 | 7 | **3** |
| private | 678 | 117 | **0** |
| rpt-rs | 1 | 1 | **0** |

So 683 of 687 records in the whole collection are zero-length — an object carrying the
feature's slot with nothing in it. The rule-bearing three are all public.

**Measured:** 3 of 88 public reports emit different RDL and **0 of 2,324 private**. The third
public one is `Top5USAwithSub`, whose subreport is the same content as RWB; note that the
tag census walks a report's own stream and so does not see subreport records, which is why
it counts 2 files where the RDL diff counts 3 — the RDL diff is the number that matters.
**SalesByCustomer-Grouped 48.3 → 54.8%**, its second largest move. 0 fatal and 0 exceptions
across both corpora.

*Three cases dipped 0.1 and their RDL is byte-identical,* so that is the suite's own
anti-aliasing jitter rather than movement; `BaselineTolerance` exists for exactly this.

### A currency symbol I shipped onto every plain number, and the byte that says not to

The numeric-format round taught the parser to read decimals and a currency symbol out of
the tag-249 → 248 record, and it put a symbol on numbers Crystal leaves bare.
`ProductPriceList`'s Product ID came out **`$1,101`** against Crystal's **`1101`**. The
visual suite scored that report 59.6% — squarely in line with the other list reports — so
nothing about the number looked wrong; it was found by reading the render.

*Two things the suite did not do here, worth naming.* The ink metric compares an 8px-cell
mask, and `$1,` in a right-aligned cell moves few enough cells to sit inside the noise. And
the corpus scan is a crash control, not a fidelity one: all 88 files rendered before and
after, because a wrong format string is still a valid format string.

**What decides it is `data[4]`.** The report gives the answer on its own, because its two
numeric fields disagree while storing the same thing:

```
idx:        0  1  2  3  4  5  6  7  8  9
ProductID:  00 00 03 00 00 00 01 00 00 0B   -> Crystal renders 1101
Price(SRP): 00 00 01 00 01 00 01 00 02 09   -> Crystal renders $14.50
```

Both records carry a `"$"` in the currency slot and a `","` in the thousands slot. Reading
the slots alone therefore cannot tell them apart — which is exactly the mistake that
shipped. `data[4]` is 1 on the price and 0 on the ID, and it governs **both** the symbol
and the grouping.

The grouping half rests on `1101` being four digits: had Crystal wanted the separator it
would have printed `1,101`. `ConsolidatedBalanceSheet`, whose `data[4]=1` columns render
`932,694.00`, agrees from the other side. `data[2]` is not the flag — it takes 0, 1 and 3
independently of `data[4]`, and the largest group in either corpus is `data[2]=3` with
`data[4]=1` and a symbol.

*What is not settled:* whether the flag also suppresses the **decimals**. Every one of the
41 public `data[4]=0` records has zero decimals, so the corpus that decided the flag is
silent on it, and 75 private records pair the flag off with two decimals. Those keep their
decimals here, which is the conservative reading — stated in the parser so the next person
does not read it as established.

An earlier draft of that comment claimed no `data[4]=0` record anywhere has decimals. That
was true of the public corpus only, and is corrected in place.

**Measured:** 5 of 88 public and **111 of 2,324 private** reports emit different RDL, each
changed file gaining exactly one bare `<Format>0</Format>` and nothing else. The 18 public
records that carried a symbol against `data[4]=0` were all rendering one Crystal suppresses.

Visual regression: **`ProductPriceList` 59.6 → 62.8%**, the only fixture-backed report with
a flag-off field. Two new tests, both driven off that one report so the pair is compared
inside a single file; teeth-checked by reverting the parser, where the flag-off case fails
and the flag-on case still passes.

Verified: 0/88 public and 0/2,324 private fatal, 0 exceptions, 100% parse both corpora,
909 crystal tests, 5 RptEngine, visual regression 12 green + 1 ignored.

### A detail band's columns come out in the order the report was written, not drawn

`ProductPriceList-xs` entered the visual suite at 33.8% against the 62.8% of
`ProductPriceList`, which is the same report with one extra column. Reading the diff, the
detail values sat progressively further left of their own column headings — `Color` almost
right, `Size` about 110px off, `Price` about 370px — while the headings themselves landed
exactly on the reference.

*The first diagnosis was wrong and is worth recording as such.* I read the growing gap as
the formula column, `Num. Xs`, being missing from our table, and wrote that up. It is not
missing: the converter emits it as a calculated DataSet field, and the table has all six
columns. Grepping the emitted RDL rather than eyeballing the diff is what corrected it.

**What is actually wrong is the order.** The detail objects are recorded in the order the
report was authored, which has nothing to do with where they are drawn:

```
FieldObject L=120   W=1095    Product ID
FieldObject L=1406  W=2794    Product Name
FieldObject L=4455  W=1320    Color
FieldObject L=6255  W=1344    Size
FieldObject L=9675  W=1128    Price (SRP)   <- rightmost, recorded fifth
FieldObject L=8520  W=950     Num. Xs       <- to its LEFT, recorded sixth
```

Columns, widths and cells are all built from that list in the order it arrives, so `Price`
became column 5 and `Num. Xs` column 6, each carrying the other's width.

**And a second fault rode on the first.** Column widths are meant to be the distance to the
next column, because Crystal leaves gaps and an RDL table's columns are contiguous — taking
each object's own width closes every gap and drags everything to its right leftwards,
cumulatively. That logic is guarded on the column starts ascending, and here they do not, so
it silently fell back to object widths. This is why the displacement *grew* across the row
rather than being a single swap at the end. The guard was written as a condition to detect
and back away from; it is really a condition to fix.

**Sorting each detail band's objects by Left fixes both**, and the widths come back
gap-based on their own: 0.893in for the first column, the 1286-twip distance to the next
object, where it had been emitting the 1095-twip object width. Sorted within each section
rather than across all of them, so a report with stacked detail bands does not get its bands
interleaved into one left-to-right sequence; `OrderBy` is stable, so tied Lefts keep the
order they were recorded in.

**This was not a one-report problem.** Of the reports with two or more detail field objects,
**34 of 73 public and 1,575 of 2,049 private** record at least one out of position — 47% and
77%. All of them were getting mis-ordered columns and gap-closing widths.

**Measured:** 33 of 88 public and **1,627 of 2,324 private** reports emit different RDL, the
widest change this suite has produced. `ProductPriceList-xs` **33.8 → 59.5%**, into line
with its sibling, and every other case in the suite is unchanged — it is the only
fixture-backed report with an out-of-order band.

*What this does not fix,* and the counts here correct the ones the commit message carried.
That first survey compared each object with the one recorded before it across the whole
concatenation, so it reported "tied Lefts" for two objects in *different* bands sitting at
the same Left — which is not a tie, just two rows starting in the same column. Measured
per band:

| | public | private |
|---|---|---|
| reports with 2+ detail field objects | 73 | 2,049 |
| a band recorded out of left-to-right order | 32 | 1,555 |
| two objects sharing a Left **inside** one band | **0** | **200** |
| more than one Details band | 8 | 450 |
| still failing the ascending guard after the sort | 8 | 603 |

So the public corpus has no genuine within-band tie at all, and every public report still
falling back does so for one reason: **more than one Details band.**

### Blocked on a reference: one table row per Details band

Crystal allows Details_a through Details_z, each drawn as its own line under every record.
The converter flattens all of them into a single table row, one column per object across
every band, which is structurally wrong rather than merely misplaced. `boyum__Documents` is
the clearest case — four bands, and after the per-band sort its concatenation runs
840–9575, then 840–9667, then 2861, then 2861:

```
band 1: @Line_ItemCode  @Line_Description  @Line_Quantity
        @Line_PriceBeforeDiscount  @Line_DiscountPercentage  @Line_Total
band 2: @Line_Description (8357 wide)  @Line_Total
band 3: @SpecialLine_Text
band 4: @SpecialLine_Text
```

Ten objects become ten columns of one row, where Crystal prints four lines. The shape RDL
wants is a column grid built from every band's distinct Lefts and one `TableRow` per band,
cells spanning with `ColSpan` — a real change to `WriteDetailsTable`, worth 8 public and
**450 private** reports.

**Not attempted, because it cannot be measured** — but the reason is narrower than the
first version of this entry claimed, and that version was wrong on both of its points.

It said none of the 8 public multi-band reports can produce a reference render. Two can.
Six return `ParameterFieldCurrentValueException: Missing parameter values`, but
`ServiceContract` and `ServiceContract_HANA` render from their own saved data, and richly:
a header block of 14 labelled fields, a **4-column, 2-row detail table** (`HP Printers` and
`IBM Printers`, the second row on a grey band), a 7-row coverage table and a comments box.
The second detail band has exactly 4 field objects, matching that table's 4 columns.

So the blocker is not the reference, it is the fixture. `FixtureBuilder` reports:

```
fields (50): CardName, ContactPerson, Owner, Status, ServiceType, ... CardCode
no usable label row; reading export columns positionally
No exported row has 50 values that are not column labels. Widths present: 1, 4, 39.
```

Those widths are the shape of the report: **one 39-value row is the master record** — the
header block — and **the 4-value rows are the detail rows**, matching the detail band's four
objects. The export does contain the rows. `FixtureBuilder` rejects them because it only
recognises a detail row at the full width of the field list, which is the right rule for
every list report it was built for and the wrong one here.

*What would unblock it:* a fixture builder that understands a master/detail export — emit
one row per detail row with the master's values repeated, since an RDL DataSet is one flat
table. The detail half of that mapping is well founded now that detail objects are read
left to right: four objects, four columns, in order. The master half is not — nothing says
which 39 of the 50 fields those columns are, or in what order, and guessing would make the
reference an opinion rather than a measurement. That is why this is written down and not
built.

### The export's columns are what the report shows, not what the database has

`FixtureBuilder` read the exported grid positionally: column 0 into field 0, and so on.
That holds only while the report displays exactly its database columns in order.
`ProductPriceList-xs` displays a formula between two of them, so it exports **six** columns
against **five** fields, and everything from `Color` onwards landed one place off.

The failure was worse than a dropped fixture, because it was silent. Rows that happened to
carry five values — the ones with no `Color` — passed the width test and were written out
shifted, so the committed fixture said `Color="xsm"` and `Size="1"` and nothing complained.
It had 19 rows where the report has 115, and its render was being measured against a
reference built from data that was simply wrong.

**Now the columns are matched by the labels the export itself carries.** A row whose cells
name every field exactly once becomes the column map, and a column no field claims — the
formula one — is skipped. Failing that, the positional reading stands: plenty of reports
have no usable label row, `SalesByCustomer-Grouped` among them, since it labels two of its
three columns and the third is a group name.

One guard on top, because a label row that maps cleanly can still be the wrong row: the
detail data has to actually reach the last column the map claims. Without it a coincidental
match on a narrow header band could map fields onto columns no value ever occupies.

**All 9 committed fixtures regenerate byte-identical**, so this only fixes what was broken.
`ProductPriceList-xs` goes 19 → **115 rows**, matching its sibling, and it is added to the
visual suite, where it immediately exposed a much wider defect (see the entry above).

*Still not fixed by this:* the fixture is only ever as good as the export, and a report
that suppresses its detail rows exports no rows to align. The tool prints the map it chose
and the row count it produced, and reading that before committing the output remains the
rule — it is what caught this.

### A null does not shorten a row, it empties a column: 39 rows back, one new case

`FixtureBuilder` compacted each exported row to a list of its values, on the stated
reasoning that "a row's own values are ordered but its column indices mean nothing" — true
of a group subtotal, which lands alone in column 0 whichever column it prints under. It is
**not** true of a detail row, and dumping the BIFF grid with its indices shows why:

```
row   1: n=5  cols=[0,1,2,3,4]  Product ID | Product Name | Color | Size | Price (SRP)
row   2: n=4  cols=[0,1,3,4]    1101 | Active Outdoors Crochet Glove | xsm | 14.5
row  13: n=5  cols=[0,1,2,3,4]  2201 | Triumph Pro Helmet | black | sm | 41.9
```

Row 2 is not missing a *value*, it is missing **column 2** — that glove has no colour.
Compacting slid Size and Price one place left, the row came out four values wide, and it was
dropped as short. That cost `ProductPriceList` 39 of its 115 rows, and the previous entry's
claim that nothing could tell such a row from a header was simply wrong: its column index
says so.

Rows are now also built column-indexed, and a short row is taken as a detail row when every
value it does have is the right *kind* for the column it sits in and it fills at least half
its columns. The half-full test is what keeps a group subtotal out — a lone number in
column 0 agrees with the first character of the detail shape and nothing else. The
label-row guard still applies.

**All seven committed fixtures regenerate byte-identical**, so this only ever adds rows that
were being lost. `ProductPriceList` goes 76 → **115 rows**, and the tool now reports
`recovered 39 row(s) that a null had pushed out of the shape vote` along with the shapes it
saw.

**`ProductPriceList` is added as the eighth fixture-backed case, at 59.6%** — straight in
line with the other list reports, which is its own evidence that the recovered rows are
right. Without its fixture it scores 8.7%.

*The other two candidates are still left out, with better-stated reasons than before.*
`ProductPriceList-xs` recovers only 2 rows and lands at 19 where its sibling report has
115 — something else is dropping rows there, it is not understood, so it stays out.
`TenPct-DiscountDays` recovers none and stays at 22, which is plausible for a report that
filters to ten-percent discount days but is not verified against a row count from the real
engine.

*What this does not fix:* a report whose detail rows are short **and** whose value kinds
collide with another row shape is still ambiguous, and a fixture is only ever as good as the
export. The tool prints what it recovered so the count can be checked against the report
before committing, which remains the rule.

### The drop shadow, third time asked and this time worth it

Twice declined on proportion, and taken now because it was the only measurable gap left:
`SalesByCustomer-Grouped` was the outlier among fixture-backed reports and the four
worst-agreeing 8px bands on its page were all the header's shadow.

Emitted as the **two strips that are actually visible** — one below the object shifted
right, one to its right shifted down, filled black, written before the object:

```
below:  (Left + 72tw, Top + Height, Width,  72tw)
right:  (Left + Width, Top + 72tw,  72tw,   Height)
```

72 twips is 0.05in, which is the 15px measured off the reference at 300dpi. Two strips
rather than one offset rectangle behind the object, because a shadowed object normally sets
no background of its own — every border record on this report carries `bg=FFFFFFFF` — so a
single rectangle would show through the interior and fill the box black instead of edging
it.

**`SalesByCustomer-Grouped` 42.5% → 48.3%**, its second largest single move, and nothing
else in the suite changes by a tenth.

**Its reach is one report, and that is worth stating plainly.** Counting objects whose
parsed `Format.DropShadow` is set:

| Corpus | Files | Objects |
|---|---|---|
| public | 1 | 1 `FieldObject` in a `ReportHeader` |
| private | 2 | 2 `TextObject`s in a `GroupFooter` |

Only the public one gets a shadow. The two private ones are in group footers, and where a
details table exists that content becomes a **table cell**, which has no Left/Top for a
strip to be positioned against — the same limitation as the cell-border entry. So 1 of 88
public and **0 of 2,324 private** reports emit different RDL. (An earlier count of "42
shadowed objects" in the private corpus came from scanning border *records* rather than
parsed objects, and overstated it by counting records the object parsers never read.)

Both corpora stay at 0 fatal with all 12,263 private non-fatal occurrences identical per
file per message.

### Not pursued: a formula's result type, which is what the Boyum family needs

The numeric-format entry notes that its type gate only recognises database columns. The 44
Boyum reports in the public corpus are the cost of that: their placed objects reference
**formula** fields (`@Date`, `@CustomerName`, `@Address`, `@Title_Customer`), and nothing
infers a formula's result type, so none of them gets a `<Format>`.

What that is worth was checked before spending on it, by rendering one with the licensed
engine: its numbers come out `50.00`, `200.00`, `37.50` — **two decimal places, no currency
symbol, no visible grouping**. So the prize is decimals on the Boyum family, not currency.
Real, but modest.

What it would cost is the open question. The result type does not appear to be in the
formula's own record: tag-119 → tag-118 carries the name, a dependency list and the formula
text, and its tag-113 children are `(name, typeCode)` pairs for *other* fields rather than
for the formula itself — `@X_Language`, a formula returning a string, has a tag-113 child
naming `Title` with type 11. So this is either more binary research or type inference over
the formula text, and neither is a small piece.

It is also **unmeasurable by this suite**: no fixture-backed report is a Boyum report with
numeric columns, so the only verification available is eyeballing reference renders. That
combination — moderate cost, modest prize, no metric — is why it is written down rather
than attempted. If it is ever taken up, inferring only the obvious cases (a formula whose
text is a single summary or arithmetic over numeric columns) would cover most of the
family without a general type system.

*(Since done, that way: see "A formula's number format was always dropped". The field
object was re-checked first and does not carry the type either; the 12 bytes after its
field name are a reference to the field, not its type. The suite gained a way to measure it
when `TenPct-DiscountDays` became fixture-backed.)*

### The report now names its own Language, so formatting stops depending on the host

`Style.FormatValue` applies a BCP 47 culture to every formatted number and date, and takes
it from the style's `Language`, falling back to the report's, falling back to
**`CultureInfo.CurrentCulture`**. Nothing in this converter emitted a `Language`, so until
now every report's numbers and dates were formatted with whatever culture the *rendering
machine* happened to be set to. On this host that is en-US and the output looked right; on
a Danish box the same report would have rendered `1.234,56`.

The report now declares one, derived from the separators the numeric-format record names:

| Recorded separators | Language |
|---|---|
| `,` thousands, `.` decimal | `en-US` |
| `.` thousands, `,` decimal | `de-DE` |
| no thousands, `.` decimal | `en-US` |
| anything else | none emitted |

**It is a separator carrier, not a claim about locale.** `de-DE` stands for every
"1.234,56" convention here, not for Germany; .NET has no way to state separators in a
format string directly, so naming a culture that spells them correctly is the only route.
Verified against the engine with a hand-written RDL before the converter was touched: a
report-level `da-DK` renders `1234567.89` as `1.234.567,89`, and a style-level `en-US`
override on the same page renders `1,234,567.89`.

With the culture named, `BuildNumericFormat`'s separator check is gone — `#,##0.00` is
placeholders and the culture fills them.

**Two things to be accurate about.**

*The separator relaxation is currently inert.* No corpus report both uses European
separators and places a numeric `DatabaseField`: the Boyum SAP templates that carry
`.`/`,` reference **formula** fields (`@Date`, `@CustomerName`, `@Address`), and the type
gate only recognises database columns because a formula's result type is not inferred
anywhere. So the Danish reports gain the `Language` but still no `<Format>`. Inferring
formula result types is what would unlock them, and it is a bigger piece of work than this
was.

*The visible effect here is robustness, not appearance.* All 88 public reports gain exactly
one line and nothing else — confirmed by diffing a report's whole RDL before and after.
Three fixture-backed cases move by a tenth or two (CustomerList 60.9% → 61.0%, Orders10k
57.1% → 57.2%, SalesByCustomer-Grouped 42.3% → 42.5%) because the print date and the
amounts are now formatted by a named culture rather than by coincidence. Both corpora stay
at 0 fatal with all 12,263 private non-fatal occurrences identical per file per message.

### Implemented: the array literal, as an argument and only as an argument

The external corpus's last fatal is gone. `meridian_probes__21_string_functions` holds

```
Join([{tier}, {name}, {currency_code}], " - ")
```

and the grammar had rules for `primary[expr]` subscripting and for `In [list]` but none for a
standalone array literal, so the formula fell to the regex fallback, which resolved the field
references and left Crystal's `[` in place. The engine's expression parser then rejected it —
*Constant or Identifier expected but not found. Found '['* — and the whole report was lost.

**The rule is confined to the one position where it cannot be ambiguous.** An array literal is
a function *argument*, not an `expr` and not a `primary`:

```csharp
argList.Rule    = MakePlusRule(argList, ToTerm(","), arg);
arg.Rule        = expr | arrayLit;
arrayLit.Rule   = "[" + arrayElems + "]";
arrayElems.Rule = MakePlusRule(arrayElems, ToTerm(","), expr);
```

Both existing bracket forms need something consumed before their `[` — `sliceExpr` needs a
primary, `expr In [list]` needs the `In` keyword — so restricting the array to the one place
where nothing has been consumed means no two of the three rules are ever live at the same
bracket. Making it an `expr` instead breaks both: `{X}[1]` becomes reducible either as a slice
or as primary-then-array, and `{X} In [1,2]` gives a reduce-reduce conflict between
`caseValueList` and the array's element list, both being comma-separated expression lists
ending in `]`.

**Irony's own conflict list was measured before and after: 4 before, the same 4 after** — the
pre-existing Select-Case lookahead, the `( … )` reduce-reduce and the dangling `Else`. No new
conflict, and none suppressed.

The emitter turns `Join([a,b,c], " - ")` into `(a & " - " & b & " - " & c)`; a literal given to
any other function spreads into arguments, so `Maximum([1,2,3])` becomes `Max(1, 2, 3)`; a
`Join` over a non-literal is left alone.

**Measured.** External corpus 114/114 convert, and all 121 emitted RDL files are byte-identical
to before **except the single target expression**, which now reads
`=(Fields!tier.Value & " - " & Fields!name.Value & " - " & Fields!currency_code.Value)`. Public
corpus 88/88 with all 110 emitted files byte-identical — nothing else in either corpus uses the
form.

*The guard that mattered.* Seven private reports carry `[` **inside string literals**
(`"Fund: [" & {?Fund} & "] has been Selected"`), which is exactly what a careless fix breaks.
Those are pinned by tests, and the teeth-check is the useful part: with the grammar change
stashed the four array-literal tests fail while the bracket-in-string, `primary[expr]`,
`[n To m]` and `In [list]` guards all still pass — they guard behaviour that was already there.

*Explicit limitation.* The rule handles a **flat, single-level** list only. `[[1,2],[3,4]]` and
a bare `[1,2,3]` as a whole formula body are still parse failures falling to the regex fallback
exactly as before. Crystal's own arrays do not nest and there is nothing in RDL to emit for one,
so this is a deliberate stopping point rather than an oversight; both limits are pinned by
`ArrayLiteral_StandaloneOrNested_IsStillNotParsed` so that a later change cannot quietly claim
more than it does.

### The new corpus's fatals: a chart category read as a typeface, and two functions

The external corpus went from **12 fatals in 114 files to 1** on three fixes. All three are
invisible to the main corpora, which is the whole point of having it: 0 of 88 public and 0
of 2,324 private reports emit different RDL after these changes.

**A chart's category was being read as a font name (7 of the 12).** The tag-289 chart
definition record ends with the chart's font block — eight or more names like `Arial` or
`MS Shell Dlg` — and `ScanStrings` brute-forces every MUTF-8 string out of the whole
record. A chart that carries no category string of its own (a Gantt, or one grouped by an
on-change-of field) leaves those typefaces as the only strings after the title, so
`strings[1]` was a font: the converter emitted `Fields!MS_Shell_Dlg.Value` and a
`ChartCategory_MS_Shell_Dlg` grouping, the engine answered `Field 'MS_Shell_Dlg' not found`,
and five reports in one family then collided on the duplicate grouping name.

A category now has to be a field the report actually has. Where nothing valid remains the
parser already returned `null` for the chart rather than inventing one, so no converter
change was needed.

*That check was too strict on its first attempt, and the main corpus caught it.* Crystal
stores a chart's group-by under its **display** name, which for a table-qualified field is
`"TableName ColumnName"` with a space — `Top3-Employee-Sales` stores `Employee Last Name`
for the column `Last Name`. Matching the qualified pair is not enough either, because
`TableName` is not always populated by the time a chart is parsed. The first version
therefore rejected a perfectly good category and silently dropped that report's pie chart,
which the before/after RDL comparison over the public corpus surfaced as one changed file.
The check now also accepts a candidate that ends with a known column name on a word
boundary. A typeface does not end in one of the report's own columns.

**`HasValue` is not a function this engine has** (2 files). Crystal's
`HasValue({?p})` asks whether a parameter was answered, and it reached the engine verbatim:
`Function HasValue is not known`, fatal, whole report lost. It is `IsNothing`'s negation, so
it cannot be a plain rename in `FunctionMap` — it is wrapped as `Not (IsNothing(x))` at the
call site, next to a note in the map so the two stay findable together.

**`DayOfWeek` is VB's `Weekday`** (1 file), which the engine has in `VBFunctions.cs`;
`DayOfWeek` it does not. A one-line map entry. `WeekdayName` was already fine — the failing
expression `WeekdayName(DayOfWeek(...))` only complained about the inner call.

*A note on process, because this bit twice in one round.* `dbg8` copies the parser and
converter DLLs at build time, so after any `git stash` / `git stash pop` around a
measurement it holds whichever build was current when **it** was last compiled — not
whichever is in the working tree. Verifying the chart regression gave the wrong answer once
for exactly this reason: a `--rdl` diff came back empty against a hash comparison that said
the file had changed, because the diff was two runs of the same stale build. Rebuild `dbg8`
explicitly after every stash operation, or the before/after is meaningless.

### An external corpus: rpt-rs fixtures, downloadable but never committed

`scripts/download-test-rpts.sh --with-rpt-rs` now fetches the report fixtures from
[MrSrsen/rpt-rs](https://github.com/MrSrsen/rpt-rs) — an independent Rust implementation
with the same goal as this project — into `tests/rpt-corpus-external/`. **114 reports**, in
five families: `parking/`, `synthetic/`, `typography/`, `worrall/` and `meridian/`. The
README links the project under "Other implementations".

Three deliberate choices:

- **Never committed.** `.gitignore` excludes the directory. They are that project's own
  authored test assets under MPL-2.0, not third-party samples, and this repository has no
  business redistributing them. The script downloads them on request, the way the main
  corpus already works.
- **A separate directory, not `tests/rpt-corpus/`.** Dropping 114 files into the main corpus
  would invalidate every count recorded in this file — "0 of 88 public", "0 of 2,324
  private" — and make every past round's numbers incomparable. Opt-in and separate keeps
  those meaningful.
- **One tarball, not 114 requests**, and its `benbrahim777/` directory is skipped: those are
  the same public files the main corpus already carries.

*A trap worth knowing about, because it caught this change.* The end of
`download-test-rpts.sh` sweeps `find "$REPO_ROOT/tests" -name "*.rpt"` and symlinks
everything it finds into the main corpus. A new directory anywhere under `tests/` is picked
up by it automatically: the first run of `--with-rpt-rs` silently added all 114 reports to
`tests/rpt-corpus/` as `__local__*` links, taking it from 88 files to 202 and the test count
from 899 to 1,811 - defeating the separate-directory decision entirely, and quietly. The
sweep now excludes `$EXTERNAL_DIR`. Anyone adding a third report directory under `tests/`
needs to exclude it there too, or check the corpus count afterwards.

**It earned its keep immediately: 12 of its 114 reports hit a fatal, where both existing
corpora sit at 0.** Three root causes, and the first is much the largest:

- **A chart's category field is being read as a font name** (7 files) — **fixed**, see
  below.
- **Two unimplemented formula functions** (3 files): `DayOfWeek` and `HasValue` — **fixed**,
  see below.
- **An array literal in an expression** (1 file):
  `Join([Fields!tier.Value, Fields!name.Value, ...], " - ")` — the transpiler does not
  handle `[` as a list constructor. Still open, and the only fatal left in that corpus; it
  needs grammar work rather than a mapping.

None of these is reachable from the existing corpora, which is the whole argument for having
this source available.

### Measured, not yet implemented: the drop shadow is the grouped report's biggest gap left

`SalesByCustomer-Grouped` is the outlier among fixture-backed reports at 42.3% where the
others are 51–61%, and unlike them it does not recover at a coarse cell: 63.0% at 32px
against 76–82% for the rest. So it holds real misplacement rather than glyph-width
difference, and mapping where its 8px disagreement concentrates finds the four worst bands
are all full-width solid rules around the report header.

They are the **drop shadow**, which is parsed (tag-237 `data[9]`) and deliberately not
emitted, because RDL has no expression for one. Measured off the reference render, its
geometry is no longer a guess:

```
box border      4px (1pt)   top y 61-64, bottom y 379-382, x 50-2428
shadow, bottom  y 383-393   x   65-2439      11px thick
shadow, right   x 2429-2439 y   76-393       11px thick
```

So it is a filled rectangle offset about 15px — 0.05in, 72 twips — down and right of the
box, visible only as the two strips beyond the box's bottom and right edges. The box
interior stays white even though the object sets no background, so Crystal is not simply
painting a rectangle behind a transparent object.

**The emulation that would fit RDL** is two filled rectangles rather than one offset one:
a bottom strip at `(L+D, T+H, W, D)` and a right strip at `(L+W, T+D, D, H)`. Two strips
rather than one rectangle specifically to avoid the z-order and transparency problem — a
single rectangle behind a transparent object would show through its interior and paint the
whole box black.

It is not implemented, and the reason is proportion rather than difficulty: 3 shadowed
objects in the public corpus and 42 in the private one, against inventing synthetic geometry
that the ink metric punishes hard when it lands slightly wrong (see the underline entry).
Worth doing when the grouped report is the last thing standing; the geometry above is the
head start. — *Implemented after all, once it was the last measurable thing left: see the
entry below.*

### An unanswered parameter no longer filters every row away

Three fixture-backed reports had been rendering blank pages since they were added:
`BeforeTV`, `Orders10k` and `Orders5-150`, at 3.2%, 1.6% and 0.8% against references
carrying 2.3–8.0% ink. Their fixtures were complete and correct; the rows arrived and were
filtered out.

All three select on a report parameter, which is how Crystal spells a range:

```
{Orders.Order Amount} = {?Order_Amt_Range}      (Orders10k, Orders5-150)
{Orders.Order Date}   = {?Date Range}           (BeforeTV)
```

That converts faithfully to a DataSet `Filter`, and then no parameter value exists at render
time — parameters are a render-time concern here, applied after conversion, so the converter
cannot know whether one will arrive. A bare comparison against nothing is false for every
row.

**An unanswered parameter now makes the filter inert instead of exclusive:**

```
=(IsNothing(Parameters!Order_Amt_Range.Value)
  OrElse ((Fields!Order_Amount.Value = Parameters!Order_Amt_Range.Value)))
```

**This is deliberately not "drop the filter", which is the option considered and rejected
when this was first written up.** Dropping it would silently un-filter reports for callers
who *do* supply a value — a report showing every row when it should show five is worse than
one showing none, because it is not obviously wrong. Both halves were verified against the
engine with a hand-written RDL before the converter was touched: with no parameter value all
three rows render, and with a value supplied exactly the one matching row does. If several
parameters are referenced, any one of them being unanswered skips the whole comparison,
because a comparison against nothing has no meaningful answer.

| Report | Before | After |
|---|---|---|
| `BeforeTV` | 3.2% | **57.4%** |
| `Orders10k` | 1.6% | **57.1%** |
| `Orders5-150` | 0.8% | **51.1%** |

That is the largest movement this suite has recorded, and those three were its largest
remaining gap. `SampleReport` moves 57.1% → 57.0%, which is noise; nothing else changes.

**Scale.** Only 5 of 88 public and 252 of 2,324 private reports emit different RDL — this
touches just the reports whose selection formula references a parameter, which is a narrow
slice, and it was worth 50 points to three of them. 0 parse or convert failures, 0 fatal in
both corpora, and all 12,263 private non-fatal occurrences identical per file per message.

*What is still true and still a judgement call:* a report whose author intended the
parameter to be answered now shows everything rather than nothing when it is not. That is
the deliberate trade, it is visible rather than silent, and supplying the parameter restores
the intended filtering exactly.

### Looking for more .rpt files to decode tag 191 with: what is actually out there

Tag 191 was left undecoded for want of samples — three records in two files, none in the
private corpus. A search for more turned up little that helps, and the reason is worth
recording so the next person does not repeat it.

- **The Crystal runtime on a dev box ships no sample reports.** `SAP BusinessObjects` under
  Program Files is the redistributable runtime only; the feature-demo reports come with the
  *designer*, which is not installed here.
- **The independent public repositories are nearly exhausted.** The three sources
  `scripts/download-test-rpts.sh` already uses account for almost everything findable.
  Beyond them there are perhaps six new files across two small ASP.NET demo repositories
  (`raselahmmedgit/crystal-report-sample`, `devistic-dotnet-projects/Crystal-Report-Sample`)
  plus two trivial extras in the `souvikduttachoudhury` repo already used — the rest of that
  repo's listing is build-output duplicates of the four files already in the corpus.
  Hello-world demo reports are the least likely place to find the Highlighting Expert.
- **`MrSrsen/rpt-rs` has about 200 fixtures and they were deliberately not taken.** It is a
  competing reverse-engineering project for the same format, MPL-2.0 and actively developed,
  and its `synthetic/`, `parking/`, `typography/` and `meridian/` families are its own
  authored test assets rather than third-party samples. Vendoring another project's test
  suite is a licensing and provenance question rather than a technical one, and it should be
  a deliberate decision by the repository owner, not a side effect of a decoding round. Its
  `benbrahim777/` fixtures are the same public files this corpus already has.

**The cheap unlock is the designer, not the internet.** Anyone with SAP Crystal Reports
installed can author a handful of one-field reports with a highlighting rule each — one per
comparison operator, a couple of colours — and that would settle tag 191's operator map and
colour byte order in one pass, with far better evidence than any found file. The same trick
would settle the drop shadow and the European-separator question. Until then those three
stay written up rather than implemented.

*Settled since, definitively.* The rpt-rs fixtures were fetched after all (as an external,
uncommitted corpus — see the entry below) and scanned: **0 of those 114 reports carry a
tag-191 record**, including a purpose-built `synthetic/` family that probes one feature per
file. Across 2,526 reports now available, tag 191 exists in three records in two files. The
internet does not have this one; only the designer will.

*(Since resolved without any new file. The unlock was not more samples but a report already
in the corpus: `USA-Orders-RWB-colored` renders 20 subtotals against its single threshold,
which settles the operator the search was meant to settle. The conclusion that the internet
has nothing more to offer here still stands — it was the wrong thing to be waiting for.)*

### Not implemented: tag-191 conditional formatting appears in 2 files out of 2,412

> **Since implemented — see "The Highlighting Expert, declined twice for want of a case
> that existed" above.** The record counts here are right, and a later survey that said
> otherwise was counting empty records. Two of the specific reasons given below are wrong.
> The colour byte order *does* line up with the border record's flag,B,G,R once the tail is
> split in the right place. And there is a second report whose render can check the operator:
> `USA-Orders-RWB-colored` renders 20 state subtotals against its single threshold, which is
> what settled the operator map. The rest of this entry is the head start it promised to be.

`SalesByCustomer-Grouped`'s subtotal renders red on grey in the real engine and plain black
on white here, and that was the last visible defect on the report. It is conditional
formatting — Crystal's Highlighting Expert — and it was traced far enough to be sure of what
it is before being dropped.

**What was established.** Rendering three pages of the report with the real engine settles
the semantics: page 1's `$53.90` is `#FF0000` on `#C0C0C0`, page 3's `$54,565.39` is plain
black on white with the same plain box. So the formatting is conditional on the value, not a
property of the object.

The records live at tag 191, *inside* a field object's wrapper — in the record sequence they
sit between the tag-159 that opens the group-subtotal object and the tag-160 that closes it,
so they are object-level and would be readable in `ParseFieldObject` beside the border and
numeric records. Each is a leading byte, two int32-length-prefixed decimal strings, and a
ten-byte tail:

```
lead=3  v1='1000.00'    v2='0.00'  tail=00000000 FF00C0C0C0 00
lead=5  v1='100000.00'  v2='0.00'  tail=00000080 0002FFFFFF 00
```

The leading byte is the comparison and the first string its threshold: `$53.90` is under
1,000 and gets the grey rule, `$54,565.39` is under 100,000 and gets neither, which is
consistent with 3 and 5 being two different operators. `C0C0C0` and `FFFFFF` are visible in
the tails and match the two rendered backgrounds.

**Why it stops there.** The whole corpus holds **three of these records, in two files** —
`SalesByCustomer-Grouped` and `USA-Orders-RWB-colored` — and **none at all in the 2,324
private reports**. Two operator values, two tails, one report whose render can check them.
Decoding the operator map, the colour byte order (the tail's does not line up with the
border record's `[flag][B][G][R]`), and how several rules chain would be guesswork dressed
as a specification, and it would be unfalsifiable: there is no second report to be wrong
about. The payoff is one fixture-backed report's page-1 score.

So it is written down rather than implemented. If a corpus file ever turns up with a richer
set of rules, this entry is the head start.

### The visual suite's one permanent failure was a data problem, not a layout one

`Top5USAsubCanada` page 2 had failed the visual suite continuously: "our render has 1
page(s); the real-Crystal reference has at least 2". It is not a layout defect and never
was.

That report has **no data fixture** — `FixtureBuilder` correctly refuses it, because a Top-N
report's detail rows are not in the data-only export. With no rows the report renders what
is static and stops, and a second page has nothing to be made of. It is out of reach twice
over: page 2 is a *subreport*, and `RuntimeOverrides.Data` would not reach it even given a
fixture — `ReportEngine` pushes only to the main report's own `DataSet1`, while a subreport
is loaded separately at render time and gets nothing.

The page-count check now skips instead of failing **when the report has no fixture**, naming
the reason. Where a fixture does exist a short render is still a real defect and still
fails, so the assertion keeps its teeth exactly where it can have them. `Assert.Ignore`
rather than `Assume.That`: an inconclusive result drops the case out of the run altogether,
where an ignored one stays visible in the count as a skip.

The suite is now **10 passed, 1 skipped, 0 failed** — green for the first time in this
run of work, with the skip and its reason on the record.

*The subreport data gap is worth its own line:* nothing can push data into a subreport
today. That blocks any visual case whose content is in one, and it is the thing to fix if
`Top5USAsubCanada` page 2 is ever to be measured.

### The currency symbol is a string in the numeric record, and the second record wins

`$53.90` had been rendering as `53.9` for as long as this project has existed. The
tag-249 → tag-248 numeric record's decimals byte was already known; the currency flag was
listed as undecoded. There is no flag — the symbol is a **string** in the record, and its
presence is the whole of it.

```
data[8]         decimal places (0-5 across both corpora)
data[9]         11 minus data[8] in all 162,082 private records - derived, not read
from data[17]   a run of [1-byte length][that many bytes, null-terminated] slots.
                Zero-length slots are padding; the non-empty ones are, in order, the
                thousands separator, the decimal separator, the currency symbol, and
                then the format's own name ("<Default Format>").
```

`data[9]` being exactly `11 - data[8]` in every one of 162,082 records is what establishes
`data[8]` as a decimal count rather than a format code.

**The second of an object's two records is the effective one, and this is the case that
proves it.** `Country-Region-Sort`'s Customer ID has `"$"` in its first record and an empty
symbol in its second, and the real engine renders a bare `158`. Reading the first would put
a currency symbol on every plain number in both corpora. The date extractor beside it takes
the *first* record, deliberately, so the two now differ and say why.

What the corpora actually contain:

| Slots | public | private |
|---|---|---|
| `th=','  dec='.'  cur='$'` | 1,251 | 103,079 |
| `th=','  dec='.'  cur=''` | 297 | 58,402 |
| `th='.'  dec=','  cur=''` | 1,715 | — |
| `th='.'  dec=','  cur='kr. '` | 1,198 | — |
| others (`kr `, ` kr.`, `%`, `£`, `Rs`, empty `th`) | 301 | 601 |

**Two things are deliberately left unformatted rather than formatted wrongly.**

- **European separators.** .NET substitutes the *rendering culture's* separators for `,` and
  `.` in a format string, so a report whose separators are `.` for thousands and `,` for
  decimals — 3,190 of the 4,762 public records, all Danish — cannot be honoured this way.
  Those are left alone. The en-US pair, which is 161,000 of the 162,000 private records, is
  formatted. *(Superseded: the report now names a culture that spells its separators, so the
  separator check is gone — see the Language entry below.)*
- **`%`.** It is a percentage rather than a currency, it belongs after the number, and
  Crystal treats it as its own feature. 36 records across both corpora; skipped.

A symbol stored with a leading space (`' kr.'`) is emitted as a suffix and any other as a
prefix, which is how the file itself carries the spacing. The symbol is quoted in the format
string so a letter symbol (`kr`, `Rs`) is a literal rather than a format specifier.

**The type gate matters more than the record.** Every field object carries a numeric record,
string ones included, holding whatever the object was last defaulted to — `Customer Name`'s
says two decimals. So the format is applied only where the report's own field list calls the
column numeric (`Int16`, `Int32`, `Float32`, `Float64`, `Currency`). A test covers a string
field getting nothing; it passes with or without this change and is a guard against a future
widening rather than evidence for this one.

**Scale.** 40 of 88 public and **1,664 of 2,324** private reports emit different RDL, with
0 parse or convert failures, 0 fatal in both corpora, and all 12,263 private non-fatal
occurrences identical per file per message. `SalesByCustomer-Grouped` 41.7% → **42.3%** —
both its amounts now read `$53.90`. It is the only fixture-backed report with a currency
field, so nothing else moves.

*Still open on that report:* the subtotal's grey fill and red figure. Its twelve border
records all carry `bg=FFFFFFFF`, so neither lives in the border record; the remaining
candidate is tag 191, which holds an ASCII number pattern and two colour words including
`C0C0C0` and `FF0000`. Undecoded, and now the last visible defect on the report.

### Object borders and backgrounds: tag 237, and the fore colour was channel-swapped

What a report draws as a box around its title, a rule under a column label, or a frame
around a subtotal is **border formatting on the object**, not a drawn line object. It lives
in a tag-237 record inside each object's wrapper, alongside the placement and font records,
wrapping a tag-236 child:

```
data[0..3]   edge style codes: left, right, top, bottom (0 none, 1 single, 2 double,
             3 dashed, 4 dotted)
data[9]      drop shadow flag
data[14..17] background colour: flag byte (0 = set, FF = none), then B, G, R
data[18..21] border width, big-endian twips (20 = 1pt, the default)
```

How each piece was pinned down, because most of it is empirical:

- **Bottom** is the two column labels on `SalesByCustomer-Grouped` whose only decoration is
  the rule under them (`edges=0001`).
- **Top** is `ConsolidatedBalanceSheet`: sixteen `edges=0010` objects, and the real
  engine's render shows the classic line *above* every total.
- **Left/right cannot be told apart by any corpus file** — every box sets both — so that
  half of the order is convention. A wrong guess mirrors verticals and nothing else.
- **The colour order is B,G,R**, proven by the real engine painting `007DA5BF` fills as
  `sc (0.749, 0.647, 0.49)` — tan — which is the B,G,R reading exactly.
- Style codes 2–4 follow Crystal's line-style list; only 1 is corpus-verified
  (public corpus styles: 1 ×456, 2 ×18, 4 ×2; private adds 3 ×157).

**The same check found the fore colour has been channel-swapped all along.**
`ExtractForeColor` read tag-256 as A,R,G,B; the file stores flag,B,G,R. On the greys and
blacks the fixture reports use, the two orders agree, so nothing caught it — but the real
engine renders `Top5USA`'s `00A5795A` as steel blue (0.353, 0.475, 0.647) and `00800000`
as navy, where the R,G,B reading gives brown and maroon. Every asymmetric text colour this
converter has ever emitted was wrong. Fixed in the same place the border record's
background colour was implemented.

**Scale.** 307 of 3,243 public-corpus objects carry at least one border edge, and the
private corpus has 18,965 bordered objects and roughly 4,300 with a background fill — the
largest untapped feature found in this project. 32 of 88 public and **1,276 of 2,324**
private reports emit different RDL; 0 parse or convert failures, 0 fatal, and all 12,263
private non-fatal occurrences identical per file per message.

`SalesByCustomer-Grouped` goes 34.0% → **41.7%**, its largest single move: the header box,
both label rules and the subtotal frame now render where Crystal has them. No other
fixture-backed report uses borders, and none of their numbers move at all — which is its
own regression evidence.

*Left undone, deliberately:*
- **The drop shadow** is parsed and stored but not emitted — RDL has no expression for it.
  Thickened bottom/right borders would fake it; not worth inventing yet.
- **The subtotal's grey fill and red figure on `SalesByCustomer-Grouped` still do not
  render.** Its twelve border records all carry `bg=FFFFFFFF`, so that grey lives somewhere
  else — tag 191. *(Two corrections since: tag 191 is not the number-format record, which is
  tag 249 and is now decoded; and tag 191 is conditional formatting, investigated and
  deliberately not implemented — see the entry above.)*
- **A border on a table cell spans the column, not the field.** The cell's textbox fills
  the cell, so the label rules run to the column edge where Crystal stops at the field's
  width. Same mechanism as the cell-padding entry; a real fix wants the border on a sized
  inner element.

### Engine: the point was TeX's, and every inch was 0.375% oversized (other repo)

`CustomerList` was the one fixture-backed report whose agreement did not recover at a
coarse cell, so it still had real misplacement. Mapping where its disagreement concentrates
found the worst 8px bands spaced exactly one row apart — the row boundaries — and fitting
both renders' row starts gave pitches of 64.4px (Crystal) against 64.9px (ours) for the
same `0.215in` row. The RDL asks for 15.48pt; our engine rendered 15.535pt.

15.535 = 0.215 × **72.27** — TeX's printer's point. `RSize.Points` in
`Majorsilence.Reporting` converted every inch (and cm and mm) to points with 72.27 to the
inch, where PDF user space, CSS lengths (which is what RDL sizes are) and GDI all define
1pt = 1/72in. Every dimension written in inches came out 0.375% oversized: a Letter page
had a MediaBox of **794×614** instead of 612×792, our rasterised pages came out 3308×2558px
against the reference's 3299×2550, and a page of table rows drifted by a pitch error that
put `CustomerList` half a cell adrift by its thirtieth row.

Fixed in the engine (`RSize.POINTSIZED/POINTSIZEM`, `Measurement.POINTSIZE_F/M`, and
`RdlPrint`'s private copy). Sizes given in `pt` round-trip through `RSize`'s normalized
form unchanged under either constant, so font sizes do not move; only in/cm/mm dimensions
do. The standalone tools — designer, viewer, reader, `RdlCmd`, the EAN-13 barcode — carry
their own copies of 72.27 and are left for a separate pass; they draw to screens, not PDFs,
and were not verified this round.

After the fix the MediaBox is exactly 612×792 and the row pitch is exactly the 15.48pt the
RDL asks for. A new engine-side test renders a Letter report and asserts the page is
612×792 within the writer's whole-point rounding; reverting the constants fails it at
614×794.

| Report | 8px before | 8px after | 24px before | 24px after |
|---|---|---|---|---|
| `CustomerList` | 57.6% | **60.9%** | 74.1% | 75.4% |
| `Country-Region-Sort` | 64.4% | 60.9% | 87.9% | 88.0% |
| `boyum__SampleReport` | 58.3% | 57.1% | 86.5% | 86.5% |

`CustomerList` — the report with the pitch drift — improves at every cell size. The two
8px dips on the others are quantisation, not movement: their 24px numbers are unchanged to
a tenth, and the render they measure is now on a correctly-sized page with exact row
pitch. The whole suite's rasters also now come out at the same pixel dimensions as the
references, so the comparison no longer resizes anything.

Engine suites all pass: ReportTests 261+293+293 across net48/net8/net10 (including the new
page-size test), Majorsilence.Pdf.Tests 279×2, RdlCreator.Tests 24×2, Tests 2×2. Crystal:
893 tests, 0/88 and 0/2,324 fatal, all 12,263 private non-fatal occurrences identical per
file per message.

### The remaining disagreement is glyph width, and the underline was not the problem

The previous entry lowered two baselines and blamed the underline stroke: 7px too low and
drawn the width of the textbox instead of the width of the text. Measuring it properly
rather than eyeballing the two images shows neither part is true.

Taking the ink bands around a heading on `boyum__SampleReport` — glyph rows and rule rows
separated by how solid each row is:

```
reference   glyph y 148-176  x 451- 618  w=168      ours   glyph y 153-184  x 453- 639  w=187
            RULE  y 179-181  x 450- 620  w=171             RULE  y 186-189  x 451- 641  w=191
```

The rule starts 3px below Crystal's glyph band and 2px below ours, and each rule is about
3px wider than the glyphs it underlines. **The underline is placed correctly relative to
the text it belongs to, in both.** What differs is the text: for the same string, in the
same nominal font, ours is 187px wide where Crystal's is 168px.

Across five strings on that report, measured the same way:

| String | Crystal | ours | ratio |
|---|---|---|---|
| `CardCode` | 168px | 187px | 1.11 |
| its rule | 171px | 191px | 1.12 |
| `CardName` | 178px | 198px | 1.11 |
| `Acme Associates` | 284px | 319px | 1.12 |
| `ADA Technologies` | 298px | 336px | 1.13 |

> **Answered, and it was our defect — see "Crystal's point size is a cell height, not an
> em" above.** The 88.5% computed below is 0.8951, which is Arial's
> upem/(usWinAscent+usWinDescent). Crystal is not condensing anything; it renders a smaller
> em because it treats the point size as a character cell height. The paragraph below is
> right that ours matches Arial's published *em* metrics — that was the wrong measurement to
> match. The 1.21 ratio noted for the italic title is not explained by this and is still
> open.

**Ours is the one matching the published metrics.** `CardName` in Arial is 4,834 units per
1,000 em, so at 10pt it advances 48.3pt — 201px at 300dpi, and we draw 198px of ink.
Crystal draws about 88.5% of that. Both PDFs embed Arial. Why Crystal condenses is not
established, and the 18pt italic title on `SalesByCustomer-Grouped` shows a different ratio
again (about 1.21), so this is not a single scale factor and should not be written up as
one.

*This is also why `Customer ID` wraps* (see the entry on free-form boxes): the string is
56.12pt by Arial's own metrics in a 56.16pt box, which we draw at full size and Crystal
draws smaller.

**How much of the remaining gap this accounts for.** Ink agreement recomputed at coarser
cells, which forgives fine-grained mismatch while still requiring content in the same
place:

| Report | 8px | 16px | 24px | 32px |
|---|---|---|---|---|
| `boyum__SampleReport` | 58.2% | 72.7% | **86.7%** | 84.0% |
| `Country-Region-Sort` | 64.2% | 78.0% | 87.9% | **90.5%** |
| `CustomerList` | 57.4% | 67.2% | 74.1% | 68.1% |

Two of the three reach the high eighties or better once the cell is wider than the glyph
difference. That says their content is in the right place and what is left is how the
glyphs are drawn. `CustomerList` does not, so it still has real misplacement in it and is
the better target of the three.

**What this means for the suite's numbers.** The 8px baselines cannot approach 100% while
the two renderers disagree on advance widths by a tenth, and nothing in this repository can
change that — it is not a layout fault to fix. Future rounds should read the baselines as a
regression guard and a relative measure, not as a percentage of correctness, and should be
sceptical of any plan whose goal is a specific high number.

*Not done, deliberately:* the metric's cell size was left at 8px. A coarser cell would
report friendlier numbers and forgive real defects along with the glyph difference, and
changing it would invalidate every recorded baseline at once. The table above is the
better way to ask that question when it matters.

### Italic and underline were read from bits the files never set

The font record's style field was decoded as `0x02` = italic and `0x04` = underline.
Neither bit is set anywhere: across the 88 public and 2,324 private reports the field only
ever holds `0`, `0x1`, `0x10000` or `0x10001`. Both attributes were dead code — no report
this converter has ever processed emitted an italic or an underline.

The bits the files actually use are **`0x00000001` = underline** and **`0x00010000` =
italic**. `0x10001` occurring on its own is what makes them independent flags rather than
one enumeration.

The evidence is a count match on four reports, against the real engine's own output:

| Report | Records with the bit | What the reference shows |
|---|---|---|
| `CustomerList` | 6 × `0x1` | its six column headings, underlined |
| `Country-Region-Sort` | 4 × `0x1` | its four column headings, underlined |
| `boyum__SampleReport` | 2 × `0x1` | `CardCode` and `CardName`, underlined |
| `SalesByCustomer-Grouped` | 1 × `0x10000`, no `0x1` | its one italic title, and rules that are drawn objects rather than an underlined font |

**Two baselines go down on this, and that needs saying plainly.** `CustomerList`
57.2% → 57.6%, but `Country-Region-Sort` 64.6% → 64.4% and `boyum__SampleReport`
59.2% → 58.3%.

Nothing moved to cause it: the before and after renders place every string at the same
coordinate and differ by 48 bytes of stroke.

> **The explanation first written here was wrong.** It said the stroke sat 7px low and ran
> the width of the textbox rather than the width of the text, and named an engine fix for
> it. Neither part survived measurement — see the entry above, which has the real cause.
> The stroke is drawn correctly relative to our own text; it is our text that differs.

Shipping this is still correct: a bit test that can never fire is a defect whatever the
score does, and the alternative is keeping italic and underline permanently dead to protect
a number. The baselines are recorded at the measured values.

**Scope.** 36 of 88 public and 648 of 2,324 private reports emit different RDL, 0 parse or
convert failures, 0 fatal in both corpora, and the private corpus's 12,263 non-fatal
occurrences identical per file per message.

### What is left on the grouped report is features, not layout

`SalesByCustomer-Grouped` sits at 34.7% while every other fixture-backed report is near
60%, so it was worth asking what is different. Comparing it against the reference, what is
missing is not misplaced content — it is content this pipeline cannot express at all:

- **Box and line objects are never parsed.** `LineObject` and `BoxObject` exist in the
  model, `RdlConverter` emits `<Line>` and `<Rectangle>` for them, and a converter test
  constructs them — but nothing in the parser ever creates one, so no real report has ever
  had a line or a box. The section object loop has a catch-all that skips any unrecognised
  odd tag in 159–200. This report's header rule, its header border and the two rules under
  its column labels are all invisible in our output, and they are a lot of ink.
  *(Half-wrong, it turned out: this report's rules and boxes are not line or box objects at
  all but border formatting on ordinary objects — see the border entry above. Its twelve
  placements were all accounted for by recognised types, which is what that diagnosis
  missed. Line and box objects remain unparsed, but no fixture-backed report contains one.)*
- **Object background colour is dead end to end.** `ObjectFormat.BackColor` is declared,
  never set by the parser, and never read by the converter — `WriteObjectStyle` has no
  `BackgroundColor` branch at all. The grey fill behind the group subtotal cannot be
  expressed.
- **The currency format is still undecoded** (`$53.90` renders as `53.9`), which is the
  existing tag-249 item.

One lead on the second and third: tag **191**, two records in this report, holds an ASCII
number pattern and two colour words. Record 0 reads `"1000.00"`, `"0.00"` and then bytes
that decode as `FF0000` and `C0C0C0` under the same "skip one, take three" convention
`ExtractForeColor` uses for tag-256 — which is exactly the red-on-grey the subtotal has.
That is a lead and not a decoding; the record's structure, and which object it attaches to,
are not established.

The useful conclusion is the negative one: the layout work on this report is done. Its
remaining gap is three unimplemented features, and no amount of positioning will move it.

### A detail cell was as wide as the column, not as wide as the field

A Textbox `Width` inside a TableCell is ignored — the textbox fills the cell — and this
converter makes each column as wide as the gap to the next column's start. That gap is
wider than the field Crystal drew whenever the report left a space between columns, so
right-aligned text ran to the column's right edge, which is the next column's left edge.

On `Country-Region-Sort` the customer id ended at x=128.00 and the customer name began at
x=127.98: no gap at all, rendering as `158Bicicletas Buenos Aires`. Crystal's field there
is 1,123 twips inside a 1,243-twip gap, so 120 twips — 6pt — of clearance was missing.

The cell now carries a `PaddingRight` of the column's width minus the field's, and only
where the column really is wider: a fallback column width is not a measurement of
anything, and padding by a made-up difference would move text that is currently right. The
id moves 5.98pt left, which is that 6pt, and the gap is back.

| Report | Before | After |
|---|---|---|
| `Country-Region-Sort` | 61.0% | **64.6%** |
| `CustomerList` | 55.1% | **57.2%** |
| `SalesByCustomer-Grouped` | 33.9% | **34.7%** |

`SampleReport` does not move: its two columns are already the width of their fields, so
there is no difference to pad. That is the rule working, not a miss.

**The probe that nearly said this was impossible.** The first test of whether the engine
honours padding added `PaddingRight` to the detail cells of a generated RDL and rendered
it: byte-identical output, which reads as "the engine ignores padding". It does not. The
probe pushed synthetic *string* values, and the engine left-aligns strings — there was
nothing for a right-hand padding to move. Re-run with `PaddingLeft`, the text moved by
exactly the 40pt applied. The engine right-aligns the numeric column of its own accord;
the RDL carries no `TextAlign` there at all.

**Scope.** 33 of 88 public and 647 of 2,324 private reports emit different RDL — only
reports whose columns are wider than their fields — with 0 parse or convert failures, 0
fatal in both corpora, and the private corpus's 12,263 non-fatal occurrences identical per
file per message.

*Deliberately not extended to the group header and footer rows.* Their cells are written
by the same helper and would take the same padding, but the object they measure against
comes from the group section rather than the detail row, and its bounds do not have to line
up with the detail columns. Padding those by a difference that may not mean anything is
the exact mistake the "only where the column really is wider" guard exists to avoid. Worth
doing once there is a fixture-backed case that measures it.

### A growing free-form box wraps its text, which moves it off its own Top

The previous entry stopped table cells growing and left free-form objects alone, reasoning
that a free-form object carries its own position so growing it moves nothing else. That
reasoning was wrong, and the column headings were the proof.

Reading the text placements straight out of our PDF for `Country-Region-Sort`, page one:

```
y=713.87  Customer          <- the column labels
y=703.89  ID
y=716.55  158  Bicicletas Buenos Aires   <- the first detail row
```

PDF y counts upward, so **the first detail row was rendering above the labels**. The cause
is on the second line: `Customer ID` wrapped onto two lines. A box that can grow wraps its
text and grows downward, and the wrapped block no longer begins at the object's declared
`Top` — the box has not moved but what is drawn in it has.

So the rule from the previous entry now covers free-form `TextObject`s and `FieldObject`s
too: `format?.CanGrow ?? false`. The labels move to y=727.68, above the first detail row at
716.55, which is the order Crystal has.

`Country-Region-Sort` 59.6% → **61.0%**, `SampleReport` 59.1% → **59.2%**. Modest next to
the previous entry's numbers, and the reason to ship it is the ordering rather than the
metric: a column heading printed under the first row of data is wrong in a way a percentage
understates.

**The residual, measured, because it is not what it looks like.** `Customer ID` still wraps.
Its box is 0.780in — 56.16pt — and the Arial 10pt advance widths of that string sum to
**56.12pt**. It fits Crystal by four hundredths of a point and does not fit this engine.
Both PDFs embed Arial, so it is not font substitution. Adding
`PaddingLeft/Right/Top/Bottom = 0pt` to every `Style` in the emitted RDL produces a
byte-identical render, so it is not the engine reserving padding either. What is left is
the engine's own text measurement, in the other repo, on a path every string in every
report goes through. Not worth touching for four hundredths of a point.

**Scope.** 88 of 88 public and 2,069 of 2,324 private reports emit different RDL, 0 parse
or convert failures, 0 fatal in either corpus, and the private corpus's 12,263 non-fatal
occurrences are identical per file per message.

*Now the most visible thing left, and it is fully specified* — fixed in the entry above.
A right-aligned detail cell's text runs to the column's right edge, which is the next
column's left edge, so a number abuts the following text with no gap at all. Measured on `Country-Region-Sort`: `158` ends
at x=128.00 and `Bicicletas Buenos Aires` starts at x=127.98. The cause is known — a
Textbox `Width` inside a TableCell is ignored, and this converter makes each column as wide
as the gap to the next one, so the cell is wider than the field Crystal drew. Crystal's
field here is 1,123 twips inside a 1,243-twip gap, so 120 twips (6pt) of clearance is
missing. A `PaddingRight` of column-width minus object-width on the cell's textbox is the
shape of the fix.

### Every table row grew, so the error was a pitch and not an offset

`CanGrow` was written as `true` on every table cell. That discards the row height the
converter derives from the detail objects' own bounds and lets the engine use whatever
line box it gives the font instead. In a table that is not a one-off displacement: it is a
**row pitch**, so it compounds down the page.

Measured on `Country-Region-Sort`, by taking the horizontal ink bands off both renders:
Crystal's rows are **46px** apart, ours were **53.5px**. By the thirteenth row that is a
third of an inch of drift, and it keeps going.

**Crystal's Can Grow is a per-object flag and it is off by default.** A detail field
occupies the height the report drew it at. So the converter now writes
`format?.CanGrow ?? false`.

*That reads as a constant today and is written this way on purpose.* `ObjectFormat` has
had a `CanGrow` property all along but nothing sets it — the flag is not decoded from the
.rpt yet — so it is false for everything. Writing the rule rather than the constant means
a report whose field really is set to grow starts working the moment the parser learns to
set it. Decoding that flag is now worth doing; it was not while every cell grew anyway.

| Report | Before | After |
|---|---|---|
| `Country-Region-Sort` | 29.8% | **59.6%** |
| `boyum__SampleReport` | 38.7% | **59.1%** |
| `CustomerList` | 36.7% | **55.1%** |

That is the largest movement this suite has recorded, roughly doubling all three. Nothing
regressed. `SalesByCustomer-Grouped` does not move, because what is wrong with it is
elsewhere, and the four cases without usable data cannot show anything either way.

**Free-form textboxes still grow, deliberately.** One carries its own position and height,
so growing it moves nothing else. Only the table case had a pitch to get wrong. — *This
turned out to be wrong; see the entry above. A grown box wraps its text, and the wrapped
block no longer starts at the Top the object declares, so growing one does move something.*

**No text is lost.** The worry with turning growth off is truncation. Checked on the
longest values in the corpus fixtures — `Bicicletas de Montaña La Paz` in a 2,554-twip
column — and they render in full. The mechanism is worth stating plainly: a 10pt line box
in this engine is about 12.85pt and the row is 11.02pt, so the text slightly exceeds its
row and is drawn anyway, because this engine does not clip at a container's edge (see the
entry on that below). Crystal packs the same 10pt text into the same 11pt row with tighter
leading. The outcome matches; the route to it differs, and it depends on that
non-clipping behaviour.

**Scope: all of it.** 88 of 88 public and 2,324 of 2,324 private reports emit different
RDL — every report with a table has cells, and every cell changes. 0 parse or convert
failures, 0 fatal in both corpora, and the private corpus's 12,263 non-fatal occurrences
are identical per file per message. Rendered bytes barely move (private 11,903,652 →
11,910,139) because a shorter row does not change how much text there is.

*A correction to the previous entry's closing note.* It said the remaining gap looked like
a uniform down-and-right offset of the whole content block, likely one origin or margin
calculation. That was wrong, and testable: searching every translation from -12 to +12
cells for the one that best aligns the two renders' ink gives a best shift of 1-2 cells
(0.02-0.05in) worth only 0.4 to 3.7 points of agreement. There was no offset to find. The
error was in the spacing between rows, which no single translation can fix.

*Still wrong in these same reports, and now the most visible thing left:* the column
headings sit on top of the first detail row rather than above it — fixed in the entry
above. And a right-aligned numeric column abuts the next column's text with no gap
(`158Bicicletas Buenos Aires`), which is the cell-padding item, now measured precisely at
the end of that entry.

### A page footer of special fields does not need the table, and pays for being in it

A `FieldObject` placed in RDL's own `<PageFooter>` cannot resolve: `Expression`'s final
pass finds a `fields` collection only by walking up to an enclosing data region, and a
page footer is never inside one. So any page footer holding a `FieldObject` — or a
`TextObject` with a `{reference}` in it — is routed into the details table's footer band,
where the scope exists.

**A Crystal special field needs no scope at all.** `{Page Number}` becomes
`=Globals!PageNumber`, `{Print Date}` becomes `=Format(Globals!ExecutionTime, "d")`;
neither touches the DataSet. Routing them anyway costs exactly the thing a page footer is
for: the table's footer band renders where the table ends, which on a short report is
directly beneath the last detail row, and once per report rather than once per page.
`boyum__SampleReport` printed its page number four inches above the bottom of the page.

So a `PageFooter` whose every reference is a special field now stays in `<PageFooter>`.
Three things keep that narrow:

- **Positive identification only.** A reference counts as scope-free when it is recognised
  as a special field and nothing else. A group name, a formula, a column, something the
  converter cannot place at all — anything else keeps the section in the table, which is
  where it already works.
- **The DataSet wins.** A name the report's own fields claim resolves to `Fields!`
  whatever the special-field list says, because the emitters check fields first. A column
  genuinely called `Print Date` is not mistaken for Crystal's.
- **`RowNumber()` is not scope-free.** It is in the same special-field table as the
  Globals, but it counts the rows of a data region, so `{Record Number}` still routes.

**Only `PageFooter` is exempted, deliberately.** The same routing carries ReportFooter,
GroupHeader and GroupFooter sections, and a ReportFooter in particular is picked back up
from the table's own footer band by joining its `TextObject`s — exempting one would drop
its content on the floor rather than move it. There is no better place for those to go;
`<PageFooter>` is a genuinely better place for this one.

**Measured.** Converting every corpus file before and after and comparing emitted RDL:
**33 of 88** public and **185 of 2,324** private reports change. Every fixture-backed
visual case improved at once, which no previous change in this suite has done:

| Report | Before | After |
|---|---|---|
| `boyum__SampleReport` | 38.4% | **38.7%** |
| `CustomerList` | 35.0% | **36.7%** |
| `SalesByCustomer-Grouped` | 32.4% | **33.9%** |
| `Country-Region-Sort` | 28.7% | **29.8%** |
| `BeforeTV` | 3.2% | **3.3%** |
| `Orders10k` | 1.6% | **1.7%** |
| `Canada-CrossTab` | 0.0% | **0.1%** |

**The PDF-byte control moved for the first time this session, and by a lot** — public
1,065,862 → 2,007,431 bytes, private 7,668,995 → 11,903,652. That is not a warning sign:
a page footer in `<PageFooter>` prints on every page, and in the table's footer band it
printed once. Reports that render many near-empty pages now carry a footer on each of
them. It is worth recording because that control has been flat through every other change
here, and a reader who sees it jump should know what did it.

Both corpora stay at 0 fatal, and the private corpus's 12,263 non-fatal occurrences are
identical per file per message.

### A band clamped to its table's left edge printed over the first column

The details table starts at its first data column, and a band written into it — a page
header, a field-bound page footer — carries absolute page positions that are re-based on
that edge. RDL has no negative `Left`, so anything further left than the first column was
clamped to zero and drawn at the table's own edge, on top of whatever heading sits there.

`boyum__SampleReport` shows it plainly. Its print date is at the page's left margin and
its only two columns start 1.33in in, so the date came out at 1.33in as well — printed
across the `CardCode` heading, both strings in the same place.

**The fix is to start the table at the leftmost thing in it, not at its first data
column,** with a leading spacer column carrying the difference. A column rather than an
indent on the band, because the data columns must stay where the report drew them:
moving the table left without one drags every detail row left with it. The spacer needs a
matching empty cell at the start of the detail row, the group header and footer rows, and
the queued-extras rows.

*This is the mirror of the trailing spacer that was built and discarded two entries
below, and it is worth being clear about why one ships and the other does not.* The
trailing one was for band content running past the table's right edge, which this engine
draws anyway because it does not clip — so it changed no pixel. This one is for content
the converter actively **moves**, and moved content renders in the wrong place under any
engine.

**Measured, and the first measurement was wrong.** Scanning the parsed model for reports
whose band objects sit left of their detail columns gives 58 of 73 public and 1,492 of
2,116 private. That is an upper bound, not the answer: it counts band objects wherever
they are, and some are not written into the details table at all. `CustomerList`'s
left-most objects are in the report header, which already gets a full-width table of its
own, so nothing of its is clamped and nothing of its changes. The honest measure is
converting every corpus file before and after and comparing the emitted RDL:

| | Reports whose RDL changes |
|---|---|
| public corpus | **30 of 88** |
| private corpus | **251 of 2,324** |

**Verified at the rendered-position level, not just the XML.** Rendering with data
fixtures pushed and comparing decompressed PDF content streams:

- `SampleReport` — print date `108.377` → `12.064` points and page number `150.509` →
  `54.197`, both 96.3pt, which is the 1.33in gap exactly. Ink agreement 37.1% → 38.4%.
- `SalesByCustomer-Grouped` — page-footer date `38.724` → `12.064` points, 26.7pt, which
  is its 0.37in gap exactly.
- `CustomerList` — 0 of 15 streams differ, as predicted.

The change is position-preserving for everything that was not clamped, which is the
property that matters most here. `SalesByCustomer-Grouped` has a footer object whose
emitted offset goes `5.578in` → `5.948in` while its table moves 0.369in left: same
absolute position, and the two cancel to the byte.

*A note on how nearly this was mis-verified.* Comparing page 1 of all ten
visual-regression renders before and after showed exactly one report changing. That reads
as "narrow effect" and it is wrong: `SalesByCustomer-Grouped` paginates per group and its
page footer is not on page 1. The content-stream comparison across all pages caught what
the page-1 pixel check could not. Same lesson as the entry below — a render comparison is
only worth what it actually covers.

*Still wrong in the same report, and visible in its diff:* `SampleReport`'s page number
prints immediately below the last detail row instead of at the foot of the page — fixed
in the entry above.

### Not shipped: this engine does not clip at a container's edge

**A whole change was built, measured, and discarded.** Last round left 31 public-corpus
reports whose header band holds something wider than the table containing it, and said
widening the table was next. It was written - a spacer column at the right end, sized to
reach the page's usable width, with a matching empty cell added to the detail row, the
group header and footer rows, and the queued-extras rows. It took the count from 31 to 0.

It changes no rendered pixel, on any report, and it is not in the tree.

**How that was established.** All 88 public-corpus reports were rendered to PDF with and
without the change and their content streams compared after decompression, ignoring the
trailer's random `/ID`: **0 of 88 differed**. That result on its own is weak, for a reason
worth writing down separately below, so the mechanism was tested directly with a
hand-written RDL - a 2in-wide `Rectangle` containing a 6in-wide right-aligned `Textbox`,
beside an identical `Textbox` sitting free in the `Body`. Both draw their text at
`302.415` points, the same position to three decimals, ending 4.25in past the rectangle's
right edge. The engine lays a report item out against its own width and draws it wherever
that puts it; a container's right edge is not a boundary.

So "band content wider than its table" is a real inconsistency in the RDL this converter
emits, and it is not a defect in what this engine renders. It would matter to a consumer
that does clip - SSRS does - which is an argument for fixing it one day, but not one this
project can verify, and shipping a structural change to five row writers on an unverifiable
hypothesis is worse than leaving it alone. The two entries below have been annotated where
they assumed otherwise; the count itself should stop being tracked as a defect count.

**The corpus PDF-byte control is weaker than it has been described as.** Every round in
this project has reported "PDF bytes unchanged" or "byte-identical" across the corpora as
evidence a change was safe. Measured properly: of the 88 public-corpus reports, **64 render
no text at all**, and the median count of PDF text-showing operators per report is **zero**.
These reports have no saved data and no reachable database, so most of them render an empty
page, and two renders of an empty page agree no matter what the layout code does.

That control is still worth keeping - it exercises parse, convert and render over 2,412
files and catches crashes, fatals and severity changes, which is what it has actually been
catching. It is not evidence about fidelity. The only fidelity signal in the project is the
visual-regression suite, and only its three fixture-backed reports have ink to compare.
Extending fixture coverage is therefore worth more than it looked like it was.

### A table band is not at the page's left edge

**Implemented, and half of the over-wide-header problem below is now gone.** A header,
footer or group band belongs to its table, and a table starts where its first detail
column starts - 0.37in in on the grouped reference report, 0.08in on `CustomerList`. The
objects in those bands carry absolute page positions, and `WriteFreeFormObjects` wrote
them out unchanged, so everything in a band came out that offset too far right and
anything that reached the page's edge extended past the end of the table. (The position
half of that is the whole of it: the "past the end of the table" half turned out not to
matter to this engine at all - see the section above.)

The container's own left edge is now passed down to `WriteObjectPosition` and subtracted.
It is zero everywhere else - the Body, RDL's own `PageHeader`/`PageFooter`, and the
full-width report-header table added last round - so only table bands move.

**Clamped at zero**, because RDL has no negative `Left`. An object drawn left of the
table's first column cannot be expressed inside that table at all; the table's own edge is
the closest place there is, which is exactly where it lands today, so the clamp cannot
make anything worse than it already is.

**Result, measured over the public corpus.** Reports whose header band holds something
wider than the table containing it: **47 → 31**. Sixteen were nothing but this offset.
The remaining 31 are genuinely wider than their table. Widening the table was tried next
and thrown away: the engine does not clip at a container's edge, so it changed nothing.
See the section above.

`CustomerList` **34.8% → 35.0%**: its labels overhung by 0.08in, so there was only 0.08in
to win. `SalesByCustomer-Grouped` does not move at all, and that is the expected answer
rather than a disappointing one - its report header already has its own table at the
page's left edge, and its column labels are in the group header, which is laid out in
cells rather than free-form.

**No behaviour change in the wider corpus**, and this was checked rather than assumed: the
per-message, per-file breakdown of all 12,263 non-fatal occurrences is byte-identical to
last round's, and total PDF bytes are unchanged in both corpora.

Verified: **887 crystal tests**, **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize
still 4, visual regression 5/6 with the same pre-existing missing-page failure. The new
test fails when the subtraction is reverted.

### Engine: an image was drawn a third too large, in the other repo

**Implemented in `Majorsilence.Reporting`.** Every picture in every PDF this engine
produces has been coming out `DpiX/72` too large - half again at the usual 96 dpi -
anchored at the correct top-left corner.

`RenderBase.ProcessPage` built the image's rectangle with its x and y in points and its
width and height converted to pixels:

```
new RectangleF(i.X + PaddingLeft, i.Y + PaddingTop,
               PixelsX(i.W, pgs) - ..., PixelsY(i.H, pgs) - ...)
```

Two units in one rectangle, with the padding - points - subtracted from the pixel half.
The PDF writer works in points, so the size was inflated by the ratio while the position
stayed right. Everything else in that file converts *to* points, including the neighbouring
Clip branch, so the pixel conversion was the anomaly rather than the convention.

**How it was found, because the first diagnosis was wrong.** From the render it looked like
`FitProportional` was picking the wrong branch, and the entry above says so. It was not:
the bitmap is 223x86 and the box 2.323 x 0.896in, the same aspect to four figures, so
`FitProportional` has nothing to correct and the picture should come out at exactly the box
size. What settled it was measuring instead of looking - the blue square in the logo is
21.08% of the bitmap's width, it is 0.490in in Crystal's render (implying a 2.324in
picture, the box) and 0.638in in ours (implying 3.03in). 223 pixels emitted as points is
3.097in. That number is what named the bug.

**The engine's own tests caught a real regression from the first fix.** A DataMatrix
barcode stopped decoding. `CustomReportItem` builds its image with `Clip` sizing and an
image *generator*, and that branch passes the rectangle's width to `GetImageData` - which
is not a layout size at all but a sample count, asking for a bitmap that many pixels
across. Converting the rectangle to points quietly asked for three quarters as many
samples, and a dense symbology stops being readable. That call now converts back to pixels
explicitly, with a comment saying why, since it reads like an inconsistency otherwise.

**Result.** `SalesByCustomer-Grouped` **20.6% → 32.4%** and `CustomerList` **32.4% →
34.8%**. Both reports carry the same logo, and in both it now measures within a few
percent of Crystal's - 144x106 pixels against 147x113, which is antialiasing.

A new engine test renders a 40x20 image into a 2in x 1in box - the same aspect, so
`FitProportional` has nothing to do - and asserts the picture is placed 144 x 72 points.
Nothing had been asserting a rendered image's size before, which is why a bug this size sat
in a PDF renderer without anyone noticing: a too-large image still renders, and still
decodes.

Verified: **292 engine tests** on net8/net10 and 260 on net48, **886 crystal tests**,
**0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize still 4, non-fatal steady at
12,263 with an identical breakdown.

### A report header needs a data region, but not the details table's

**Implemented.** A report header holding a field reference cannot sit free-form in the
Body - a `Fields!` expression resolves only inside a DataRegion - so it was routed into the
details table's Header band. That gives it a scope and takes away its width: a header band
is only as wide as its table, and the table starts at its first column, so a page-spanning
title comes out pushed right and running off the end of a narrower table.

It now gets a one-column table of its own, full body width, at the top of the Body, above
the details table. That is the same shape `WriteHeaderOnlyTable` already built for reports
with no details at all, so the fix is mostly a matter of letting a second caller use it -
with a name of its own, since both hosts were called `Table1`, and without the report
footer, which the details table below owns.

**The placeholder row had to be hidden.** That table's Details band exists only because
RDL demands at least one row, and a Details row is emitted once per row of the DataSet. On
the grouped reference report - 2,191 rows - an unhidden 1pt placeholder would have laid
down 2,191 blank rows and pushed the real table thirty inches down the page. Hiding it
collapses it, and this fixes the same latent problem in the original caller, where it has
been quietly emitting a blank row per data row all along.

**Result.** `SalesByCustomer-Grouped` **3.7% → 20.6%**, its largest jump by far, and the
title and logo now start at the top left corner where Crystal has them. Header content
wider than the table containing it drops from **65 of 88 public-corpus reports to 47**;
the remainder are page headers, which stay in the details table because their content is
column-aligned with the details by nature, and are a smaller overhang. `CustomerList` is
unchanged at 32.4% - its report header is a plain text object, so it was never routed.

**Still wrong on that report**, in order: the logo renders larger than the box it is
given, running over the start of the title, which reads "es By Customer" as a result
(**fixed - see the section above, and the diagnosis in it is not the one written here**);
the amount
and date still touch, because a textbox inside a table cell stretches to the column and its
own width is ignored, so the gap Crystal leaves has to come from padding; and the amount
reads `53.9` rather than `$53.90`, which is the undecoded currency flag.

Verified: **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize still 4, non-fatal
steady at 12,263 with an identical breakdown, PDF bytes byte-identical, **886 crystal
tests** green, visual regression 5/6 with the same pre-existing missing-page failure. Both
new tests fail when their halves are reverted.

### A group header's labels were written twice, and a header band is not as wide as the page

**Implemented: the duplicate.** The group-header row places each label in the column it
sits over. The objects it had no cell for are queued and emitted as extra rows underneath,
and a label that *was* placed stayed in that queue, so every label came out twice - once in
its own column, then again a row lower and a column to the left. Which column decides
which label is now settled before the queue is built rather than inside the cell loop, so a
placed label is not a leftover. The grouped reference report went **3.6% → 3.7%** and its
"Order Amount"/"Date" captions appear once.

**Found, and now fixed: the header band inherits the table's width and offset.** Kept
below as it was written, because the measurement is what made the fix obvious; see the
section directly above for what was done about it.

A report header routed into the details table - which happens whenever it holds a field
reference needing a data scope - is confined to that table. The table starts at its first
column, so the header is pushed right by that offset, and the table is only as wide as its
columns, so anything wider than that runs past the end. On the grouped reference report the
title is a 7.9in field object inside a table 5.8in wide that starts 0.37in in: the title
and logo land visibly right of where Crystal puts them.

It is worth measuring rather than guessing at, so: **65 of the 88 public-corpus reports**
emit header-band content wider than the table containing it, the worst overhanging by
**3.95 inches**. Routing page headers into the table last round made this more common -
before it, only a field-bound header went there and the rest sat in RDL's full-width
PageHeader. That routing was right on ordering and is not to be reverted; the width is a
separate thing to fix on top of it.

The likely fix is to stop putting the *report* header in the details table at all and give
it its own full-width one-column table above it - the same shape `WriteHeaderOnlyTable`
already builds for reports with no details - which keeps the data scope that forced the
routing while restoring the page width. Page headers can stay where they are: their content
is column-aligned with the details by nature.

**One behaviour change in the wider corpus.** Non-fatal occurrences 12,259 → 12,263. All
four are in one report, `taxcert.rpt`, whose subreport now renders once more because
removing the duplicated row changed what fits on the scanned page - the same
"unable to connect to datasource" warnings every dataless render produces. Same shape as
last round's, and equally benign.

Verified: **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize still 4, PDF bytes
byte-identical, **884 crystal tests** green, visual regression 5/6 with the same
pre-existing missing-page failure. The new test fails when the fix is reverted.

### Dates carry their own format, and the visual suite could not have measured it

**Implemented for dates.** Numbers are not done, and the useful part of this entry is
what was decoded on the way.

**Where a field's formatting lives.** Every field object carries a family of format
records - 237, 239, 241, 243, 245, 247, 249, 251 - each wrapping a single child. Two of
them matter here:

* **tag-243 → tag-242, the date format.** Exactly one per field object, in every file of
  both corpora. `data[0]` is the date order and `data[17]` the separator character.
  `data[4]` (numeric month) and `data[6]` (four-digit year) are the same value in every
  explicitly-ordered record in either corpus, so nothing else needs reading yet.
* **tag-249 → tag-248, the numeric format.** Exactly *two* per field object, always -
  76,411 records across 38,000-odd objects and never any other count. The second is the
  effective one. `data[8]` is the decimal places and `data[9]` the rounding; `data[9]`
  equals `11 - data[8]` in 76,390 of 76,411 cases, and the 21 that differ are what proves
  they are two independent settings rather than one written twice. Separators and the
  currency symbol sit in the same record as short length-prefixed strings, next to the
  literal format name `<Default Format>`.

**Date order, and the value that is not a format.** `data[0]` takes three values: 0
(year-month-day), 2 (month-day-year), and 1, which is by far the most common and means
"use whatever short date this machine has". That last one is confirmed rather than
guessed: this machine's short date is `yyyy-MM-dd`, and a report whose date fields carry
order 1 renders `2000-12-09` through the real engine, while a report carrying order 2
renders `05/26/2001` on the same machine in the same minute. So order 1 must emit *no*
format at all - the renderer already does what it asks - and writing one would bake this
machine's locale into the converted report.

**A .NET trap worth knowing.** The first working version emitted `MM/dd/yyyy` and rendered
`05-26-2001`. A bare `/` in a .NET format string is not a slash; it is "this machine's date
separator". Crystal means the character it stored - it renders slashes here, where the
machine's separator is a dash - so the separator is emitted quoted.

**The suite could not have measured any of this.** The fixture loader typed every CSV
column as string, with a comment explaining that the engine converts from the RDL field
types anyway. That is true of arithmetic and false of formatting: a textbox `Format` over
a value that arrived as the string `2001-05-26` does nothing whatsoever. The first correct
version of this change therefore showed no effect at all, and the temptation was to
conclude the format was not reaching the cell - it was, and the RDL proved it. Columns are
now typed from their contents, with dates parsed by exact pattern rather than `TryParse`,
because `TryParse` is happy to read a column of years as a column of midnights.

**Still wrong: the currency symbol.** `Order Amount` renders `$53.90` in Crystal and
`53.9` here. It is not the column type - the file calls that column a plain number - and
it is not simply the presence of the currency-symbol string in the numeric format record,
because fields that render without a symbol carry one too. Two candidate flags were tested
against five fields of known ground truth and both were contradicted by a sixth. Not
shipped: a wrong number is no better than an unformatted one, and the decimal places alone
would only turn `53.9` into `53.90`. The decoded offsets above are the place to start.

**Result.** The grouped report's date renders `05/26/2001`, matching the reference
exactly. Ink agreement does not move - a date is a handful of glyphs on a page whose
disagreement is dominated by everything else - which is worth recording as a limit of the
measure rather than of the change: the render itself is verifiably right.

**One behaviour change in the wider corpus.** Non-fatal occurrences went 12,255 → 12,259.
All four are in one report, `ApprovalJEDetail.rpt`, whose subreport is now rendered twice
instead of once, and they are the same "unable to connect to datasource" warnings every
dataless render produces. Deterministic across repeat runs, and not caused by the
style-copy fix that landed alongside - that was tested separately and made no difference.

**Fixed in passing.** The detail cell rebuilt its style whenever a bold override applied,
naming the fields it copied - and so silently dropped the foreground colour and the
alignment. It now copies them, along with the format.

Verified: **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize still 4, PDF bytes
byte-identical, **883 crystal tests** green, visual regression 5/6 with the same
pre-existing missing-page failure. All three new tests fail when their parts are reverted.

### Crystal's page header goes below the report header, which RDL cannot express

**Implemented.** Crystal prints the Report Header once at the top of page one and the
Page Header *below* it, then the Page Header alone at the top of every page after that.
RDL's `PageHeader` only does the second half: it is pinned to the very top of every page,
page one included. Mapping one onto the other put `CustomerList`'s column labels above its
title and logo and left the whole header block about half an inch out of place.

**The fix.** A Page Header now goes into the details table's own `Header` band, which sits
below whatever the Body draws above the table - the report header - and carries
`RepeatOnNewPage` to give back the every-page half. The machinery already existed for page
headers that hold a field reference and need a data scope; this drops that condition, so
the list is no longer named for it.

**Ordering.** Section order in the file is not render order - the page header is stored
first - so the header rows are assembled explicitly: a routed Report Header, then the Page
Header, then group headers in the no-table case.

**An empty band is fatal.** The first version of this lost a whole report to a blank page.
The grouped reference report has a Page Header section with nothing in it, and routing one
emits a Rectangle wrapping an empty `ReportItems`, which the engine rates Severity 8.
Empty bands are now skipped, and there is a test that fails if any empty `ReportItems`
reaches the output.

**What this costs.** A report whose data set comes back with no rows now loses its page
header, where Crystal would still print it. That is inherent to putting the content inside
a data region and is the trade every converter of this shape makes; production reports
have rows.

**It also blinded the corpus scan's positive control.** Rendered PDF bytes fell from
43.2 MB to 7.7 MB across the larger corpus and from 1.33 MB to 1.07 MB across the public
one. Nothing was lost: the same report emits the same 97 textboxes before and after, they
have simply moved inside the table. The scan renders *without data*, so a table with no
rows renders neither its rows nor its header band, and page-header content that used to
print on every page of every report now prints nowhere. Fatal counts and the non-fatal
breakdown are unaffected and still mean what they meant; the byte total no longer works as
a "something actually rendered" check.

**And it hid, rather than fixed, an open item.** Non-fatal occurrences went 12,299 →
12,255, and the 44 that vanished are exactly the `IIf(..., Nothing)` `IConvertible`
exceptions logged elsewhere in this file. They are not fixed - the expression is still in
the RDL, one occurrence *more* than before - they are merely no longer evaluated in a
data-less render. Expect them back the moment those reports get rows.

**Result.** `CustomerList` **28.4% → 32.4%**, and the page now has the reference's
arrangement: logo and title at the top, column labels beneath them, detail rows beneath
those. The other five are unchanged.

**Still wrong on that report**, smallest remaining things first: the ID column's values
right-align against the next column's text with no gap, because a textbox inside a table
cell stretches to the column and its own width is ignored, so the gap Crystal leaves has
nowhere to come from - padding is the likely answer; the "Customer ID" label wraps to two
lines because our font renders it wider than Crystal's does in the same box; and the header
band is shorter than Crystal's, so the labels touch the first detail row.

Verified: **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, oversize still 4, **880 crystal
tests** green, visual regression 5/6 with the same pre-existing missing-page failure. Both
new tests fail when their halves of the change are reverted.

### A text object's alignment is on its paragraph, not on the object

**Implemented.** The parser read alignment from the object-level record only, which is
blank for most text objects, so nearly everything rendered flush left.

**Where it lives.** Every paragraph inside a text object opens with a tag-192 record,
always exactly 23 bytes, and `data[12]` is that paragraph's horizontal alignment. It uses
the same codes as the object-level record - 1 left, 2 centre, 3 right, 4 justify - and it
is the one that is actually filled in: across 55,270 paragraph records the byte is only
ever 1, 2, 3 or 4, while the object-level record reads 0, unset, for four fifths of the
text objects in the public corpus. Reading only the object record meant `CustomerList`
rendered every one of its nine text objects left-aligned when three are not.

**Which wins when both are set.** They agree in 119 of 123 public-corpus cases. In the
four that disagree the object says right and the paragraph says left, and the real engine
renders them left, flush with their left-aligned neighbours. So the paragraph wins
outright rather than merely filling in when the object is unset. Four samples is thin,
but it is four samples of ground truth against nothing on the other side.

**Justify.** Crystal has a fourth alignment that RDL does not - the schema's TextAlign is
General/Left/Center/Right. It is 1,800 of the 55,270 paragraphs, none of them in the
public corpus. The converter writes `Justified`, which the target engine knows; a
consumer that does not know it falls back to its own default, which is where those
paragraphs would have landed anyway.

**A limit worth recording.** 245 text objects in the larger corpus hold several
paragraphs whose alignments differ from each other. The model has one alignment per text
object, so those keep the first paragraph's. Fixing it properly means the model growing a
notion of a paragraph, which nothing else needs yet.

**Result, and a metric that lies about it.** `CustomerList` went **28.9% → 28.4%**. That
is not a regression, and it is worth spelling out why the number moved the wrong way.
Left-aligned, this report's title printed on top of the logo - those cells were inked in
both images regardless, so being wrong there was free. Centred, which is within seventy
pixels of where Crystal puts it, the title moves off the logo and inks cells of its own,
at a height where the reference has no title, because our whole header block still sits
about four tenths of an inch too low. The ink metric is charging the alignment fix for a
different bug that was already there. The renders side by side settle it: before, the
title sits at the far left across the logo; after, it sits where Crystal's does.

Second time in three rounds that a correct change has measured worse because a second
error was standing next to it. The margin case was the same shape and got reverted for it.

**Next**, and the reason the number could not go up: Crystal prints the Report Header
*above* the Page Header on page one, and we print the page header first because that is
what RDL's PageHeader means. (Done - see the section above.) It is measurable rather than inferred - the reference's
column labels start about 1,785 twips into the body, which is the report header's own
height of 1,290 plus the labels' 495-twip offset within their section, and the logo's top
edge lands exactly on the 240-twip margin. Everything in `CustomerList`'s header block is
therefore about half an inch out. Fixing it means not mapping Crystal's page header to
RDL's PageHeader at all, which is a larger change than it sounds.

Verified: **0 of 2,324 and 0 of 88 fatal**, 0 exceptions, non-fatal steady at 12,299
occurrences, oversize still 4, **878 crystal tests** green, visual regression 5/6 with the
same pre-existing missing-page failure.

### A table's columns are as wide as the gaps between them

**Implemented.** Two changes that are each worthless alone and large together,
which is why the second one was previously measured and rejected.

**Columns span to the next column, not to the object's edge.** Crystal leaves
gaps between columns. An RDL table's columns are contiguous, so a column has to
be as wide as the distance to the next one; taking each object's own width
silently closes every gap and drags everything to its right leftwards,
cumulatively. On the grouped reference report that is a 968-twip gap before the
last column, plus 532 twips of table indent that was being discarded - about an
inch of drift by the third column. The table also now starts where its first
column does instead of at the body's left edge.

This corrects a misreading recorded in the section below. The grouped report's
amount and date looked like they were sharing a cell and printing concatenated.
They were not: they were in the correct cells, and the cells were in the wrong
places, close enough together that right-aligned and left-aligned neighbours
touched. Worth remembering that a rendered page shows symptoms, not causes.

**Margins are a sixth of an inch, not a half.** The page-setup record's margin
block is identical in every file of both corpora - a "use the printer's defaults"
sentinel - so one default has to serve, and the real engine renders these reports
into a twelve-point inset. That was measured a round earlier and *rejected*,
because on its own it made the only measurable case worse. It made it worse
because the columns were still compressed: a narrower body was the only reason
the compressed table fitted at all. Fix the columns and the same margin change is
worth twelve points of ink agreement.

The pair of them is the lesson: two errors that partly cancel will defend each
other against being fixed one at a time, and a measurement that says "this made
it worse" is not always a verdict on the change being measured.

**Result.** `CustomerList` **16.2% → 28.9%**, its largest single jump so far, and
its columns now sit across the page where Crystal puts them.
`SalesByCustomer-Grouped` 2.6% → 3.6%. `Top5USAsubCanada` 0.0% → **2.9%** without
a data fixture at all - its static header and footer now land correctly, which is
the first evidence that the three fixture-less reports are not permanently stuck
at zero after all.

**Tried and reverted:** giving each detail cell's textbox the width of the object
inside it, so right-aligned text would sit at the object's edge rather than the
wider column's. The engine stretches cell contents to the column and ignores the
width, so it changed no pixel; it is not in the diff, but it is not worth trying
again either.

**Next**, from looking at the two renders side by side: text objects carry an
alignment that is not being read. `CustomerList`'s title is a full-width text
object that Crystal centres and we render flush left, and its "Customer ID"
column heading is right-aligned in Crystal and left in ours - yet the file's
object-level alignment reads Left for every object in that report, while the
grouped report emits three alignments correctly from the same code path. So the
alignment that matters for these is probably held per paragraph inside the text
object rather than on the object, and is simply not being looked for. After that,
number and date formatting, which is the last obviously wrong thing on the
grouped report.

Verified: **0 of 2,324 and 0 of 88 fatal**, non-fatal steady at 12,299
occurrences, oversize still 4 despite every body getting wider, **876 crystal
tests** green, visual regression 5/6 with the same pre-existing missing-page
failure.

### A second measurable visual case, and what it exposed

**Implemented.** The visual suite had one report able to detect a layout
regression, because the other five render no rows. That is now two, and getting
there turned up four defects.

**Where fixtures come from now.** Crystal's CSV export writes *rendered* rows -
every line carries the whole report line, headers and labels and footers - so the
detail columns are only recoverable from a plain list, which is why the first
fixture was the only one. The data-only Excel export writes a cell grid instead,
which survives grouping. It arrives as BIFF8 in a compound file, so the work is
split across the fence that already exists: the net48 tool that needs the real
Crystal runtime writes the export (`ReferenceRenderer --xls`), and a new net10
tool that needs our own parser turns it into a fixture
(`tools/Majorsilence.Crystal.FixtureBuilder`), reusing the compound-file reader
the .rpt parser already has.

Detail rows are picked out by shape rather than by content: a row is a detail row
when it has a value for every field and its values are the right *kinds* - which
is what separates it from the label row above it, same width but all text where
the detail row has numbers. That needs no knowledge of what the labels say, so a
label that reads like a field name cannot fool it. On the grouped report it
separates 2,191 detail rows from 268 label rows cleanly.

**What it still cannot do.** The export contains what a report *displays*. Three
of the four remaining reference reports show only group summaries or a cross-tab,
and their underlying rows are in no export at all - what comes out is the summary
or the pivot. Those need the rows saved inside the .rpt, which is still blocked on
the saved-data stream. So the suite goes to two measurable cases, not five.

**A caution worth recording.** The first fixture built this way looked right and
was wrong: 394 of its 2,191 rows had a blank customer name, and it was blank in a
contiguous block near the end. Shared strings in BIFF8 spill into continuation
records, and a string cut in half by that boundary *restarts with its own
compression flag* - one byte per character on one side, two on the other.
Concatenating the records and reading straight through goes out of step at the
first split string and stays that way, which silently blanks text cells from
there on while numeric cells stay perfect. It was found only because the render
put unfamiliar rows on page one; the fixture itself looked plausible. The reader
now walks the records as one stream and re-reads the flag at each boundary, and
an out-of-range string index is a hard error rather than an empty string, so the
same mistake cannot be silent twice.

**Engine: pushed data and calculated fields.** Pushing a DataTable into a DataSet
that declares a calculated field threw from inside `DataColumnCollection` - a
calculated field has a Value expression and no DataField, and the pushed-data path
looked every field up by name regardless. The query path in the same file has
always skipped calculated fields and tolerated a column it cannot find; the pushed
path now does the same. Fixed in the engine, with a test there. This is why the
grouped report could not be given data at all.

**Converter: the invented header row.** The details table emitted a bold row of
the DataSet's column names as its header. Crystal has no such row - the labels
above a column are ordinary text objects in the page or group header, and they
already render from there. So the table duplicated the labels on any report that
has them, invented a row of raw column names on any report that does not, and
pushed itself down the page either way. Removed.

**Converter: group header cells by position.** A Crystal group header commonly
holds the group's own field at the left and column labels further across. Cells
were filled in declaration order, so the first label took the first cell and the
group field was dropped entirely: every group rendered captioned with the wrong
words and nameless. Objects are now assigned to the column they sit over, using
the detail objects' own positions - which is only possible because those
positions are read at all now, and is the first real payoff from that. Where the
detail objects do not run left to right the list is left empty and the old
declaration order applies, so no report is worse off.

**Result.** `CustomerList` 15.0% → 16.2%. `SalesByCustomer-Grouped` 0.0% → 2.6%,
and more meaningful than the number: page one now has the right *structure* -
group header with the customer's name and its labels, the detail row, the
subtotal, and a page break per group - where before it was one undifferentiated
list of rows with a blank first column. Both baselines are raised.

**Still wrong on the grouped report**, in rough order of how much page they
account for: the detail row's amount and date appear to land in the same cell and
print concatenated; the report header's title is clipped at the left; numbers and
dates render unformatted (`53.9`, `2001-05-26`) where Crystal renders `$53.90`
and `05/26/2001`.

> The concatenation was not a placement error at all - the values were in the
> right cells, and the cells were in the wrong places. See the section below.

**A limit of the corpus scan worth knowing.** These converter changes moved the
rendered output of both visual cases but left the corpus scan's PDF byte total
identical to the byte. The scan renders without data, and a details table with no
rows renders nothing at all, so no static table row it emits ever reaches the
page. The corpus scan is a check on parsing, conversion and *not crashing*; it is
close to blind to how the table itself is laid out.

Verified: **0 of 2,324 and 0 of 88 fatal**, non-fatal steady at 12,299
occurrences, oversize still 4, **874 crystal tests** and **291 engine tests**
(net8/net10, 259 on net48) green, visual regression 5/6 with the same pre-existing
missing-page failure.

### Objects do carry a position, and the page is not always Letter portrait

**Both implemented.** Two separate pieces of geometry were never being read.

**Object position: tag-190.** Every object wrapper is followed by a four-byte
record holding two big-endian UInt16s — left, then top, in twips, relative to
the section. It was found by working backwards from ground truth rather than by
reading more bytes of the records already suspected: rendering a report with the
real engine, pulling the text positions out of the PDF content stream, and
searching every record in the file for those values as integers. The page-header
column labels render at 84, 222, 348, 474 and 612 points from the page edge; the
matching twip values, less the 12-point inset the engine renders into, turned up
at offset 0 of a tag-190 record after each of the six text objects, and again
after each of the six detail fields underneath them.

The reading is confirmed several ways beyond that arithmetic. Header labels and
the detail fields below them share identical lefts and differ only in top, which
is what a column layout is. The first column's underline in the reference PDF
ends at 74.15pt, exactly the right edge of a 1123-twip object starting at left
120 — a right-aligned numeric column, which is what "Customer ID" is. And the
rule is universal, not a lucky file: across both corpora, **136,712 objects in
2,412 files, every single object wrapper is followed by a tag-190, and every one
is exactly four bytes**. The widest offset seen is 20 inches, so the UInt16 the
four-byte length forces is not a constraint in practice.

The flow pass in the converter is deleted rather than kept as a fallback. It
existed only because position looked absent, and with real data it would do harm:
a section whose objects share a left edge is a vertical stack, and flowing it
would spread it out into a row. What replaces it is only a check that the band is
tall enough for what it holds.

**Page setup: tag-398.** `int32 width, int32 height`, twips, with orientation
already applied — a landscape report stores the wider value first, so there is no
flag to read. This was not being parsed at all: every report got US Letter
portrait with half-inch margins from hardcoded defaults. That is wrong for
**17 of the 88** public files and about one in five of the larger corpus, and the
page body is what object positions are relative to, so a wrong page misplaces
everything on it.

Read rather than recognised, which matters: the corpus contains Letter, Legal, A4
(`boyum__ServiceCall` reads 11899 x 16841 twips, A4 to within a twip), sizes up
to 30 inches wide, and label stock as small as an inch square. A table of paper
names would not have covered it. 109 of the 2,324 files carry no tag-398 and keep
the default.

**Margins are deliberately not touched.** The 32 bytes after the two dimensions
are **byte-for-byte identical in every one of the 2,303 files that carry it**, which is
what a "use the printer's defaults" sentinel looks like and not what per-report
margins would look like. The real engine renders CustomerList into a 12-point
inset, and that inset is what makes the position arithmetic above come out exact,
so 240 twips looked like the right default — but measured, it makes the one case
with real signal *worse* (ink agreement 15.0% → 12.5%, while a report that
renders no rows went 0.0% → 2.9%). Geometry says one thing and measurement says
another, so the default stands until that contradiction is understood. Worth
noting the likely reason they disagree: a printer's printable area and a report's
page margins are different quantities, and the 12-point clip in the reference PDF
is the former.

> **Resolved, and the conclusion above was wrong.** The geometry was right and
> the measurement was misleading, because a second error was cancelling it out:
> the table's columns were being built from each object's own width rather than
> from the distance to the next column, which compressed every row and so
> partly hid the fact that the body was too narrow. Correct the columns and the
> margin becomes unambiguous - a sixth of an inch takes the measurable case from
> 16.2% to 28.9%, where it had made it worse before. Neither change is worth
> anything without the other. See "A table's columns are as wide as the gaps
> between them" below.

**Result.** The one visual case that carries real signal went **8.9% → 15.0%**
ink agreement, and its recorded baseline is raised to match. The other five are
unmoved and cannot move: they have no data fixture. The wrap bound from the
previous round is still doing its job — oversize warnings stay at 4 — and nothing
else shifted: **0 of 2,324 private and 0 of 88 public fatal, non-fatal steady at
12,299 occurrences, 873 crystal tests green, visual regression 5/6** with the same
pre-existing missing-page failure.

**Next for layout fidelity**, now that position and page size are real, is the
margin contradiction above, and then the data-fixture limit — four of the six
reference reports render no rows, so the suite currently has exactly one case
able to detect a layout regression. Widening that is worth more than another
format find. (Partly done — see the section directly below.)

### Free-form objects carry no position, and tag-158 is not where it lives

**Investigated and partly fixed.** The next non-fatal category was `Size 'X' is
larger than the RDL specification maximum of 160 inches` — 2,039 occurrences, but
concentrated in only 26 files, nearly all Canadian T4 tax forms. Those are
precise-layout documents, so the sizes were the symptom rather than the problem:
the offending values were `<Left>` positions of 177 to 328 inches, increasing
monotonically down the section. That is the signature of the free-form flow
fallback, which lays objects out left to right by width when Crystal reports no
position. On a 316-object band it walks straight off the page, so everything past
the first few objects rendered invisibly.

**The measurement that reframed it:** the fallback is documented as covering "the
degenerate case where every object reads Left = 0". Counting rather than sampling
shows it is not a degenerate case at all — across **3,087 objects in all 88
public-corpus files, Left and Top are zero every single time**. The fallback is
not a fallback; it is the only layout path the converter has ever used, and every
free-form position in every converted report is synthesised.

**Where the position is not** (negative results, so this ground is not covered
again). The parser reads tag-158 as `[0-3] width, [4-7] height, [8-11] left,
[12-15] top`. Width and height are right — they match the objects' real sizes —
but bytes 8 through 15 are zero in every record examined. Dumping the full
payload rather than the first 16 bytes shows why: everything after the object
name is **byte-for-byte identical across every object in a report** (a colour
table and flags), so the record simply has no per-object position in it. The
enclosing wrapper record does not either — the bytes following the nested tag-158
hold the bound field name and a few small counters. So the comment naming offsets
8-15 as left/top is wrong and has been corrected.

Position presumably lives in a per-section placement structure that has not been
located yet. Finding it is the single highest-value item left for layout
fidelity, and it would matter for the visual-regression work too, where five of
the six reference reports currently score zero ink agreement.

> **Superseded.** It was found: the record immediately after each object
> wrapper. See "Objects do carry a position, and the page is not always Letter
> portrait" below. Two claims in this section were also wrong, and are worth
> naming because both were the result of reasoning from an absence. "Every
> free-form position in every converted report is synthesised" was true only
> because the parser was reading the wrong bytes. And the five reference reports
> scoring zero ink agreement have nothing to do with layout — they have no data
> fixture, so they render no rows at all, which the visual suite already says in
> the comment above its baseline table.

**What was fixed meanwhile (2,039 → 4).** The flow now wraps at the printable
width — page width less margins — starting a new row and growing the band to
hold it. This is not a claim to be correct layout; it is a bound. A section that
already fits is laid out exactly as before, because the wrap only engages once a
row is full, so the change is confined to the sections that were previously
running off the page. Content that was invisible is now on it: the corpus renders
8,187 more bytes of PDF than before.

The four remaining oversize warnings are unrelated and pre-existing — they are
quoted in points rather than inches (`56089.62pt`), one per file, unchanged
before and after this round, and were not investigated.

Verified: **0 of 2,324 private, 0 of 88 public** fatal, non-fatal 14,334 →
12,299, 868 crystal tests green, visual regression 5/6 with the one report that
carries real signal (`CustomerList`, 8.9% ink agreement) still inside its
baseline tolerance — so the layout change did not degrade the only case that can
currently detect such a regression.


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
The `QESession` OLE stream is encrypted with a 16-byte key that is not the fixed
one the `Contents` stream uses and is not carried anywhere in the file. Cannot be
decoded. Every converted report requires the user to fill in `<ConnectString/>`
manually. No fix possible without the key.
