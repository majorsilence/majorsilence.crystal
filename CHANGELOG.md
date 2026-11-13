# Changelog

Release notes, newest first. [BACKLOG.md](BACKLOG.md) has the measurements behind each
line; [ROADMAP.md](ROADMAP.md) has what comes next.

## Unreleased

**Fixed**

- **A formula named with two spaces in a row prints its value in rich text.** The spaces
  inside its reference were written as html's `&nbsp;`, the formula was not found under that
  name, and the reference printed as written.
- **Text whose font or size changes inside a line prints each run in its own font and
  size**, as Crystal does: a paragraph in Arial that goes on in Calibri, or a larger word
  before smaller ones on the same line, printed whole in one font, and a bullet in Symbol
  printed as a middle dot. Such text is now written as html, each run carrying its family,
  its size and Crystal's line pitch for it, and its lines break in the room of the font of
  most of its letters. Measured against Crystal's renders, 35 reports rise by 1 to 69 points.
- **Text in two sizes whose words change style inside a line prints each size and style as
  Crystal does.** A notice's 12pt underlined heading over 11pt paragraphs with bold words in
  them printed whole in the object's own 10pt, plain and in one alignment: text in more than
  one size was split into a piece per size only where every change of style also started a
  line. It is now split where the size changes, at a line break, and each piece keeps its
  bold, italic and underlined words. Measured against Crystal's renders, 14 notices and
  letters rise by 2 to 43 points.
- **Reset Page Number After on a group footer numbers each group's pages from 1**, its total
  page count too, as Crystal does: five one-group pages that read "Page 1 of 5" to "Page 5
  of 5" each read "Page 1 of 1". The setting was read but not written, so the numbering ran
  on across groups. A suppressed footer, which never prints, resets nothing, as in Crystal.
  Needs an engine that reads RDL 2008's PageBreak on a group (Majorsilence.Reporting #655).
- **Text is set with the font's pair kerning**, as Crystal sets it: "Taxes" and "Total"
  close up by a tenth of an em in Arial, and a space before "A" is shorter. Without it every
  word after such a pair sat a little to the right of Crystal's, the drift growing along the
  line. Every report now asks the engine for kerning, and a line's room to break in is no
  longer widened by half a percent: that allowance stood in for the narrower kerned words.
  Needs the engine's opt-in kerning.
- **Rich text that is justified, holds tabs or may grow prints in its own styles and
  alignments too**, as Crystal prints it: such text stayed plain, so its bold, italic and
  underlined words printed plain and its paragraphs took one alignment. It is now written as
  html like any other rich text: a tab as the engine's `<tab>` at Crystal's quarter-inch
  stops, a justified paragraph spread by the engine, and growing text grown by the lines it
  draws. A field's spaces in a row inside html text are kept, as in plain text, where html
  folded them to one. Its lines break in the style most of its words print in, not the
  object's own: a notice whose object said bold over plain paragraphs broke every line a
  word short. Needs the engine to spread justified html lines, keep the box's tab stops,
  and grow and clip html text as it does plain text.
- **A box that may not grow prints every line Crystal prints in it**, as tall as it is. The
  room left below its lines was worked out at the pitch written, where the engine reads
  the pitch back a little longer: a form's twelfth line of double-spaced 10pt text fell
  0.1pt short of its room and was lost.
- **An empty page header section beside one that holds the page's heading keeps its room**,
  as Crystal prints it at the top of every page. Only the section with something in it went
  into the details table's header, and the empty one was dropped: a certificate's 1.6in
  spacer above its heading went missing, and the whole letter printed that much higher.
- **A section set to Suppress Blank Section whose text fields all print nothing is left out,
  its height with it**, as Crystal leaves it out. It printed blank at its full height: a page
  header of title formulas that came out empty put two budget reports' pages 0.68in below
  Crystal's, and a summary's empty group headers opened a gap above every group. Only a
  section of text fields (string columns, parameters and formulas) is decided this way; one
  holding text, a line, a picture or a number prints as before.
- **Details formatted with multiple columns print side by side, Down then Across, as on a
  sheet of mailing labels.** Record 108 holds the layout (`ReportDefinition.DetailColumns`):
  the detail width and height, the gaps between details and the printing direction. Each
  label printed one to a row down the left edge of the page, the next column's on the pages
  after, and as tall as its section rather than its label. Each now fills a cell of the
  detail height, its address running on to the foot of the cell and cut there, as Crystal
  cuts it. Across then Down is still printed one to a row.
- **A details field set to Suppress If Duplicated prints again on the first record of each
  group**, as Crystal starts the comparison again with every group. It was compared with the
  record before across the group boundary, so a group that opened with the value the last
  one closed on printed it blank. In a group header or footer Crystal prints such a field in
  every instance, as it already printed here.
- **A report parameter prints the value it is given when a stored procedure has a parameter
  of the same name.** A report declaring both `PeriodEnd` and the procedure's `@PeriodEnd`
  gave a value supplied as `PeriodEnd` to the procedure's, which reports list first, and
  left the report's own empty: an income statement's column headings printed "1" for the
  year and its title no period end. The name as written is now matched before the name
  without its `@`.
- **A field that prints more than one line, or that interprets HTML, sits as low in its box as
  Crystal prints it.** Crystal lays such a field out as it does a text object, its first line
  the font's ascent in whole pixels of the device below the top, where a one-line field has
  the ascent in twips: about a point lower in a report set for no printer. Every address block
  built with line breaks, and every HTML field, printed that much high.

**Changed**

- **The libraries, the CLI and the tests target .NET 11 as well as .NET 10.** The .NET 11
  builds compile async methods as runtime async. Round keeps rounding a value such as 1.005
  at the digits as written on .NET 11, whose double-to-decimal conversion is now exact: the
  value is taken to 15 significant digits first, as the conversion did up to .NET 10.
- **The engine is Majorsilence.Reporting 27.0.0-alpha.12**, which sets PDF text with the
  font's pair kerning when a report asks for it. Measured against Crystal's renders with
  kerning on, the reports average 84.6 where they averaged 83.9: 419 rise by more than a
  point and 15 fall, by at most 7, mostly where a line now breaks a word away from Crystal's.
- **The Avalonia viewer builds from the published Majorsilence.Reporting.UI.RdlAvalonia
  package** (27.0.0-alpha.11), where it needed a Reporting checkout beside this one, and
  it and its demo move to Avalonia 12.1.1, which that package needs.
- **The engine is Majorsilence.Reporting 27.0.0-alpha.11.** Html text in a box that may not
  grow shows only the lines that fit, as plain text does, keeping a last line whose letters
  fit. Measured against Crystal's renders it moves no report by more than a point but two
  forms, which rise by 4 to 5.

## v0.4.0

**Added**

- **A group kept together that does not fit in what is left of the page starts on the next
  one**, as Crystal's Keep Group Together moves it, and one longer than a page runs on from
  the top of the next. Each group ran on from wherever the last one ended, so a page held
  the start of a group whose rest was on the page after. Needs Majorsilence.Reporting
  27.0.0-alpha.6, which reads KeepTogether on a TableGroup.
