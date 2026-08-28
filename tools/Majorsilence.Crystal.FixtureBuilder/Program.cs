// Turns Crystal's "data only" Excel export into a visual-regression data fixture.
//
// This is the second half of a two-step, dev-machine-only pipeline. The first half needs
// the real Crystal runtime and so lives in the net48 ReferenceRenderer; this half needs
// our own parser and so lives here:
//
//   ReferenceRenderer --xls  <report>.rpt  <report>.xls
//   FixtureBuilder           <report>.rpt  <report>.xls  tests/reference-data/<report>.csv
//
// Why the Excel export rather than the CSV one the first fixture came from: Crystal's CSV
// export writes *rendered* rows, so every line carries the whole report line - headers,
// labels, detail values, footers - and the detail columns are only recoverable when the
// report is a plain list. The data-only Excel export writes a cell grid instead, which
// survives grouping.
//
// What it still cannot do: the export contains what the report *displays*. A report that
// suppresses its detail section and shows only group summaries or a cross-tab exports
// those summaries, and the underlying rows are nowhere in it. Those reports need the rows
// saved inside the .rpt itself, which is a separate unsolved problem (see BACKLOG).
//
// The output is committed, so read the summary this prints before committing it: the row
// count and the first row are exactly the things a mis-parse gets wrong.

using System.Globalization;
using System.Text;
using Majorsilence.Crystal.Model.Fields;
using Majorsilence.Crystal.Parser;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: FixtureBuilder <rpt-path> <xls-path> <out-csv-path>");
    return 1;
}

string rptPath = args[0], xlsPath = args[1], outPath = args[2];
foreach (var p in new[] { rptPath, xlsPath })
{
    if (File.Exists(p)) continue;
    Console.Error.WriteLine($"Not found: {p}");
    return 1;
}

// ---------------------------------------------------------------- field list
var parsed = RptParser.Parse(rptPath);
if (!parsed.Success || parsed.Report is null)
{
    Console.Error.WriteLine("Parse failed");
    return 1;
}

var fields = parsed.Report.Fields.OfType<DatabaseField>().ToList();
if (fields.Count == 0)
{
    Console.Error.WriteLine("No database fields; nothing a fixture could hold");
    return 1;
}
Console.WriteLine($"fields ({fields.Count}): " +
    string.Join(", ", fields.Select(f => $"{f.Name}:{f.DataType}")));

// ------------------------------------------------------------------ the grid
var grid = Majorsilence.Crystal.FixtureBuilder.Biff.ReadGrid(xlsPath);
if (grid.Count == 0)
{
    Console.Error.WriteLine("No cells in the export");
    return 1;
}

// Each exported row is left-compacted - a group subtotal alone on its line lands in
// column 0 regardless of which column it is printed under - so a row's own values are
// ordered but its column indices mean nothing. Compact to a list per row and work from
// the shape of the values instead.
int maxRow = grid.Keys.Max(k => k.Row);
var rows = new List<List<object>>();
for (int r = 0; r <= maxRow; r++)
{
    var row = grid.Where(kv => kv.Key.Row == r)
        .OrderBy(kv => kv.Key.Col)
        .Select(kv => kv.Value)
        .ToList();
    rows.Add(row);
}

// A detail row is one that has a value for every field and whose values are the right
// *kinds* of value. That is what separates it from the label row above it, which is the
// same width but all text where the detail row has numbers - and it needs no knowledge of
// what the labels say, so it is not defeated by a label that reads like a field name.
static string Shape(IEnumerable<object> vals) =>
    string.Concat(vals.Select(v => v is double ? "n" : "s"));

// A cell holding a field's own name is a column label, not a value. Some report shapes
// repeat the page-header labels on every exported line, and those rows are the same
// width and the same all-text shape as a detail row - so the shape vote alone elects
// them and the fixture comes out holding the words "Order Amount" where every amount
// should be. Half the cells matching a field name is far past coincidence; one product
// genuinely called "Color" cannot reach it.
var fieldNames = fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
bool LooksLikeLabelRow(List<object> r) =>
    r.Count(v => v is string s && fieldNames.Contains(s.Trim())) * 2 >= r.Count;

var fullWidth = rows.Where(r => r.Count == fields.Count).ToList();
var candidates = fullWidth.Where(r => !LooksLikeLabelRow(r)).ToList();
if (fullWidth.Count != candidates.Count)
    Console.WriteLine($"ignored {fullWidth.Count - candidates.Count} full-width row(s) whose " +
        "cells are this report's own field names rather than values");

if (candidates.Count == 0)
{
    Console.Error.WriteLine(
        $"No exported row has {fields.Count} values that are not column labels. Widths present: " +
        string.Join(", ", rows.Select(r => r.Count).Distinct().OrderBy(x => x)) +
        ". The report probably suppresses its detail section, in which case the rows are " +
        "not in the export at all.");
    return 1;
}

// A BIFF row omits an empty cell, so a detail row carrying a null exports one value
// short and never reaches the candidate list at all. Nothing here can tell such a row
// from a header or a group line, so the count is reported rather than guessed at: a
// fixture quietly missing a third of its rows renders a shorter report than the
// reference it is measured against, and that gap reads as a layout fault.
int shortRows = rows.Count(r => r.Count > 0 && r.Count < fields.Count);
if (shortRows > 0)
    Console.WriteLine($"NOTE: {shortRows} exported row(s) hold fewer than {fields.Count} values. " +
        "Some are the report's own header and footer lines; any that are detail rows carrying " +
        "a null are NOT in this fixture. Check the row count against the report before committing.");

var byShape = candidates.GroupBy(Shape).OrderByDescending(g => g.Count()).ToList();
string detailShape = byShape[0].Key;
Console.WriteLine("row shapes at full width: " +
    string.Join(", ", byShape.Select(g => $"{g.Key} x{g.Count()}")));
Console.WriteLine($"taking '{detailShape}' as the detail rows");

var detail = byShape[0].ToList();

// -------------------------------------------------------------------- output
// Excel keeps dates as a day count from 1899-12-30. Left as a number, a date column
// formats as "37037" and the comparison is against a rendered date, so convert the
// columns the report itself calls dates.
static bool IsDateField(DatabaseField f) =>
    f.DataType.Contains("date", StringComparison.OrdinalIgnoreCase);

var epoch = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

var sb = new StringBuilder();
sb.AppendLine(string.Join(",", fields.Select(f => Quote(f.Name))));
foreach (var row in detail)
{
    var cells = new List<string>(fields.Count);
    for (int i = 0; i < fields.Count; i++)
    {
        object v = row[i];
        if (v is double d && IsDateField(fields[i]))
            cells.Add(Quote(epoch.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        else if (v is double n)
            cells.Add(Quote(n.ToString("R", CultureInfo.InvariantCulture)));
        else
            cells.Add(Quote(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""));
    }
    sb.AppendLine(string.Join(",", cells));
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, sb.ToString());

Console.WriteLine($"wrote {outPath}: {detail.Count} rows");
Console.WriteLine("  header: " + string.Join(" | ", fields.Select(f => f.Name)));
if (detail.Count > 0)
    Console.WriteLine("  first:  " + string.Join(" | ", detail[0].Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
return 0;

static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
