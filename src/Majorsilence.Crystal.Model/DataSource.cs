namespace Majorsilence.Crystal.Model;

public sealed class DataSource
{
    public string Name { get; init; } = string.Empty;
    public DataSourceKind Kind { get; init; }
    public string? ServerName { get; init; }
    public string? DatabaseName { get; init; }
    public string? OdbcDsn { get; init; }
    public string? SqlQuery { get; init; }
    public List<TableDefinition> Tables { get; init; } = [];
}

public enum DataSourceKind { Odbc, OleDb, Native, Dataset, Unknown }

public sealed class TableDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public List<ColumnDefinition> Columns { get; init; } = [];
}

public sealed class ColumnDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
}
