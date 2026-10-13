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
}

public enum FormulaSyntax { Crystal, Basic }
