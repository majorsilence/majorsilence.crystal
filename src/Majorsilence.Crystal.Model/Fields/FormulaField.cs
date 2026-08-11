namespace Majorsilence.Crystal.Model.Fields;

public sealed class FormulaField : ReportField
{
    /// <summary>
    /// Raw Crystal formula text. May be Crystal syntax or Basic syntax — check <see cref="Syntax"/>.
    /// </summary>
    public string FormulaText { get; set; } = string.Empty;
    public FormulaSyntax Syntax { get; set; } = FormulaSyntax.Crystal;
}

public enum FormulaSyntax { Crystal, Basic }
