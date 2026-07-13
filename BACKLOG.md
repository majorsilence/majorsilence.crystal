# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

### Non-Sum group footer aggregates
The current converter always emits `=Sum()` for numeric group footer columns.

**Investigation result (updated)**: the earlier hypothesis (tag-237 → tag-236
child, byte 22 = aggregate function code) is wrong. tag-237 is a per-object
*field format* record: one appears inside every FieldObject/TextObject wrapper
(explaining the 11–23x count), and byte 22 of its tag-236 child reads 0x01 for
plain database fields too. A scan of >130,000 tag-236 records across a large
real-world corpus found no value other than 0x01 at byte 22, even in reports
that visibly use Count/Average summaries. The 130-byte tag-237 payload differs
between a summary FieldObject and a plain detail FieldObject at child offsets
8–11 (four boolean-looking flags), which may encode "is summary" but not the
function. The summary *function* must live elsewhere — candidates are the
tag-241/243/245/247/249/251 sequence that follows the format records inside
object wrappers, or a summary-definition registry outside the section stream.

**Status: needs binary research** — corpus with non-Sum summaries is now
available; next step is diffing those object records between a Sum and a
Count/Average summary object.

---

### Section-level suppress formula
Crystal Reports allows `Suppress (No Drill-Down)` to be driven by a formula
instead of a static flag.

**Investigation result (updated)**: scanning a large real-world corpus for
unexpected tags in the `sectionStart → 157 → 255 → objects` wrapper sequence
found zero hits — the formula reference is *not* stored between the wrapper
records. Internal formula fields named `*_Visibility` (already skipped by the
formula-field extractor) strongly suggest section-suppress formulas are stored
as ordinary tag-119 formula definitions and linked to sections elsewhere —
likely inside the tag-255/254 payload itself (a formula-name/id slot) or the
tag-266/267 record pairs that follow objects in most files. Next step: take a
report with a known conditional suppress, locate its `*_Visibility` formula,
and search the section records for a back-reference.

**Status: needs binary research.**

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

**Limitation**: WMF/EMF metafiles (placeable `D7 CD C6 9A` or standard
`01 00 09 00` headers) cannot be embedded — RDL has no WMF MIME type. These
are skipped with a warning. A follow-up could rasterize WMF → PNG. Some OLE
"package" embeddings carry only presentation streams (`\x02OlePres000`,
again WMF) and are skipped likewise.

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
Crystal Reports subreports embed a second OLE compound document inside the
parent. **New findings**: the placed subreport object wrapper is
**tag 163/164** (payload ~107 bytes, contains the nested tag-158 bounds
record); the inner report lives in an OLE storage named `Subdocument N`
containing its own `Contents` (and often `QESession`/`PromptManager`) streams
— directly parseable by the existing TSLV pipeline via
`OleReader.ReadStreamAt("Subdocument N/Contents")`. Subreports are the single
most common unimplemented feature in real-world corpora (~23% of files).
Implement: parse tag-163 for bounds + the link to its `Subdocument N` index,
recursively convert the inner Contents, write a companion `.rdl`, emit an SSRS
`<Subreport>` element. On-demand subreports also appear (tag 180/181 pairs
show up in files with drill-down subreports/charts — needs confirmation).

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
