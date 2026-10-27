namespace Majorsilence.Crystal.Model.Fields;

public sealed class FormulaField : ReportField
{
    /// <summary>
    /// Raw Crystal formula text. May be Crystal syntax or Basic syntax — check <see cref="Syntax"/>.
    /// </summary>
    public string FormulaText { get; set; } = string.Empty;
    public FormulaSyntax Syntax { get; set; } = FormulaSyntax.Crystal;

    /// <summary>
    /// The type the formula returns, as the file records it, in the names
    /// <see cref="DatabaseField.DataType"/> uses ("Float64", "Currency", "Boolean",
    /// "DateTime", "String"); null when the file says it is unknown.
    /// </summary>
    public string? ResultType { get; set; }

    /// <summary>
    /// The formula's "Default Values For Nulls" setting: a null field it reads counts as
    /// "" or 0 inside it. Clear ("Exceptions For Nulls", Crystal's default), a null field
    /// read where the report does not convert nulls makes the whole formula null.
    /// </summary>
    public bool DefaultValuesForNulls { get; set; }
}

public enum FormulaSyntax { Crystal, Basic }
