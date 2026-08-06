# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

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

## Blocked / by design

### Connection strings
The `QESession` OLE stream is encrypted with a proprietary 16-byte key not
present in the decompiled runtime JAR. Cannot be decoded. Every converted
report requires the user to fill in `<ConnectString/>` manually. No fix
possible without the key.
