# Majorsilence.Crystal — Backlog

Items are grouped by tractability. "Blocked" items cannot be fixed without
information unavailable from the decompiled runtime.

---

## Tractable (implementable with binary research)

### Non-Sum group footer aggregates
The aggregate function code for each Crystal summary field lives in tag-237 →
tag-236 child, byte 22 (0x01=Sum, 0x02=Count, 0x03=DistinctCount, 0x04=Min,
0x05=Max, 0x06=Average). The current converter always emits `=Sum()` for
numeric group footer columns. The tag-237 records appear in stream order that
likely corresponds to the order of FieldObjects in the group footer section;
confirm this and use the actual function code instead of hardcoding Sum.

### Section-level suppress formula
Crystal Reports allows `Suppress (No Drill-Down)` to be driven by a formula
instead of a static flag. The formula is referenced somewhere in the section
properties or adjacent tag-119/118 records. Identify the reference, transpile
it with `FormulaTranspiler`, and emit it as an SSRS `<Hidden>` visibility
expression instead of `true`.

### Parameter pick-lists and validation ranges
Crystal Reports parameters can carry an allowed-values list or a range
constraint. These are stored in the parameter field definition records near
tag-122/123 (adjacent to tag-113 which already gives name+type). Parse them
and emit `<ValidValues>` or `<DefaultValue>` elements in the SSRS
`<ReportParameter>` block.

### Image / OLE picture objects
Crystal Reports supports embedded images (Picture objects). They appear in the
section TSLV stream as a distinct object type with a separate tag (not 159 or
165). Identify the tag, extract the image bytes (likely JPEG or BMP), emit an
SSRS `<Image>` element with `<ImageData>` (base64) or a file-path reference.

### RepeatGroupHeader binary bit
The `Section.RepeatGroupHeader` model property is unpopulated because no
corpus file has it set — the bit position in tag-254 (or tag-229) is unknown.
Obtain or construct an RPT with "Repeat Group Header on Each Page" enabled,
identify the bit, and wire it to `<RepeatOnNewPage>true</RepeatOnNewPage>` in
the TableGroup Header.

### ResetPageNumber RDL emission
`Section.ResetPageNumber` is now parsed from tag-254 bytes [17..18] but never
emitted — SSRS 2005 schema has no group-level `<ResetPageNumber>` element.
The Majorsilence Reporting engine may support it via a custom extension; once
confirmed, add emission logic.

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
to an SSRS `<Matrix>` data region. No corpus file currently exercises this
path — binary layout must be reverse-engineered from scratch.

### Charts / graphs
Crystal Reports charts store axis definitions, series, legend, and data
bindings in a dedicated object block. SSRS has a `<Chart>` data region.
Requires identifying the chart object tags, parsing series/axis structure, and
generating SSRS chart XML. Very high effort.

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
