# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

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
