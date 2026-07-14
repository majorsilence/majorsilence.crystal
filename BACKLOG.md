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
the details row and group header/footer rows. The formula supersedes the
static suppress bit when both are present — Crystal keeps the stale checkbox
value set alongside an attached formula, so static-wins would permanently hide
the section. Untranspilable formulas (variable-based, `=""` fallback) emit no
Visibility rather than hiding content.

**Remaining**: the other hook entries (newPageBefore/After formulas, back
colour) are detected by the scan tooling but not yet emitted; free-form
section items (page header/footer) don't receive per-item Visibility.

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
The `Section.RepeatGroupHeader` model property is unpopulated — the bit
position in tag-254 (or tag-229) is unknown.

**Investigation result (updated)**: a byte-level variance scan of the
undocumented tag-254 tail (offsets 29–52) across a large real-world corpus
found *no* variance on GroupHeader records, so RepeatGroupHeader is unlikely
to live in the tag-254 tail. Variance was found elsewhere: PageFooter
byte[30] = 0x01 in ~9% of records (cheque-style reports — plausibly
"reserve minimum page footer" or print-at-bottom variants), Detail/section
byte[45] and ReportFooter byte[46] ∈ {0x01, 0x02} in a handful of reports.
Next candidates for RepeatGroupHeader: tag-229 (group options record) tail
bytes, or the area-level tag-254 (bytes[3..4] == 0) which the scan skipped.

**Status: needs binary research** — diff tag-229/area-254 between a report
with and without the option enabled.

---

### ResetPageNumber RDL emission
`Section.ResetPageNumber` is now parsed from tag-254 bytes [17..18] but never
emitted — SSRS 2005 schema has no group-level `<ResetPageNumber>` element.
The Majorsilence Reporting engine may support it via a custom extension; once
confirmed, add emission logic.

**Status: pending confirmation** — need to confirm Majorsilence Reporting
supports a custom RDL extension for this.

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

**Remaining gaps**: on-demand subreport behaviour (tag 180/181 pairs) is not
modelled — the subreport renders inline.

### Cross-tab / OLAP grid objects
Cross-tab objects are pivot-table structures with row groups, column groups,
and a summary cell matrix. They require a new TSLV object-tag branch and map
to an SSRS `<Matrix>` data region. Tag 185/186 is the likely cross-tab wrapper
(appears in `Canada-CrossTab.rpt`, `BigCells.rpt`, `BigCells-Mexico.rpt`).
Binary layout must be reverse-engineered from those corpus files.

### Charts / graphs
Crystal Reports charts store axis definitions, series, legend, and data
bindings in a dedicated object block. Tags 170/171, 172/173, 175/176 are the
likely chart wrapper candidates (appear in pie-chart and RWB-map files). SSRS
has a `<Chart>` data region. Very high effort.

---

## Blocked / by design

### Connection strings
The `QESession` OLE stream is encrypted with a proprietary 16-byte key not
present in the decompiled runtime JAR. Cannot be decoded. Every converted
report requires the user to fill in `<ConnectString/>` manually. No fix
possible without the key.

### Crystal variable declarations (`Local NumberVar`, etc.)
Crystal multi-pass variables (`Local`/`Global`/`Shared` + `NumberVar` etc.)
have no SSRS VB.NET equivalent — SSRS evaluates expressions in a single pass
against the DataSet. These always emit `=""`. The correct fix is to rewrite the
report logic using SSRS running values, aggregates, or report parameters, which
is a human task not automatable by the converter.