- **A multi-table report renders from one table per Crystal table** (#36). Push each table
  under its name in `RuntimeOverrides.TableData`, and the engine joins them as the report's
  own links say: inner and left-outer joins on equal keys, in the order Crystal applies them.
  A report renders the same from per-table data as from the table a caller joined already,
  and a caller that pushes one pre-joined table through `Data` keeps working unchanged.
- **The report's tables and links are read from the file.** The `QESession` stream, the
  QueryEngine's saved session, is now decrypted and decoded. It gives the parsed report its
  data source, every table with its columns and alias, and its links
  (`ReportDefinition.TableLinks`); `ReportEngine.Analyze` lists them too, in
  `ReportAnalysis.TableLinks`, a new required member (empty for a one-table report). They
  agree with the Crystal runtime's own for every table and link in all three corpora: 79
  links in the public one, 166 in the third-party one and 648 field pairs in the private one.

- **A section's Keep Together and a group's Keep Group Together are read from the file**
  (`Section.KeepTogether`, `GroupDefinition.KeepTogether`), where neither was. They agree
  with the Crystal runtime's for every report in all three corpora: 243 sections that turn
  Keep Together off and 319 groups that keep together. A group kept together now moves
  whole to the next page (above); for a section, a band of fixed height already moves whole
  as Crystal's does, and one that grows needs the engine to measure it first.
- **The report's own record sorts are read** (#31). A converted report now sorts its details as
  the report does, ascending or descending, whatever order the data arrives in. They agree
  with the Crystal runtime's for every report in all three corpora.
- **A subreport with no rows prints its header and footer** (#38), where it printed nothing.
- **A group's top or bottom N prints as Crystal's does**: the Top5USA family keeps only the
  N groups ranked first by the summary, in rank order, while percentages of the grand total
  still count the rest, as Crystal's do. A percentage summary with no format of its own
  prints with two decimals and a percent sign, where it printed a bare number.
- **A formula's result type is read from the file** (#9), so an object showing a numeric
  formula gets the number format the report gives it, where it printed unformatted.
- **More date formats are honoured** (#11): a date showing only two of month, day and year
  with different separators, text before or after the date, and the day of week before or
  after it. Each was checked against what Crystal prints for the same setting.
- **A group footer that lists or totals its group through a variable prints it** (#19).
  Crystal's usual way of doing that takes three formulas: one in the group header resets a
  variable, one in the details adds each record to it, and one in the group footer shows
  it. The footer printed nothing, because an RDL expression has no variables; it is now
  written as the aggregate it amounts to, a joined list or a sum over the group's records.
  Only that exact shape is rewritten; any other use of a variable still prints empty.
- **A formula that builds its value in steps prints it** (#19): an address assembled line by
  line, a value computed through a variable or two, an if that sets a variable. Such a
  formula is reduced to the single expression it computes. One that loops, uses arrays, or
  reads a variable left over from an earlier record still prints empty.
- **The same, for Basic-syntax formulas**, the dialect most production reports use: an
  address built in a Shared variable line by line, a Dim set in an If block, an ElseIf
  chain. Across a corpus of 2,324 production reports, the share of formulas that printed
  nothing fell from 30% to 21%.
- **A box's fill and a line's colour stored with a flag of FF are kept**, where the box drew
  empty and the line black.
- **ReferenceRenderer --synthetic makes up rows for a report that saved none**, renders the
  reference from them and writes them as its data fixture, with the parameter values used.
- **A percentage of a group's total divides by that group's total**, where every percentage
  was a share of the grand total. Needs Majorsilence.Reporting 27.0.0-alpha.5.
- **An empty page header leaves its height below the report header on page 1**, as Crystal
  prints it, where what followed sat that much higher.
- **A tab in a text object is kept**, and goes to the next half-inch stop.
- **A report header holding a field prints in its place**, where with no details table it
  printed below every report header after it.
- **A picture stored first in the file prints**, where it was dropped.
- **An object's Suppress checkbox and suppress formula are honoured**, where every object
  printed. A suppressed object keeps its space, as Crystal's does, rather than closing it up
  and moving what is below it.
- **A group header or footer whose objects stand on several lines prints each in its place**,
  where they were laid out as one row of columns: a group's second and later sections too,
  a band whose one tall object stands beside a column of labels, and a later section
  holding an object wider than its column, such as a paragraph across the page.
- **An object suppressed whatever the data is left out** of the converted report, rather than
  written hidden beside an empty place-holder.
- **A box drawn from one of a group's headers into the next is drawn in both**, as it already
  was across report headers, where it was cut off at the bottom of the first; likewise for a
  group's footers and the page header and footer.
- **A subreport's frame is drawn in its own colour**, where every one was black.
- **Report Title prints nothing for a report with no title**, as Crystal does, where it
  printed the converted report's internal name.
- **A date column with no time of day prints only the date**, the machine's short date, where
  on the machine's formats it printed a midnight after it too. **A date and time prints both**,
  in its own time format, order and separator ("31/01/2024  04:00PM"), where it printed the
  date alone; a time of day prints its time.
- **A page header section that is only a band of colour is drawn**, where it was dropped and
  the table below it sat that much higher.
- **New Page After on a group header that prints nothing starts a page for each group**, as
  Crystal does, where the groups ran on together.
- **New Page After N Visible Records on the Details area starts a new page after every Nth
  record**, as Crystal does, where the records ran on. N is the area's count or its formula,
  a parameter on a half-page receipt, and 0 is no break; the footers that close the Nth
  record's groups stay on its page. It is read as `Section.NewPageAfterRecords` and
  `NewPageAfterRecordsFormula`, and agrees with the Crystal runtime's for 422 reports, every
  one that sets it among them. A report whose details have a suppress formula, or whose group headers print, is
  left without the break for now.
- **A New Page After formula on the Details area starts a new page after the records it
  holds for**, as Crystal does, where the details ran on. One that reads only a parameter
  breaks after every record or none, "RecordNumber = 18" after the 18th, and the footers of
  the groups a record closes go to the new page with what follows, as Crystal's do. It needs
  an engine that tests PageBreakCondition at the end of a group, as Majorsilence Reporting
  27.0.0-alpha.8 does, which this release moves to; an earlier one breaks after every record.
- **A parameter passed through Trim (and the other string-only functions) stays a string**
  when the result is joined with "+", where it was taken for a number and a blank one
  printed "0".
- **An empty text object prints nothing and still draws its border**, where it printed its
  own name.
- **Lines of text are set at Crystal's pitch**, a whole number of screen pixels for each font
  and size, and at the paragraph's line spacing (one and a half, double), where they were set
  at the font size. A growing field drawn shorter than one line at that pitch keeps the
  engine's own line height, so that its one line does not grow every row of a table.
- **A date prints in the format its object gives it** when it is embedded in a text object,
  a date formula, a date parameter or Print Date and Data Date, where it printed the machine's
  short date.
- **A growing field moves nothing else in its section**, as in Crystal, where a growing field
  prints over what is below it and only the section grows to hold it. Everything below it
  moved down by all of its growth, in every column: a name and address over several lines
  pushed the rest of a letter, and the fields beside it, down the page.
- **A report with no page setup prints on the paper and the way round its printer settings
  name**, where a landscape report printed portrait. One with no printer settings that
  follows its printer's paper takes a portrait printer's quarter-inch default margins, where
  it took a sixth of an inch and its text wrapped wider than Crystal's.
- **A summary of a formula prints its total** ("Sum of @Amount"), where it printed its name,
  and so does a summary embedded in a text object ("totally {Sum of @Amount}").
- **A formula that builds a value line by line under many conditions prints it**: each "if"
  that only adds a line now adds it once, where the whole value was written out on both
  paths and doubled with every "if", so that an address of seven optional lines passed the
  length allowed and printed nothing.
- **A label a formula chooses by parameter prints**, when another formula writes the same
  Shared variable too: the choices it makes print, and only the case it does not cover
  prints empty, where every case did.
- **Strings compare as Crystal compares them, ignoring trailing spaces**: "" = " " and
  "ab" = "ab   " are true, and "a" < "a " is not. A section shown when
  `Mid({?Code}, 1, 1) <> " "` is false was hidden for a blank parameter, and a padded
  code from a fixed-width column did not match its value.
- **A parameter has the type its report declares**: Number, Currency, Boolean, Date, Time,
  DateTime or String, where a Number, a Boolean and a DateTime were read as strings and then
  typed by how a formula used them. A string compared with "" was taken for a number, and a
  column it hides was hidden. A pick list is kept for a string parameter only, since for a
  number it is read as the descriptions alone.
- **A subreport's parameter takes the main report's parameter of its name**, or the one a
  `Pm-?` link names, before a column or a formula that shares the name; and one matched by
  name alone is fed a column only of its own kind. A date parameter was given a formula's
  sentence, and a Boolean stored-procedure parameter a column of numbers, and the render
  failed once parameters had their declared types.
- **A blank string parameter is empty**, as Crystal has it, where the engine wrapper gave it
  a single space that the engine once needed: its length was 1, and an address built from
  the parameters that are set printed blank lines and grew its band by them.
- **A section in a suppressed or hidden area stays out whatever its suppress formula says**,
  as in Crystal. A formula that came out false printed the section anyway, and a statement's
  hidden header strip printed above every statement, pushing each on to a page of its own.
- **A group header or footer set to underlay the sections after it prints under them**, as
  Crystal prints it, where it took a row of its own: a statement's crests filled a page and
  pushed the statement on to the next.
- **A printed group band left of the first detail column keeps its place.** A statement
  printed from group footers across the page, its details suppressed, was re-based on the
  hidden detail column three inches in, and its left half drew over its right.
- **A text object's runs are read with their fonts** (`TextObject.Runs`): a bold label and the
  plain value after it, an italic word in a sentence.
- **ReferenceRenderer --synthetic** keeps a report's Command parameters, gives its parameters
  values its record selection keeps rows for, and skips a column a table lists twice.
- **A cross-tab on a formula prints**, its rows or columns and its summaries, where they were
  dropped and the grid with them; several summaries in a cell stack one above the next, as
  Crystal sets them, and a cross-tab with no row groups prints.
- **Two cross-tabs grouped on the same field render**, where the duplicate grouping name
  failed the whole report.
- **ReferenceRenderer --rows writes a data fixture for reports whose saved rows the runtime's
  rowset will not give**, from the runtime's own CSV export of them; `--rows-export` uses that
  route directly.
- **A column named only in the record selection takes its table from there**, where with two
  tables sharing the column it could read the wrong one and the selection keep no record.
- **ToText of a number column prints two decimals and grouped thousands**, as Crystal's does,
  where it printed "50" for "50.00".
- **A formula that shares its name with a database column prints the formula**, where every
  placed one printed the bare column.
- **A page footer showing a formula that reads no data stays at the foot of the page**, and a
  page header or footer made of several sections prints all of them, where only the first
  printed.
- **Short formula and parameter references ("@Qty") are read**, where the object printed its
  own name.
- **A formula that numbers the records as they print** ("n := n + 1") prints 1, 2, 3, where
  it printed nothing, and a row colour formula reading it alternates as Crystal's does.
- **A number or date set Left prints on the left**: Crystal's Default alignment and Left were
  both read as Left, and a number set Left printed on the right.
- **A box drawn from one report header into a later one is drawn where Crystal draws it**,
  where it was drawn at its record's height in the first header and pushed everything below
  it down. Where a line or box ends is read from the file (`ShapeObject.EndSection`,
  `EndBottomTwips`).
- **A chart or cross-tab in a later report header prints below the headers above it**, where
  it printed at the top of the page over them.
- **A text object's paragraphs start new lines**: a heading over two lines, or a name over a
  title, printed as one line run together.
- **A report header set to underlay the sections after it** lets them print beside or over
  it, where they printed below it.
- **A group header summary drawn away from its column prints where Crystal draws it.**
- **A section's colour formula colours its rows** (alternating row colours, colour by value),
  where every row took the section's fixed colour.
- **Data Date and Data Time print their values**, where they printed "[Data Date]".
- **A null field compared with '' counts as empty**, as in Crystal: a band suppressed when a
  field is '' is hidden where the field is null.
- **SQL Expression fields print their values**, where they printed "[%Name]".
- **New Page Before on a group starts a page between groups**, not before the first one,
  which left page 1 holding only the report's headers.
- **A group footer prints only what it holds**: no total in a column it leaves empty, and its
  objects where Crystal places them.
- **Lines and boxes are drawn as Crystal draws them**: a box's fill, line colour, width and
  style, a line's style, colour and width, and lines straight across or down the page.
- **A single Details section of labels, boxes or stacked fields prints as laid out**, where
  only its fields printed, one per column.
- **A running total prints in its number format** ($12,424.80, not 12424.800000000001).
- **A chart grouped "for each week" (or month, ...) by its own on-change-of** charts those
  intervals, where it charted every record.
- **A report prints within its own page margins**, read from the file, where every report
  used a sixth of an inch: a report set to half-inch margins, or to Crystal's default
  quarter inch under a portrait printer, no longer prints up and to the left of where
  Crystal puts it.
- **A grouped report with nothing in Details prints every group**, where it printed the first
  group's header and footer once: each group's sections print once per group, and the empty
  Details still take their space for every record, as in Crystal.
- **A group on a date breaks on its interval** ("for each month", week, quarter, year, hour
  and the rest): one group per interval, sorted in time, named as Crystal names it ("2024-01",
  a week by its Sunday), and a chart over the group charts the same intervals. A descending
  group sorts descending, where it printed ascending and repeated its header on every page,
  and "repeat group header" is read from where the file keeps it.
- **Charts print as Crystal draws them** (#16): the right chart type (fourteen group charts
  read as pies are stacked bars), title, axes with axis titles and labels at Crystal's sizes
  and weights, gridlines and legend. A pie draws as one pie of the groups, one-series bar
  charts give each bar its own colour, and a chart over a top-N group shows only those groups
  in rank order. Report-footer charts in tabular reports are no longer dropped, and every
  chart names its dataset, so a chart in a subreport no longer stops the report rendering.
- **Cross-tabs print in the file's own layout**: cell and header sizes, cell margins, grid
  lines, the Total row and column (honouring both suppressions, first or last as the file
  places them, in the grand-total colour), and each value in its own font and number
  format, with 0 for an empty intersection.
- **A report with several Details sections prints one line per band under each record**
  (#7), each object where its band draws it, where they were flattened into one line. Each
  band's own suppress formula applies: section formulas sharing a name (every section's
  visibility formula is called Section_Visibility) are told apart by the position the file
  records for each, where every section used to get the last one read.
- **A formula that sets a Shared or Global variable only under conditions prints**, when
  no other formula writes that variable: a label chosen by a parameter, an address whose
  first line is optional.

**Changed**

- **The engine is Majorsilence.Reporting 27.0.0-alpha.10.** A record selection on a
  parameter is applied with the parameter's value, where data given to the engine was
  filtered before any parameter had one; a pie of one slice is drawn whole, and each pie is
  centred in its share of the chart; a table row too tall for what is left under its
  repeated header prints once below it, without the header twice; rich text sits inside
  its box's padding once, and each of its paragraphs keeps its own alignment. A required
  parameter that was not supplied now goes to the engine with no value rather than its
  type's stand-in (today, for a date), so a record selection reading it lets every record
  through, as it is written to, and the parameter prints empty.

**Fixed**

- **Each paragraph of a text object prints in its own alignment**, as Crystal aligns it: a
  form's centred heading over lines at the left printed with every line centred, as a
  textbox has one alignment. A text that may not grow, without tabs, whose printing
  paragraphs are aligned apart is now written as html, each paragraph in a `<div>` of its
  own alignment, breaking its lines short through `<Custom><WrapInset>` so each is aligned
  against the object's own edges; a text split into one textbox per font gives each piece
  its paragraphs' alignment. Justified paragraphs print at the left in html, so text only
  justified or at the left stays plain. Needs the engine to align html lines by paragraph.
- **Words in bold, italic or underline inside a text object print in their own style**, as
  Crystal prints them: a notice's service and contact, in bold inside a plain paragraph,
  printed plain, since a textbox has one font. A text that may not grow, in one font and
  size, whose runs differ only in style is now written as html, each run in its own tags.
  Needs the engine to draw html text inside the box's padding once, not twice.
- **A field set to interpret its text as HTML prints its tags as styles**, as Crystal draws
  them: a pay listing's "<b>Pay Period:</b>" printed with its tags, where Crystal prints
  "Pay Period:" in bold. The option is read from the field's string format record, and such
  a field is written with Format html, which the engine draws as rich text.
- **A justified paragraph's lines reach the object's right edge**, as Crystal spreads them,
  while still breaking where Crystal breaks them. The room Crystal's lines break short by
  was padding on the right, which the engine also spread the justified lines to, so a
  letter's paragraphs stopped about 31pt short of their edge. A justified object now asks
  for it as `<Custom><WrapInset>`, which needs Majorsilence.Reporting 27.0.0-alpha.10.
- **A tab prints at Crystal's tab stops, a quarter inch apart**, from the object's left edge
  whatever its font. The engine's stops are half an inch apart, so a signature set in by
  fourteen tabs, 3.5 inches in Crystal, ran past the width of its box, wrapped, and
  printed at the margin. A text object, or a field whose formula writes a tab, now asks for
  18pt stops (`<Custom><TabStopInterval>`), which needs Majorsilence.Reporting
  27.0.0-alpha.10; that release also keeps a run of tabs at the start of a line at its
  stops and keeps a paragraph's indent when it wraps.
- **Text after an empty first paragraph prints in the alignment of its own paragraphs**, as
  Crystal sets it: an invoice's closing message, centred, typed after an empty paragraph set
  at the left, printed at the left. A text object took its first paragraph's alignment,
  whether that paragraph printed anything or not; it now takes that of the first one that
  does.
- **A details field set to Suppress If Duplicated prints only where its value changes**, as
  Crystal prints it: a column repeating one customer or code prints it on the first record
  of each run and leaves the records after it blank. The flag was not read, and the value
  printed on every record. Fields in group and page sections still print every time.
- **A group in original order on a formula prints its records in the order they come**, as
  Crystal prints it, an instance starting wherever the formula's value changes from one
  record to the next. Such a group sorted on its value and gathered every record with that
  value into one instance, and the groups in original order inside it then sorted too:
  only a group on a database column was numbered in runs. The renderer now evaluates the
  group's formula on each record, through the record selection probe it already runs, and
  numbers the runs on those values.
- **Text in one font and size with some words in bold or underlined prints in that size**,
  as Crystal prints it, where it ended in an empty paragraph in another size: a notice's
  14pt paragraph, with its service and contact in bold, printed in the 10pt of the empty
  line after it. The text's own size was taken only when its runs matched in every way,
  bold and underline included; now the family and size are enough, and the object keeps
  its own bold, italic and underline.
- **A Print Date or Data Date in a text object prints in its own date format**, as a placed
  one does: a letter's "{Print Date}" set to "October 23, 2026" printed the machine's short
  date. The format of a field embedded in a text was applied only to date columns,
  parameters and formulas; the special dates always took the machine's short date, which
  stays theirs when the field is on the machine's formats.
- **A section that grows and does not fit in what is left of the page prints whole on the
  next one**, as Crystal's Keep Together moves it, and one longer than a page runs on from
  the top of the next. A report footer holding a subreport started at the foot of the page
  and broke across it. A band of fixed height already moved whole; one that grows, through
  a subreport or an object that may grow, is now marked `KeepTogether` for the engine, which
  moves it once it has run (Majorsilence.Reporting 27.0.0-alpha.6 and later). Page headers
  and footers, and sections that turn Keep Together off, are left as they were.
- **A report header that needs a data scope is followed from where it ends**, as Crystal
  prints report headers one after another: the next band starts right below it, and moves
  down when a field in it grows. Its table was 1pt taller than the band, for a placeholder
  row that never prints, so the band below overlapped it: a header that grew printed over
  the one after it, a subreport's rows started some 85pt below its column headings, and on
  the engine after 27.0.0-alpha.8 everything below the first report header printed 1pt high,
  charts and all.
- **A field that may not grow prints on one line, cut off at its box's right edge**, as
  Crystal prints it, and so do a number or a date that may grow and a text that may not grow
  in a box one line tall. The engine broke such a value onto a second line wherever its box
  had room for one, so a ledger's names in boxes with room for two lines printed their last
  word under the rest; a value's own line breaks still start new lines, as many as its box
  holds. Uses the engine's NoWrap, in Majorsilence.Reporting 27.0.0-alpha.8. A detail field
  in a column narrower than itself is cut at its own edge too, not the column's: a label's
  address, in a column as wide as the gap to the name above it, printed as a dot.
- **A section set to underlay the sections after it takes no room, and they print over it,
  as Crystal prints them.** A group header's later sections, its inner groups and details
  print from its top, a footer's later sections from its top, and a Details section's later
  sections in the same record; the group's own footer, the next record and the next group
  print below it. A page header's whole page prints over it. A receipt's header, drawn under
  its lines, took a row as tall as the receipt and pushed the lines below it; a bill drawn
  over its watermark printed below it, most of it on the next page; and a page header's
  picture down the side of the page filled the page. One that nothing prints over is an
  ordinary section, as Crystal has it. Each such section is a row of no height whose
  Rectangle is marked `<Custom><DrawBeneath>true</DrawBeneath></Custom>`, which needs
  Majorsilence.Reporting 27.0.0-alpha.9, the release that reads that marker.
- **A group's name referred to as "Group #1" alone prints the group's name**, as Crystal
  prints it. Older reports name the field that way, placed on its own or embedded in a text
  as "{Group #1} - {Orders.Customer}", and it printed as "[Group #1]" or the braces themselves.
- **A group footer of no height with nothing in it takes no room**, as Crystal closes it
  up. It was given a line of its own, which doubled the pitch of a group that changes on
  every record and pushed a summary page's boxes down.
- **A group footer's total placed clear of the column it totals prints where it is
  placed.** It went into that column's cell, a column's width to the right of where Crystal
  prints it, and its label beside it; the footer is now laid out free-form, as one whose
  extra objects stand out of place already is.
- **A group in original order prints its records as they come when the report lists a
  table it never reads**, as Crystal leaves such a table out of the query. Any second table
  kept the groups sorting on their values, so a report naming its procedure twice gathered
  each category into one sorted instance.
- **A paragraph embedded as a document prints across the whole of its object, as Crystal
  prints it.** Its picture, a metafile far wider than it is tall, was cut to the raster's
  width alone and kept its height, so each line was squeezed to a quarter of its width; and
  every embedded picture was fitted inside its object keeping its shape, where Crystal
  stretches a picture to its object across and down. A notice's paragraphs printed in a
  narrow column at the left, and a logo drawn wider than its picture printed narrower.
- **A metafile picture prints in Crystal's colours and an embedded object's outlines at
  Crystal's weight.** A WMF can carry an enhanced-metafile copy of itself in its comments,
  and GDI+ drew that copy in place of the records, leaving out the raster operations a logo
  masks its bitmap with: a faded watermark and a coloured logo printed solid black. A
  picture's copy is now played with GDI, as Crystal plays it. An embedded object's cached
  presentation prints from its own records, mapped across its window, so a watermark drawn
  with one-point outlines keeps them, where the copy drew hairlines.
- **A report header taller than what is left of the page moves on alone**, as Crystal moves
  it. Report headers that need a data scope were written as rows of one table, kept together,
  so a letter whose first page held a long paragraph and the next page the rest went to the
  second page whole and left the first blank under its title. Each is now a table of its own.
- **A template that stores no page, or whose page follows a printer, takes the paper as
  Crystal does.** One with no page setup and no printer settings prints on its printer's
  paper and is now marked so (`PageLayout.PaperFromPrinter`), where it kept Letter whatever
  the printer held; one set for No Printer is formatted for the screen and keeps the page it
  stores, where an A4 printer's paper replaced it.
- **A group in original order prints its records as they come, as Crystal prints it**: a new
  instance starts wherever its value or a group's above it changes, so one value can head
  several instances, and the groups inside it and the record sort fields sort nothing, since
  they come after it in Crystal's list of sort keys. The engine sorts the rows by every
  group's value, so each value was gathered into one instance and the groups printed in
  alphabetical order: a statement listed in posting order printed its departments sorted
  with every department's records together, on fewer pages than Crystal's. The renderer now
  numbers each group's runs in Crystal's order (`TableJoiner.AddGroupRuns`) and the group
  breaks and sorts on the number first; data without the column groups on the value as
  before.
- **A report on Ledger paper in portrait prints on a page 17 inches wide and 11 tall, as
  Crystal prints it.** Ledger is Tabloid's sheet the other way round: 17 by 11 in portrait.
  The paper's orientation was applied by putting the shorter side across for portrait, so a
  portrait Ledger report printed on an 11 by 17 inch page, its columns past the right edge
  and half the page empty below them. Printer settings that give a paper and an orientation
  now print the paper as it is stored for portrait and turned for landscape.
- **A growing field whose line is a whole number of pixels grows to that line, as Crystal's
  does**, and a string ending in a carriage return prints no line after it. The engine reads
  an 11.25pt line, exactly 15 pixels, a little over and measures it 16, so every row of a
  listing in Arial 9 was 12pt where Crystal's is 11.25pt, and the rows drifted down the
  page. A formula's last Chr(13), alone or before a Chr(10), is a line break to the engine
  too, and an address ending in one printed a line taller than Crystal's.
- **A subreport moves nothing else in its section when it grows**, as Crystal prints it: a
  bill's usage graph, run as a subreport, prints beside the charges listed below its top. The
  engine moved everything below the subreport in its band down by all of its growth, so the
  charges printed below the graph and the rest of the bill a third of a page lower. Its frame
  is now held in a frameless Rectangle down to the band's bottom, as a framed growing field's
  is. A second subreport placed under a first keeps its place as well: the Crystal runtime
  prints it over the first's rows once they reach it.
- **A picture in a section set to underlay the sections after it prints behind them, however
  many it runs down past**, as a bill's watermark does. One running past the first section
  over it was left in a row of its own, which filled most of the page and pushed the bill
  down; it now goes into the row of the section it ends in, from above that row's top, and
  is drawn beneath everything else on the page. A grouped report with no detail columns now
  takes its underlays too, where every underlaid section printed as a row of its own.
- **A chart's second group-by draws a bar for each of its values**, as Crystal's "subdivided
  by" does, listed in the legend: a usage graph on change of month, subdivided by year. It
  was written as a second level of categories, and the engine drew no bars at all.
- **ToText of DateValue, CDate or Date prints the date alone**, the machine's short date, as
  each gives a Date, where of a date and time it printed a midnight after the date.
- **A letterhead whose name and address are in different sizes prints each line in its own
  size, as Crystal does.** A text object that may not grow, whose lines are each in one
  font and short enough not to wrap, is written as one textbox for each run of lines in the
  same font, stacked at each font's line pitch. A textbox has one font, and the whole
  letterhead printed in the address's 10pt under an 18pt name. A label typed with a final
  Enter, or ending in a line of tabs, in another font prints in its text's font: a column
  heading in 12pt with an empty 8pt line after it printed in 8pt.
- **A letterhead line that fits its box prints in its own size, and so does a letter whose
  last paragraph wraps, as Crystal prints them.** Whether a line fits is now measured in its
  font's own character widths, as the engine sets it, against the room the engine breaks
  lines in; a generous estimate kept a cheque's 11pt bold name, which fits, in one textbox
  with its 10pt address. The widths of seven common families are part of the converter, so
  a report converts the same whatever fonts the machine has. The last run of lines keeps the rest of the box, so its paragraphs may wrap, where
  a letter's 12pt heading printed in its body's 10pt. A final run of tabs in another size no
  longer keeps a certificate's text in one textbox not broken where Crystal breaks its lines.
- **A formula reads the column of the table it names where another table has a column of
  the same name**, as a group already did. A certificate's owner address, a formula of the
  customer table's Address1 beside a property table's ADDRESS1, printed the property's
  address in its place.
- **A number embedded in a text object prints in its own number format**: "owing
  ${@Total}" prints "$5,096.00", where it printed "$5096". The embedded field's number
  records are read as a placed field's are.
- **A report header suppressed outright takes no room, and a report that reads no table
  prints its report footer below its Details.** A suppressed header that needed a data
  scope was written as a hidden table, whose height the engine closed up, and a loan list's
  rows rose into the blank page header above them. A letter's report footer was set under
  its report header, so its signature printed across the first lines of the letter.
- **A subreport prints nothing past the right edge of the object it is placed in**, as
  Crystal cuts it there: a receipt laid out across the page, placed in a subreport 2.6in
  wide, prints its first column and the start of its second. Every column printed across the
  page beside it. An object that starts past the edge is left out and a line or box that runs
  past it ends there; text that runs past it still prints whole.
- **A subreport in one of a group band's cells prints where Crystal places it**, at its own
  depth in the band and as wide as its object. Written straight in the cell, the engine
  counted the page's left margin twice, and the subreport printed a margin's width to the
  right, as wide as its column; it is now written in a frame, as a free-form band's is.
- **A group's caption drawn at the band's left keeps its place over a field suppressed under
  the first column**, as the suppressed field prints nothing there. The caption gave up the
  lead column to it, so a customer's name, with the customer's address fields hidden beneath,
  printed one column to the right of where Crystal prints it.
- **A text field at Crystal's default alignment prints at the left of its object, whatever its
  values hold**, as Crystal goes by the field's type. RDL's General goes by the value the
  engine is handed, so a text column whose values were all digits, item codes or account
  numbers, printed at the right with the numbers, away from its heading. Such an object is
  now written Left, as a date at the default already is, and so is an on-demand subreport's
  caption, an account number in a trial balance's first column, say.
- **An address built line by line in a Shared variable prints, starting from what its reset
  formula left.** A Basic-syntax formula that adds each line that is not blank to a Shared
  variable reads the variable before writing it; another formula empties it, often in a
  suppressed page header, whose formulas Crystal still evaluates. Read as an earlier
  evaluation's leftover, the address printed nothing, and a section suppressed when it is
  empty never printed. A group footer's list kept in a variable printed nothing too when its
  formulas named each other in a comment ("//{@reset} goes in the group header"), which was
  taken for a use elsewhere.
- **A string's last line break prints no line**, as Crystal prints it: the Crystal runtime
  exports "a" + chr(10) as one line and "a" + chr(10) + chr(10) as two. The engine starts a
  line after every line break, so an address ending in line breaks, or a footer's list of a
  line per record, grew a line taller than Crystal's and pushed everything after it down.
  A string formula that writes a line break now prints without its last one.
- **A field whose text is empty draws no border**, as the Crystal runtime draws none for "",
  where it does for " ". A bill's comment line, ruled above for when there is a comment,
  drew its rule across every bill without one.
- **A group header's column labels print over their own columns, and a header holding only
  objects suppressed whatever the data prints no group name.** A further header at a group
  level wrote its labels into the table's cells in the order the file lists them, so a
  heading listed out of turn printed in another column ("Amount" beside "Issued", with the
  amounts at the right), and its rule was dropped; such a header is now laid out as Crystal
  places it. A header whose only field is suppressed printed the group's value in bold and
  the suppressed field beside it.
- **A growing field drawn shorter than its line takes the line, as Crystal's does.** Crystal
  makes a growing field or text object that holds any text at least one line at its pitch
  tall: through the runtime, a field of Arial 8 drawn 180 twips tall makes its section 210
  twips in a report set for no printer, and 185 in one set for a printer. Such a box was
  measured at the engine's own line with the room below it in its section added, so every row
  of a listing grew a little and the rows drifted down the page, while a group header that
  grows in Crystal did not grow. It is now set at Crystal's pitch, and a row grows to the line
  only where the line reaches past it.
- **A number whose Currency Symbol is a formula prints the symbol the formula gives**, a
  parameter's text, say, or none where it is empty; it printed the "$" its format holds. A
  Currency Symbol Type formula picks the format with a symbol, without one or floating, where
  it reads no Next or Previous (the engine reads every earlier row for each).
- **A group header's field that is no detail column prints where it was drawn.** It filled the
  first empty cell after the group's name, so a ledger's opening balances, drawn under its
  amount columns, printed under the first columns after the name; such a header is now
  written with every object in its own place, as a group footer already was.
- **A group on one table's column prints one group for each of that column's values, where
  another table has a column of the same name**, as Crystal groups on the table the group
  names. The DataSet held one field for the name, the first table's, so an invoice grouped
  on its invoice table's customer code and printing its customer table's was split into one
  invoice per customer row wherever the two tables were not linked, its totals each a part
  of the whole. The column is found where the file names it by column alone, and where it
  lists the name for the other table only. A report header whose every object is suppressed
  and that is set to Suppress Blank Section takes no room, where it took its height above the
  page header, and a group header whose objects all sit across from its first column prints
  no group name of its own, where it printed the group's value over the first label. A
  Details field printed under another and reaching across it, an invoice line's remarks
  under its number, keeps its own width, where it was held to the few twips between the two
  fields' left edges and broke after every word.
- **A date, time or date and time at Crystal's default alignment prints at the left of its
  object**, as Crystal prints it. The converter left its alignment unset, which the engine
  reads as RDL's General, and the engine after 27.0.0-alpha.6 resolves General as the RDL
  specification does, putting a date at the right with the numbers: a list's dates printed
  against the right edge of their boxes, away from their headings. Such an object is now
  written Left. A number at the default is still written General, which puts it at the right
  as Crystal does.
- **A running total counts, averages or takes the largest value as Crystal's does, and adds
  only the records its formula picks whatever it does.** Its operation was read from the wrong
  place in the file, so every running total was a sum: a count of a text column printed blank,
  and an average printed a sum. It is now read where the file records it, and agrees with the
  Crystal runtime's for every running total in all three corpora. A count with an evaluate
  formula gave the records the formula leaves out as Nothing in the second branch of an IIf,
  which the engine converts to the first branch's type, so a count of a number printed blank;
  Nothing now comes first. A distinct count counts each value once, and a maximum, minimum or
  average with an evaluate formula takes only the records it picks, an average as their sum
  over their count; all three took every record.
- **A formula's If with no Else gives a number field's 0 where its test fails**, as Crystal's
  does. It gave Nothing, which the engine failed to convert to the field's type, so the
  formula printed blank and a total of it failed.
- **A balance-forward statement prints its opening balance and its running balance as
  Crystal does.** NthLargest(1, x, {group}) is the largest in the record's instance of that
  group, as a summary over a group is, where it was the largest in the whole report: every
  account's statement opened at the largest balance forward of all of them. A running balance
  kept in a variable, set to the opening figure in the account's group header and added to in
  the details or in an inner group's footer, now prints the opening figure plus the amounts so
  far; the variable made it print nothing. A group header whose first object is a formula
  prints the formula, where its cell printed the group's own field. A section suppressed by its
  own checkbox stays suppressed under its area's suppress formula, as the Crystal runtime lists
  that formula on the area and none on the section; read as the section's own, superseding the
  checkbox, two empty spacers printed and pushed the statement down half an inch. An empty
  page header under a suppressed one is the gap above the details, as a lone one was, and one
  suppressed by a formula is a row hidden where the formula is true; without them a statement
  printed 0.95in above Crystal's.
- **Next and Previous in a suppress or new-page formula are the records Crystal prints after
  and before the current one**, across groups, and a condition that reads one past the last
  record or before the first is false. Both walked the rows in the order they were read, or
  stopped at the end of the group, so a section suppressed until the last line of a source
  printed under every line once the rows were not already in order. A group's New Page Before
  or After formula reading Next, Previous, OnFirstRecord or OnLastRecord was dropped for a
  break after every group, the last one too, which put the report footer on a page of its own:
  the condition is now kept, read on the group's first or last record as Crystal reads it, and
  "Not OnLastRecord" on a header that prints nothing starts no page after the last group.
- **A row that grows ends where its tallest object does**, as Crystal grows a section to the
  bottom of the object that grew rather than by what it grew. The room a table cell had below
  a growing field was added to it: a list's three-line addresses, in 9.25pt boxes on 11.05pt
  rows, made rows 29.05pt tall where Crystal's are 27.75pt, and the list drifted a third of an
  inch by the bottom of the page.
- **A group section with nothing in it takes its height, and a line or box in a group header or
  footer is drawn**, as in Crystal. A second section at a group's level with nothing to put in
  a table cell wrote no row at all, so a listing's blank spacer under each account's address
  was gone, and a rule under each group, with nothing but the rule in its section, was
  dropped. A section set to Suppress Blank Section, now read from the file
  (`Section.SuppressBlank`, which agrees with the Crystal runtime's for every section it lists
  in all three corpora), still takes nothing when it is empty.
- **A label that may not grow, in a box one line tall, prints on its one line**, as Crystal
  sets it there and cuts it off at the box's edge rather than breaking it into lines. It was
  broken where a taller box breaks, and the one line kept only its first words: "Last Three"
  of a three-word label Crystal prints all but the colon of.
- **A running total evaluated on a change of group adds the first record of each instance of
  the group**, as in Crystal, where it added every record: a vendor's open balance, repeated
  on each line of an invoice and totalled once per invoice, came to the sum of every line's.
  An instance starts where the group's value or an outer group's changes, a date group's with
  each of its intervals, so an invoice in a new month counts again, and a detail line already
  shows its instance's first record. This holds for a sum, count or average that starts again
  with a group. One that runs over the whole report still adds every record: telling each
  record from the one before it across the whole report costs the engine a read of every
  record before it, each time a group footer reads the total. So does a maximum or minimum,
  a total that starts again inside the group it is evaluated on, and one on a group in a
  specified order.
- **A number or a date a formula turns into text prints as Crystal's ToText prints it.**
  Given the decimals, ToText(x, 2) still groups the thousands, "1,045.00 * 1,147.00",
  where it printed "1045.00 * 1147.00". ToText of a number formula or parameter prints two
  decimals, as of a number column, and so does ToText of a number column gathered into a
  print-time variable, which printed "586" for "586.00". ToText of a Date - a column,
  parameter or formula with no time of day - prints the machine's short date and nothing
  else, where it printed a midnight after it.
- **A Date parameter or formula prints its date and no time**, placed on the page or set in
  a text object, as a Date column does: an invoice date printed "2024-01-01 12:00:00"
  where Crystal prints "2024-01-01".
- **Print Time, Data Time and Modification Time print in their object's time record**, as
  in Crystal, which uses it on the machine's formats too: a listing's "Printed: 3:05:07PM"
  printed the machine's long time, "3:05:07 PM".
- **IsNull of a picture field is still True where the report converts database nulls**, as
  in Crystal, which has no default for a picture. Since nulls read as defaults, IsNull of
  every database field was False, so a section or image hidden where a record holds no
  picture printed anyway, as an empty band that could push the page onto a second one. A
  column a database image shows keeps the null test.
- **A formula's summary over a group, Sum({T.Amount}, {T.Account}), is that group's total**,
  as Crystal gives it for the record the formula is evaluated on: the account's total on
  each of its lines and in its footer, and the last account's in a footer of the group
  around it. The group argument was dropped, so a formula, which becomes a calculated field,
  summed every record in the report: a bill's current charge and amount due came to the
  whole run's. Count, Average, Maximum, Minimum and DistinctCount over a group do the same.
  A group on a date's interval, Sum({T.Amount}, {T.Posted}, "monthly"), still sums the
  whole report.
- **A line of text breaks where Crystal breaks it.** Crystal measures a line, to decide
  where it breaks, in a font whose em is the object's point size, though it draws the
  smaller em GDI gives that size; so its lines stop about a tenth short of the right edge.
  The engine measured the font it draws against the whole box, and kept on one line what
  Crystal breaks: 8pt bold column headings 58.5pt wide printed on one line where Crystal
  prints two, and a list's growing name column set "Lee Ann Smith Jones" on one line where
  Crystal ends the line before "Jones". A text object and a text field that can grow are
  now given the room Crystal measures against, whether placed on their own or in a table's
  cells, taken off the side away from their alignment: from the font's hinted 96-dpi widths
  for a report set for no printer, and its 600-dpi ones for a report set for a printer. A
  table cell keeps the padding that holds its text inside its field, and is narrowed only
  by what its column leaves beyond Crystal's room. Against the Crystal runtime's breaks in
  random text of five families from 7 to 14pt, 4% of the engine's break decisions now
  differ from Crystal's for a report set for no printer and 1% for one set for a printer,
  where 27% did.
- **A number prints with the currency symbol, negative format and zero text its object
  sets**, as in Crystal. The numeric format record was misread: what was taken for "show
  the symbol" is the negative format, and what was taken for "apply the symbol and grouping"
  is the thousands separator alone. So every column that stores a "$" and does not show one
  printed it - an invoice list's balances, a budget's amounts - and every amount with a
  symbol ended in a space where Crystal leaves one only for the ")" or "-" of a bracketed
  or trailing-minus negative: a receipt's tendered amount stood a space short of Crystal's.
  Negatives now print bracketed or with a trailing minus, a symbol goes before or after the
  number and inside or outside the sign, zero prints as the report's text for it ("-",
  "NIL") or not at all, and a currency value takes the object's currency format rather than
  its number format, as Crystal keeps one of each. The record's options agree with the
  Crystal runtime's for every number object read from both corpora but 28 currency fields,
  which the runtime reports with a leading minus their records do not hold.
- **A band holding a suppressible line or empty picture prints where Crystal prints it.**
  The empty box that holds such an object's space stood at the object's own place, and the
  engine, which takes a band's items in order of top and left, could take the hidden object
  first and place the box, and everything below it, as though the band began at the top of
  the page: a statement printed its whole page a band's offset too high once an unrelated
  object was left out. The box now starts a twip above an object of no height.
- **A growing box one line tall keeps its row's height**, as in Crystal. The engine measures
  a line in whole pixels, so a box exactly one 9.25pt line tall measured 9.75pt and grew, and
  every row of a list grew half a point. The difference now comes out of the padding below
  the line, which the text does not reach.
- **A running total that starts again when a field changes starts again with each group on
  that field**, as in Crystal, where the record on which the field changes is the first of the
  new run: a statement's interest for each property, or a ledger's running debits for each
  account. So does one whose reset formula only asks
  whether fields changed from the record before (`{T.F} <> Previous ({T.F})`, or several
  joined by Or). Both ran on over the whole report. A reset on a field the report does not
  group on, or on any other formula, still runs over the whole report, since RDL starts a
  running value again only at a group.
- **Text is set for the engine that places a line at its font's ascent**, the Reporting
  engine that draws each line's baseline its font's hhea ascent below the top of the box
  and sets lines without a LineHeight at the font's own pitch, where the engine before set
  both at the font size. The converter had placed text for that older engine, and on the
  new one nearly every report's text sat a point or more too high: the private reports
  scored 55.5 on average where they had scored 59.8, and 13 of the 14 public visual tests
  failed. They now score 67.3 and all 14 pass. The engine package moves to Majorsilence.Reporting
  27.0.0-alpha.6, the first with that engine; on 27.0.0-alpha.5 they score lower, as it places
  lines the old way.
- **Every field and text object sets its first line at Crystal's depth below its top**, in
  every family the converter holds metrics for. Crystal sets it at the font's ascent as GDI
  gives it with the point size as the em: a field's in twips whatever the report is set for,
  a text object's in whole pixels of the printer, or of the screen in a report set for No
  Printer. Against the engine's hhea ascent at the em it draws, a 10pt Arial field sat
  0.85pt high, a Tahoma one 1.7pt, a Calibri one 3.35pt and an Arial label on the screen
  1.65pt. Each now agrees with the Crystal runtime's PDF export within a tenth of a point, in
  Arial, Times New Roman, Tahoma, Verdana, Courier New, Calibri and Cambria from 5 to 24
  points. The box holds as many lines as before and grows no taller for it. A text in more
  than one font, which the engine draws all in its last font, is placed for the font most of
  it is in.
- **A box that may not grow prints as many lines as Crystal prints**: the first, and each
  after it that starts within the box at a fifth more than the pitch, or ends within it - two
  lines of 12pt in 14.4pt, seven in 84pt. The engine printed only the lines whose letters fit
  whole below the line before, so a form's two-line labels lost their second line.
- **A report that names no printer, and is not set for No Printer, is formatted for the
  default printer**, as Crystal formats it: at the printer's line pitch and with its text
  objects laid out, and its lines broken, at the printer's resolution. It was taken for a
  report set for No Printer and set for the screen, which only a report that says so (in
  record 5) is. That is 451 private reports and 89 public ones.
- **A formula whose first branch is a whole number keeps its other branches' fractions**, as
  Crystal, whose numbers are all doubles, does: `if {x} = 0 then 0 else {a} / {b} * 100`
  prints 115.43 and `if IsNull({x}) then 0 else -{x}` prints -853.75. The engine took the
  formula's type from the 0 and rounded every other branch to a whole number, 115 and -854.00,
  in an If, a Select and Crystal's own IIf and Switch.
- **IsNull of a null number is true, and a formula that reads one prints nothing, where the
  report keeps database nulls**, as in Crystal. The engine reads a null number as NaN, which
  the test took for a value: the IsNull branch was never taken and the formula printed what
  it made of the NaN.
- **A number formatted with a "%" symbol prints it**: a variance prints "115.43%" where it
  printed the bare number with every digit it had.
- **A report on the default margins sits where Crystal puts it on the page**: a quarter inch
  in from each edge for a report set for No Printer, which prints to the screen driver, and
  a sixth of an inch for one set for a printer. The default was read from the printer
  settings' orientation, a quarter inch for portrait, which put about one report in five on
  the default margins 6pt right of and below Crystal's page, or left of and above it: a
  cheque's every line and box sat a sixth of a line low and missed Crystal's. Whether the
  report is set for No Printer is read from the file (record 5), and the Crystal runtime's
  margins follow it for 970 of 985 reports on the default margins; the other 15 are set for
  a printer the runtime's machine has, and take that printer's.
- **A date and time on the machine's formats whose field prints the date alone prints the
  machine's short date**, as Crystal does: an appointment's end printed "2024-07-17 4:00:00"
  where Crystal prints "2024-07-17". The date-time record's "date only" was read only for a
  field on its own formats.
- **The visual suite reads a fixture's date and time as a date**, where it read only a date
  without a time. A column holding "2024-01-20 13:00:00" stayed a string, which no date
  format changes, so a field that Crystal prints "01/20/2024" printed the fixture's text.
- **A page footer holding fields or a subreport prints at the foot of every page**, as
  Crystal prints a page footer. RDL's PageFooter can hold neither, so the footer goes in the
  table's Footer, which printed it once, straight under the last row: a receipt's duplicate
  copy sat 25pt high, and on some the footer did not fit page 1 and took the totals with it
  to page 2. Its rows are now marked to print at the foot of each page the table is on
  (`FooterRowsAtBottomOfPage`), with their room kept free on every page. An engine without
  that setting ignores it and prints the footer as before.
- **A field whose suppress formula was commented out stays suppressed when its Suppress box
  is ticked**, as in Crystal, where a formula of nothing but comments decides nothing. The
  formula was taken to decide, and the box was not read: a receipt printed the customer
  address block its author had switched off.
- **A date whose Date Order formula reads a parameter prints in the order it picks**: a
  receipt's date printed "Jan 01, 2024", the order its record holds, where Crystal prints
  "2024 Jan, 01" for year-month-day. The formula is read and the date's format chosen from
  its value when the report runs.
- **A number parameter placed on the page takes its object's number format**, as a column
  does: a receipt's tendered amount and change printed "0" where Crystal prints "$0.00".
- **A formula that reads a null field prints nothing where the report keeps database nulls**,
  as in Crystal. With "Convert Database NULL Values to Default" off, Crystal stops a formula
  at the first null field it reads, unless the formula tests it with IsNull first or uses
  "Default Values For Nulls", and so do the formulas that read that one. The null was read
  as "" or 0 instead: a notice's name and address, one of its columns null, printed seven
  lines where Crystal printed none, and grew its band by them. The report's two null options
  and each formula's null treatment are read from the file and agree with the Crystal
  runtime's for every report and every formula tested that still compiles.
- **A null field reads as its type's default where the report converts database nulls**, as
  the Crystal runtime reads it with "Convert Database NULL Values to Default" on: "" for a
  string, 0 for a number and False for a boolean, whether the table holds the null or a
  record has no match across an outer join. IsNull of a database field is False, a null
  number prints 0.00, a formula adds it as 0, and Count, Average and Minimum take it as a
  value. The null stayed a null instead: `if IsNull({x}) then "A" else "B"` printed "A"
  where Crystal prints "B", a number plus a null printed nothing, and an average skipped
  the record. A null date still prints blank, as Crystal prints its default date.
- **A report set for a printer sets its lines at the printer's pitch**, as Crystal does:
  Arial 8, 9 and 10 at 9.25, 10.2 and 11.4pt, and Times New Roman 9 at 10.55. Every report
  was set at the screen's pitch, which Crystal uses only for a report set for no printer, and
  lines in the rest came out up to a point and a half further apart each: a three-line 9pt
  address took 36pt where Crystal fits it in 31.65, and every row it was in grew. The printer
  the report is set for is read from the file (`ReportDefinition.PrinterName`), and a
  subreport takes its main report's. The pitch is the font's ascent, descent and leading at
  600 dpi, which agrees to the twip with every pitch measured through the Crystal runtime.
- **A growing field or text object prints no more lines than its "maximum number of lines"**,
  and its band grows no further for it, as in Crystal. The limit was not read, and a
  five-line name and address that ran to seven printed all seven and pushed the rest of the
  band down. It is read for fields and text objects alike, and agrees with the Crystal
  runtime's for nearly every growing object of the corpora.
- **An on-demand subreport prints its caption and nothing of its content**, as Crystal does:
  its caption formula's value, or with none its report's name. It ran in full where it was
  placed, and a trial balance's per-account detail made every account's row a dozen lines
  tall. The on-demand flag and the caption formula are read from the file and agree with the
  Crystal runtime's for every placed subreport of the corpora.
- **A framed growing object moves nothing below it in its band when it grows**, as Crystal
  draws it over what is there. It pushed everything below it down by its growth: a notice's
  framed account details, grown four lines, moved the paragraph beneath them 65pt down.
- **A growing object's room ends at its band's own bottom**, not at the bottom of a line drawn
  from the band down into a later section. A purchase order's column rules gave its addresses
  room 54pt past the band, and every band was that much taller.
- **A statement, notice or letter set to start each group on a new page prints one group to
  a page**, as Crystal does, where page 1 ran several together. New Page Before and After
  set on an area as a whole, rather than on one of its sections, were not read, and group
  footer areas and form letters' details areas commonly set them so. One set on a footer
  section other than the level's first was dropped, and so was a page-break formula on a group with no detail
  columns. The areas' settings agree with the Crystal runtime's for every area of all three
  corpora.
- **A report footer that starts a new page, and a report header that ends one, ask for the
  break on their band's row.** The engine breaks a table there from its next release; until
  then the footer still prints straight after the last group.
- **With no rows, a grouped report prints each group section and its Details once**, their
  fields blank, as Crystal does when no record is left: a collection letter laid out in group
  sections prints the letter. It printed a blank page, since a table group with no rows
  prints nothing. A summary in that pass prints nothing, as Crystal's does.
- **With no rows, an ungrouped report prints its Details once and a subreport its group
  sections and Details once**, their fields blank, as the Crystal runtime does. They printed
  nothing between the report header and footer. A group section written outside the
  details table, and an object lifted out of one, print in that pass too, where they were
  hidden. In that pass a formula that reads the data prints nothing and a colour formula on
  the record number colours nothing, as in Crystal; a formula of parameters still prints.
- **A group header holding only a picture, a box or a subreport prints no group name**, as in
  Crystal. One was made up for it, so a bill's banner printed its account code above the
  picture. An empty group header of no height takes no space, where it took a line, pushing
  a receipt's invoice lines a line down.
- **A text object prints its text when a field or parameter in it has no value**, the empty
  field printing nothing, as in Crystal. "Account: {@Code}" with no code printed nothing at
  all.
- **Sections hidden for drill-down no longer print.** A summary report that keeps its details
  (and often its group footers) for drill-down, rather than suppressing them, printed every
  detail row. The area-level "Hide (Drill-Down OK)" and suppress flags are now read, and
  agree with the Crystal runtime's for every report in the public and third-party corpora.
- **A suppressed report header takes no space.** It was not drawn but still pushed the rest
  of the report down by its height: four blank inches on Top5USA-piechart.
- **An empty Report Comments field prints nothing**, where it printed two quote marks.
- **A field placed in a group header or footer prints**, where the band's own cells did not
  write it. Only a field matching a detail column had a cell, so a formula there, or a
  database field the details do not show, was dropped.
- **A subreport is reached by the name Crystal gives it**, such as "CanadaOrders.rpt", as
  well as by its placed object's name. `SubreportData` and `SubreportParameters` naming a
  subreport as the Crystal runtime does were skipped when the report had more than one.
  `ReportEngine.Analyze` now reports the runtime's names.
- **A group section written outside the details table is hidden when there are no rows.**
- **A running total adds only the records its formula picks, and starts again where it is
  set to** (`RunningTotalField.EvaluateOn`, `ResetOn` and their conditions), where it added
  every record from the start of the report. A statement's interest, a total of the lines
  whose type is "Interest" reset for each property, was the sum of every balance, and the
  amount due came to nothing. A total reset on a change of group runs within that group.
- **Every font is the size Crystal draws it, to the twentieth of a point** (#23). A
  converted report's font sizes are now the ones Crystal's own PDF export writes. Bold text
  below about 15pt had been off by up to a third of a point, and 10pt Courier New by half a
  point. Across 78 public reports, 262 of the 306 distinct sizes Crystal writes now match
  exactly, against 137 before; nearly all the rest are chart, cross-tab and map text.
  Bold Arial at 10pt is now 8.75pt, where it was 8.95.
- **`crystalcli verify` can report an error** (#17). It looked for messages starting "Error"
  or "Fatal", which the engine never writes, so every file passed. All three corpora still
  verify clean with the working check.

**Changed**

- **The engine is Majorsilence.Reporting 27.0.0-alpha.4**, on Majorsilence.Forms 26.11.8.
  A pie's slices are filled as wedges, where each was a circle segment; a chart's category
  axis prints the category's label; a cross-tab total set to print first prints first; and
  a colour formula on the record number colours every row, where every fourth was missed.
  A doughnut chart now keeps its hole.
- **The engine moved to Majorsilence.Reporting 27 at 27.0.0-alpha.2**, a pre-release, and
  the package referenced is `Majorsilence.Reporting.RdlEngine` rather than its `.SkiaSharp`
  alias. A table with no rows now prints its header and footer, so a report rendered with no data
  prints its column labels and report header, and a subreport its report footer. A
  free-form subreport printed 16pt or more right of its place on this engine, its offset
  counted twice; the converter no longer repeats it.
- **`RuntimeOverrides.SortByFieldName` does what Crystal's callers do**: it regroups a grouped
  report on the field, replaces the first record sort of an ungrouped one, and warns, changing
  nothing, on a report with neither. It used to add a sort when the report had none.
- **Each RDL field's `DataField` names its table** ("Orders.Customer ID"), so two tables
  with a column of the same name no longer collide in the flattened dataset. The RDL field
  names, and so every expression, are unchanged. The engine adds the qualified names to a
  pushed table that has only the bare ones, for `Data` and `SubreportData` alike.
- **A converted report's query is its own.** It selects every column of every table and
  joins them as the links say, and its data provider is the one the report connects
  through (ODBC or OLE DB) rather than always SQL.

## v0.3.2

**Changed**

- **The engine is Majorsilence.Reporting 26.0.7.** Two of its fixes reach this package:
  - A subreport with no details prints when it has no rows. A subreport that shows the
    parameter values its parent passed was blank; it now prints them, as Crystal does.
  - On Linux and macOS, PDFs use the fonts installed there by the family they declare, so a
    report in Arial gets Arial where it is installed rather than Liberation Sans.

**Known gaps** are those of v0.3.1. A subreport with a Details section still prints nothing
when it has no rows; Crystal prints its headers.

## v0.3.1

**Fixed**

- **A placed subreport is framed, sized and stacked as Crystal draws it** (#37). The
  subreport object's border is read and drawn as its frame; the subreport prints at its own
  position rather than directly under the item above it; a full-width band in it ends at its
  edge rather than the page's; and a second report-header section prints below the first
  instead of over it, in any report. CrystalCmd's subreport-data scenario rises from 67.9%
  to 88.0% ink agreement against Crystal.

## v0.3.0

**Added**

- **Data for subreports.** `RuntimeOverrides.SubreportData` maps a subreport name to the
  one flattened table it renders from, as `RuntimeOverrides.Data` is the main report's.
  Names match case-insensitively; a name that matches no subreport is a warning. No engine
  change was needed: `RptEngine` hands each subreport its table as the engine loads it.
  Until Reporting #345, a subreport given no rows still draws nothing.
- `RdlConverter.Description`, and a `keyedDescriptions` option on
  `RenderPrep.ConvertWithSubreports`, which writes each subreport companion's file stem as
  its Description so a renderer can tell subreports apart while they load.

**Known gaps** are those of v0.2.1, except that data can now be pushed to a subreport.

## v0.2.1

**Fixed**

- **An empty page header still takes its height above a report's Details**, in a report
  that reads no table. 0.2.0 dropped it, which put CrystalCmd's plain sample's text 0.42in
  above Crystal's. A section suppressed outright is now neither drawn nor spaced there, as
  in Crystal. Private corpus: 2 reports have their Details placed lower.

## v0.2.0

Three fixes found by CrystalCmd's acceptance corpus, which renders the same requests through
the Crystal runtime and through this engine and compares the pages.

**Fixed**

- **A report that reads no table prints its Details once**, as Crystal does (#32). Such a
  report rendered blank: its Details went nowhere, or into a table that prints nothing over
  no rows. Its page header and Details sections are now body bands, and the formulas placed
  there are written as expressions. Private corpus: 11 reports with Details content that
  was blank now print it.
- **A section's background colour is drawn across its band** (#34). The colour is read
  from the section's properties and drawn on table rows, free-form sections, body bands and
  the RDL page header and footer. An object's own colour still wins.
- **A report that follows its printer prints on the paper its settings name** (#33). The
  page record's flag says whether the stored page is the template's own. When it is not,
  the template's printer settings name the paper and orientation. Against the Crystal
  runtime's own page, private templates go from 2,072 to 2,163 of 2,203 matching, and none
  that matched stops.

**Added**

- `PageLayout.PaperFromPrinter` marks a template that prints on its printer's default
  paper, and `RuntimeOverrides.PrinterPaper` lays such a report out on the paper a host's
  printer holds. Given A4, the paper of the machine the references come from, 2,201 of the
  2,203 private templates match Crystal's page.
- `Section.BackColor`.
- `ReportDefinition.Page` is settable.

**Known gaps** are those of v0.1.0, and one more: a subreport that reads no table carries
its bands but draws nothing, because the engine runs a subreport only when one of its
datasets returns a row ([Reporting #345](https://github.com/majorsilence/Reporting/issues/345)).

## v0.1.0 — first preview

The first published packages: `Majorsilence.Crystal.Model`, `.Parser`, `.Converter`,
`.Runtime`, `.RptEngine` and `.Cli`. A preview: the render path is measured on plain
tabular reports and structurally verified on everything else.

**What it does**

- Parses a Crystal Reports `.rpt` (OLE compound document, TSLV records) into a model:
  sections, fields, formulas, parameters, groups, running totals, objects with their
  positions and formats, subreports, charts, cross-tabs, embedded images.
- Converts the model to SSRS 2005 RDL: tables with group bands, page and report bands,
  formulas transpiled from Crystal and Basic syntax (a measured subset of the language,
  including the arithmetic, rounding and conditional semantics the Crystal runtime uses),
  parameters, subreport links, a first cross-tab axis and cell, single-series charts.
- Renders the RDL on the Majorsilence.Reporting engine (26.0.6, SkiaSharp build) to PDF,
  CSV, Excel (`.xlsx`) and RTF, with pushed data, parameters read by their declared type,
  and runtime overrides for formulas, record selection, sort, and object suppress, resize,
  move, text and can-grow. It never opens the connection a template names.

**What is measured**

- 11 public reports rendered with data score 86–99% ink agreement against Crystal's own
  PDF output. Subreports, cross-tabs and charts have no data fixtures and are not measured.
- Every report in three corpora (88 public, 114 third-party, 2,324 private) parses,
  converts and loads in the engine with 0 errors; a deliberately broken control still
  flags 87 of 88.

**Known gaps** (see ROADMAP.md, Stages 2 and 3)

- One flattened data table per report; the file's table links are not decoded, so a
  multi-table report needs its data joined before it is pushed.
- No data can be pushed to a subreport.
- The report's own sort order is not decoded (`SortByFieldName` supplies one).
- Formula-driven page breaks on the Details section, Crystal variable declarations, and
  the export types with no engine equivalent (the template itself, plain text, Word) are
  not expressed.
