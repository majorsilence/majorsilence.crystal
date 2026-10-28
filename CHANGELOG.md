# Changelog

Release notes, newest first. [BACKLOG.md](BACKLOG.md) has the measurements behind each
line; [ROADMAP.md](ROADMAP.md) has what comes next.

## Unreleased

**Added**

- **A multi-table report renders from one table per Crystal table** (#36). Push each table
  under its name in `RuntimeOverrides.TableData`, and the engine joins them as the report's
  own links say: inner and left-outer joins on equal keys, in the order Crystal applies them.
  A report renders the same from per-table data as from the table a caller joined already,
  and a caller that pushes one pre-joined table through `Data` keeps working unchanged.
- **The report's tables and links are read from the file.** The `QESession` stream, the
  QueryEngine's saved session, is now decrypted and decoded. It gives the parsed report its
  data source, every table with its columns and alias, and its links
  (`ReportDefinition.TableLinks`); `ReportEngine.Analyze` lists them too. They agree with
  the Crystal runtime's own for every table and link in all three corpora: 79 links in the
  public one, 166 in the third-party one and 648 field pairs in the private one.

- **A section's Keep Together and a group's Keep Group Together are read from the file**
  (`Section.KeepTogether`, `GroupDefinition.KeepTogether`), where neither was. They agree
  with the Crystal runtime's for every report in all three corpora: 243 sections that turn
  Keep Together off and 319 groups that keep together. Nothing prints differently yet: a
  band of fixed height already moves whole to the next page as Crystal's does, and one that
  grows needs the engine to measure it first.
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

**Fixed**

- **IsNull of a picture field is still True where the report converts database nulls**, as
  in Crystal, which has no default for a picture. Since nulls read as defaults, IsNull of
  every database field was False, so a section or image hidden where a record holds no
  picture printed anyway, as an empty band that could push the page onto a second one. A
  column a database image shows keeps the null test.
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
