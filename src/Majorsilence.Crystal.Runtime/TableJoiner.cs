using System.Data;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Runtime;

/// <summary>
/// Builds the one flattened table the converted RDL's <c>DataSet1</c> reads from, out of
/// per-table data joined as the report's links say, or out of a table the caller joined
/// already. Every column of the result is named "Table.Column", which is what the converter
/// writes as each field's <c>DataField</c>; a column no other table shares is also exposed
/// under its bare name, for a field whose table the file did not record.
/// </summary>
public static class TableJoiner
{
    /// <summary>
    /// Joins the caller's per-table data with the report's links, in link order. The first
    /// pushed table in the report's own table order starts the result; each link in turn
    /// brings in the table it reaches, inner or outer as recorded, or, when both its tables
    /// are in already, keeps the rows that satisfy it. A table no link reaches is
    /// cross-joined, as Crystal queries it. Tables the caller did not push are reported and
    /// left out, with the links that need them.
    /// </summary>
    public static DataTable Join(ReportDefinition report, IReadOnlyDictionary<string, DataTable> tables, List<string> warnings)
    {
        var byAlias = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, table) in tables)
            byAlias[name] = table;

        // The report's tables in its own order, then any the caller pushed that it does not list.
        var reportTables = report.DataSources.SelectMany(ds => ds.Tables).Select(t => t.Alias.Length > 0 ? t.Alias : t.Name).ToList();
        if (reportTables.Count == 0)
            reportTables = report.Fields.OfType<DatabaseField>().Select(f => f.TableName)
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in byAlias.Keys)
            if (!reportTables.Contains(name, StringComparer.OrdinalIgnoreCase))
                warnings.Add($"TableData: the report has no table named '{name}'; it is not used.");
        var order = reportTables.Where(byAlias.ContainsKey).ToList();
        foreach (var missing in reportTables.Where(t => !byAlias.ContainsKey(t)))
            warnings.Add($"TableData: no data was pushed for table '{missing}'; it renders empty.");

        var result = new DataTable();
        if (order.Count == 0) return result;

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result = Qualified(order[0], byAlias[order[0]]);
        placed.Add(order[0]);

        var pending = report.TableLinks
            .Where(l => byAlias.ContainsKey(l.SourceTable) && byAlias.ContainsKey(l.TargetTable))
            .ToList();
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var link in pending.ToList())
            {
                bool sourceIn = placed.Contains(link.SourceTable);
                bool targetIn = placed.Contains(link.TargetTable);
                if (!sourceIn && !targetIn) continue;
                pending.Remove(link);
                progress = true;

                if (sourceIn && targetIn)
                {
                    result = Filter(result, $"{link.SourceTable}.{link.SourceColumn}", $"{link.TargetTable}.{link.TargetColumn}", link);
                    continue;
                }

                // A left outer join keeps every source row. Adding the source to a result
                // that already holds the target keeps every row of the right side instead.
                string joiningAlias = sourceIn ? link.TargetTable : link.SourceTable;
                var right = Qualified(joiningAlias, byAlias[joiningAlias]);
                string leftKey = sourceIn ? $"{link.SourceTable}.{link.SourceColumn}" : $"{link.TargetTable}.{link.TargetColumn}";
                string rightKey = sourceIn ? $"{link.TargetTable}.{link.TargetColumn}" : $"{link.SourceTable}.{link.SourceColumn}";
                bool keepLeft = link.JoinType == TableJoinType.FullOuter
                    || (sourceIn ? link.JoinType == TableJoinType.LeftOuter : link.JoinType == TableJoinType.RightOuter);
                bool keepRight = link.JoinType == TableJoinType.FullOuter
                    || (sourceIn ? link.JoinType == TableJoinType.RightOuter : link.JoinType == TableJoinType.LeftOuter);
                var op = sourceIn ? link.Operator : Mirror(link.Operator);
                result = Merge(result, right, leftKey, rightKey, op, keepLeft, keepRight);
                placed.Add(joiningAlias);
            }

            if (!progress)
            {
                var next = order.FirstOrDefault(t => !placed.Contains(t));
                if (next is null) break;
                result = CrossJoin(result, Qualified(next, byAlias[next]));
                placed.Add(next);
                progress = true;
            }
        }

        AddBareNames(result);
        return result;
    }

    /// <summary>
    /// A table the caller joined already, with every column a database field reads added
    /// under the qualified name the converter writes ("Customer.Customer ID") when the
    /// table has it under the bare one. The caller's table is not changed.
    /// </summary>
    public static DataTable QualifyColumns(ReportDefinition report, DataTable data)
    {
        var wanted = report.Fields.OfType<DatabaseField>()
            .Where(f => f.TableName.Length > 0 && f.ColumnName.Length > 0)
            .Select(f => (Qualified: $"{f.TableName}.{f.ColumnName}", Bare: f.ColumnName))
            .Where(p => !data.Columns.Contains(p.Qualified) && data.Columns.Contains(p.Bare))
            .DistinctBy(p => p.Qualified, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return data;

        var copy = data.Copy();
        foreach (var (qualified, bare) in wanted)
        {
            var source = copy.Columns[bare]!;
            var added = copy.Columns.Add(qualified, source.DataType);
            foreach (DataRow row in copy.Rows)
                row[added] = row[source];
        }
        return copy;
    }

    /// <summary>
    /// The data with a rank column for each top or bottom N group
    /// (<see cref="Converter.RdlConverter.TopNRankField"/>): within the groups above it, each of
    /// the group's instances ranked by its summary, 1 first, highest first for a top N and
    /// lowest first for a bottom N, ties by the group's value. The converted report filters and
    /// sorts on it. A group on a formula, or one whose columns the data lacks, gets no rank,
    /// and then prints every instance. The caller's table is not changed.
    /// </summary>
    public static DataTable AddTopNRanks(ReportDefinition report, DataTable data, ISet<int>? selectedRows = null)
    {
        var groups = report.Groups.OrderBy(g => g.Level).ToList();
        if (!groups.Any(g => g.TopN is not null)) return data;

        string? Column(string table, string field)
        {
            if (field.Length == 0 || field.StartsWith('@')) return null;
            if (table.Length > 0 && data.Columns.Contains($"{table}.{field}")) return $"{table}.{field}";
            return data.Columns.Contains(field) ? field : null;
        }

        DataTable? result = null;
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.TopN is not { } topN) continue;
            string? key = Column(g.TableName, g.FieldName);
            string? measure = Column(topN.TableName, topN.FieldName);
            if (key is null || (measure is null && topN.Function is not AggregateFunction.Count)) continue;
            var outer = groups.Take(i).Select(o => Column(o.TableName, o.FieldName)).ToList();
            if (outer.Any(c => c is null)) continue;

            result ??= data.Copy();
            var rankColumn = result.Columns.Add(Converter.RdlConverter.TopNRankField(g.Level), typeof(int));
            var rows = result.Rows.Cast<DataRow>().ToList();
            // Crystal ranks the groups its record selection leaves, so a row the selection drops
            // does not count towards its group's summary.
            var ranked = selectedRows is null ? rows : rows.Where((r, index) => selectedRows.Contains(index)).ToList();
            foreach (var partition in ranked.GroupBy(r => string.Join("\u001f", outer.Select(c => Normalize(r[c!])?.ToString() ?? "\u0000"))))
            {
                var instances = partition
                    .GroupBy(r => Normalize(r[key]) ?? DBNull.Value, KeyComparer.Instance)
                    .Select(inst => (Key: inst.Key, Rows: inst.ToList(), Value: Aggregate(topN.Function, inst, measure)))
                    .ToList();
                var ordered = (topN.Bottom
                        ? instances.OrderBy(x => x.Value, NullsLast.Instance)
                        : instances.OrderByDescending(x => x.Value, NullsFirstDescending.Instance))
                    .ThenBy(x => x.Key is DBNull ? null : x.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int rank = 0; rank < ordered.Count; rank++)
                    foreach (var row in ordered[rank].Rows)
                        row[rankColumn] = rank + 1;
            }
        }
        return result ?? data;
    }

    private static double? Aggregate(AggregateFunction fn, IEnumerable<DataRow> rows, string? column)
    {
        var values = column is null ? [] : rows.Select(r => r[column]).Where(v => v is not DBNull and not null).ToList();
        double? Number(object v) => v is IConvertible c && double.TryParse(Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        var numbers = values.Select(Number).Where(n => n is not null).Select(n => n!.Value).ToList();
        return fn switch
        {
            AggregateFunction.Count => column is null ? rows.Count() : values.Count,
            AggregateFunction.DistinctCount => values.Distinct(KeyComparer.Instance).Count(),
            AggregateFunction.Average => numbers.Count > 0 ? numbers.Average() : null,
            AggregateFunction.Maximum => numbers.Count > 0 ? numbers.Max() : null,
            AggregateFunction.Minimum => numbers.Count > 0 ? numbers.Min() : null,
            _ => numbers.Sum()
        };
    }

    private sealed class NullsLast : IComparer<double?>
    {
        public static readonly NullsLast Instance = new();
        public int Compare(double? x, double? y) => (x, y) switch
        {
            (null, null) => 0, (null, _) => 1, (_, null) => -1, _ => x.Value.CompareTo(y.Value)
        };
    }

    // For a descending order, so that an empty summary still comes last.
    private sealed class NullsFirstDescending : IComparer<double?>
    {
        public static readonly NullsFirstDescending Instance = new();
        public int Compare(double? x, double? y) => (x, y) switch
        {
            (null, null) => 0, (null, _) => -1, (_, null) => 1, _ => x.Value.CompareTo(y.Value)
        };
    }

    // The table's columns renamed "Alias.Column".
    private static DataTable Qualified(string alias, DataTable table)
    {
        var q = new DataTable();
        foreach (DataColumn c in table.Columns)
            q.Columns.Add($"{alias}.{c.ColumnName}", c.DataType);
        foreach (DataRow row in table.Rows)
            q.Rows.Add(row.ItemArray);
        return q;
    }

    // For every "Alias.Column" whose Column no other table in the result has, the same
    // values under "Column" too.
    private static void AddBareNames(DataTable result)
    {
        var bare = result.Columns.Cast<DataColumn>()
            .Select(c => (Column: c, Bare: c.ColumnName[(c.ColumnName.IndexOf('.') + 1)..]))
            .Where(p => p.Column.ColumnName.Contains('.'))
            .GroupBy(p => p.Bare, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1 && !result.Columns.Contains(g.Key))
            .Select(g => g.First())
            .ToList();
        foreach (var (column, name) in bare)
        {
            var added = result.Columns.Add(name, column.DataType);
            foreach (DataRow row in result.Rows)
                row[added] = row[column];
        }
    }

    private static DataTable Merge(DataTable left, DataTable right, string leftKey, string rightKey,
        LinkOperator op, bool keepLeft, bool keepRight)
    {
        var result = new DataTable();
        foreach (DataColumn c in left.Columns) result.Columns.Add(c.ColumnName, c.DataType);
        foreach (DataColumn c in right.Columns) result.Columns.Add(c.ColumnName, c.DataType);

        int leftCount = left.Columns.Count, rightCount = right.Columns.Count;
        bool hasLeftKey = left.Columns.Contains(leftKey), hasRightKey = right.Columns.Contains(rightKey);
        var matchedRight = new bool[right.Rows.Count];

        // An equal join finds its matches by hashing the key; any other comparison walks
        // the right side for each left row.
        Dictionary<object, List<int>>? index = null;
        if (op is LinkOperator.Equal or LinkOperator.Unknown && hasRightKey)
        {
            index = new Dictionary<object, List<int>>(KeyComparer.Instance);
            for (int i = 0; i < right.Rows.Count; i++)
            {
                var key = Normalize(right.Rows[i][rightKey]);
                if (key is null) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                list.Add(i);
            }
        }

        foreach (DataRow l in left.Rows)
        {
            bool matched = false;
            if (hasLeftKey && hasRightKey)
            {
                var key = Normalize(l[leftKey]);
                if (key is not null)
                {
                    IEnumerable<int> candidates = index is not null
                        ? (index.TryGetValue(key, out var hits) ? hits : [])
                        : Enumerable.Range(0, right.Rows.Count).Where(i => Compare(key, Normalize(right.Rows[i][rightKey]), op));
                    foreach (int i in candidates)
                    {
                        matched = true;
                        matchedRight[i] = true;
                        var values = new object[leftCount + rightCount];
                        l.ItemArray.CopyTo(values, 0);
                        right.Rows[i].ItemArray.CopyTo(values, leftCount);
                        result.Rows.Add(values);
                    }
                }
            }
            if (!matched && keepLeft)
            {
                var values = new object[leftCount + rightCount];
                l.ItemArray.CopyTo(values, 0);
                for (int i = leftCount; i < values.Length; i++) values[i] = DBNull.Value;
                result.Rows.Add(values);
            }
        }
        if (keepRight)
        {
            for (int i = 0; i < right.Rows.Count; i++)
            {
                if (matchedRight[i]) continue;
                var values = new object[leftCount + rightCount];
                for (int j = 0; j < leftCount; j++) values[j] = DBNull.Value;
                right.Rows[i].ItemArray.CopyTo(values, leftCount);
                result.Rows.Add(values);
            }
        }
        return result;
    }

    // A second link between two tables already joined: keep the rows that satisfy it, and
    // the rows an outer join padded with nulls on either side.
    private static DataTable Filter(DataTable table, string leftKey, string rightKey, TableLink link)
    {
        if (!table.Columns.Contains(leftKey) || !table.Columns.Contains(rightKey)) return table;
        var result = table.Clone();
        foreach (DataRow row in table.Rows)
        {
            var a = Normalize(row[leftKey]);
            var b = Normalize(row[rightKey]);
            bool keep = (a is null || b is null)
                ? link.JoinType != TableJoinType.Inner
                : Compare(a, b, link.Operator);
            if (keep) result.Rows.Add(row.ItemArray);
        }
        return result;
    }

    private static DataTable CrossJoin(DataTable left, DataTable right)
    {
        var result = new DataTable();
        foreach (DataColumn c in left.Columns) result.Columns.Add(c.ColumnName, c.DataType);
        foreach (DataColumn c in right.Columns) result.Columns.Add(c.ColumnName, c.DataType);
        int leftCount = left.Columns.Count;
        foreach (DataRow l in left.Rows)
            foreach (DataRow r in right.Rows)
            {
                var values = new object[result.Columns.Count];
                l.ItemArray.CopyTo(values, 0);
                r.ItemArray.CopyTo(values, leftCount);
                result.Rows.Add(values);
            }
        return result;
    }

    private static LinkOperator Mirror(LinkOperator op) => op switch
    {
        LinkOperator.GreaterThan => LinkOperator.LessThan,
        LinkOperator.LessThan => LinkOperator.GreaterThan,
        LinkOperator.GreaterOrEqual => LinkOperator.LessOrEqual,
        LinkOperator.LessOrEqual => LinkOperator.GreaterOrEqual,
        _ => op
    };

    // Keys compare as a database would: nulls match nothing, numbers by value whatever their
    // width, strings without regard to case.
    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
            => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };

    private static bool Compare(object a, object? b, LinkOperator op)
    {
        if (b is null) return false;
        if (op is LinkOperator.Equal or LinkOperator.Unknown) return KeyComparer.Instance.Equals(a, b);
        if (op == LinkOperator.NotEqual) return !KeyComparer.Instance.Equals(a, b);
        int c;
        if (a is string sa && b is string sb) c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
        else if (a is IComparable ca && a.GetType() == b.GetType()) c = ca.CompareTo(b);
        else return false;
        return op switch
        {
            LinkOperator.GreaterThan => c > 0,
            LinkOperator.LessThan => c < 0,
            LinkOperator.GreaterOrEqual => c >= 0,
            LinkOperator.LessOrEqual => c <= 0,
            _ => c == 0
        };
    }

    private sealed class KeyComparer : IEqualityComparer<object>
    {
        public static readonly KeyComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is string a && y is string b ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase) : object.Equals(x, y);

        public int GetHashCode(object obj) =>
            obj is string s ? StringComparer.OrdinalIgnoreCase.GetHashCode(s) : obj.GetHashCode();
    }
}
