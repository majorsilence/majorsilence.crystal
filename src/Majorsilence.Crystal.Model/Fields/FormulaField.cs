namespace Majorsilence.Crystal.Model.Fields;

public sealed class FormulaField : ReportField
{
    /// <summary>
    /// Raw Crystal formula text. May be Crystal syntax or Basic syntax — check <see cref="Syntax"/>.
    /// </summary>
    public string FormulaText { get; init; } = string.Empty;
    public FormulaSyntax Syntax { get; init; } = FormulaSyntax.Crystal;
}

public enum FormulaSyntax { Crystal, Basic }
