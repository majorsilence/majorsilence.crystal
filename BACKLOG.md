# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

### Non-Sum group footer aggregates
The aggregate function code for each Crystal summary field lives in tag-237 →
tag-236 child, byte 22 (0x01=Sum, 0x02=Count, 0x03=DistinctCount, 0x04=Min,
0x05=Max, 0x06=Average). The current converter always emits `=Sum()` for
numeric group footer columns.

**Investigation result**: All 40 corpus files have only function code 0x01
(Sum) in every tag-237 record. Additionally, tag-237 records appear 11–23x per
file while group footer FieldObjects appear only 1–2x — they are not in a 1:1
correspondence, so a naïve index-based mapping would be incorrect. To implement
non-Sum aggregates safely, a corpus file containing a Crystal group summary
using a non-Sum function (e.g. Count or Average) is required to verify the
correct mapping between tag-237 records and visible FieldObjects.

**Status: needs corpus** — need an RPT with a non-Sum group aggregate.

---

### Section-level suppress formula
Crystal Reports allows `Suppress (No Drill-Down)` to be driven by a formula
instead of a static flag. The formula is referenced somewhere in the section
properties or adjacent tag-119/118 records.

**Investigation result**: All 40 corpus files use only static section
suppression (tag-254 byte flags). No formula-driven suppression appears in any
section. The standard section wrapper sequence is always
`sectionStart → 157 → 255 → objects` with no additional formula-reference tag
anywhere between them. To implement this, a corpus RPT with a formula-suppress
on any section is required.

**Status: needs corpus** — need an RPT with a section suppress formula.

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
Crystal Reports supports embedded images (Picture objects). They appear in the
section TSLV stream as a distinct object type with a separate tag.

**Investigation result**: None of the 40 corpus files contain embedded image
objects. The unusual section-level tags observed (170/171, 172/173, 175/176,
182/183, 185/186) all have data payloads of 80–172 bytes — far too small for
image bytes even for a 1px thumbnail. These tags appear to correspond to charts
(170–176), maps (182/183), and cross-tab objects (185/186). Image storage in
Crystal Reports likely uses a dedicated OLE sub-stream with only a reference tag
in the TSLV section body; parsing it requires OLE compound document work beyond
the current scope.

**Status: needs corpus** — need an RPT with an embedded picture object.

---

### RepeatGroupHeader binary bit
The `Section.RepeatGroupHeader` model property is unpopulated because no
corpus file has it set — the bit position in tag-254 (or tag-229) is unknown.
Obtain or construct an RPT with "Repeat Group Header on Each Page" enabled,
identify the bit, and wire it to `<RepeatOnNewPage>true</RepeatOnNewPage>` in
the TableGroup Header.

**Status: needs corpus** — need an RPT with RepeatGroupHeader enabled.

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
parent. The outer TSLV stream contains a subreport object tag that references
an inner stream by name. Implement recursive OLE parsing, convert the inner
report to RDL, and embed it as an SSRS `<Subreport>` element pointing to a
companion `.rdl` file written alongside the parent.

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
