using Irony.Parsing;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// Thread-safe wrapper around the Irony <see cref="CrystalFormulaGrammar"/> parser.
/// Call <see cref="ToRdlExpression"/> to parse and emit a single formula string.
/// </summary>
public sealed class CrystalFormulaParser
{
    // LanguageData and Parser are heavyweight objects — build once per grammar.
    private static readonly Lazy<CrystalFormulaParser> _instance =
        new(() => new CrystalFormulaParser(), isThreadSafe: true);

    public static CrystalFormulaParser Instance => _instance.Value;

    // Irony's Parser carries mutable per-parse state (ParsingContext) and must not
    // be shared across threads. LanguageData is immutable after construction, so it
    // is built once and each thread gets its own lightweight Parser over it.
    private readonly LanguageData _langData;
    private readonly ThreadLocal<Parser> _parser;

    private Parser Parser => _parser.Value!;

    // Grammar errors/warnings from construction — logged once
    public IReadOnlyList<string> GrammarErrors { get; }

    public CrystalFormulaParser()
    {
        var grammar = new CrystalFormulaGrammar();
        _langData = new LanguageData(grammar);

        // Filter to Error-level only; resolved SR conflicts are Warning-level and expected.
        GrammarErrors = _langData.Errors
            .Where(e => e.Level == GrammarErrorLevel.Error)
            .Select(e => e.ToString())
            .ToList();
        _parser = new ThreadLocal<Parser>(() => new Parser(_langData));
    }

    /// <summary>
    /// Parses <paramref name="formula"/> and emits an RDL/VB.NET expression.
    /// Returns null if parsing failed (caller should fall back to regex transpiler).
    /// </summary>
    public string? ToRdlExpression(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return "\"\"";

        var tree = Parser.Parse(formula);

        if (tree == null || tree.HasErrors())
            return null;  // let caller fall back

        return RdlEmitter.Emit(tree);
    }

    /// <summary>
    /// Returns all parse errors for diagnostics, or empty list on success.
    /// </summary>
    public IReadOnlyList<string> GetParseErrors(string formula)
    {
        var tree = Parser.Parse(formula);
        if (tree == null) return ["Parse returned null"];

        return tree.ParserMessages
            .Where(m => m.Level == Irony.ErrorLevel.Error)
            .Select(m => $"[{m.Location}] {m.Message}")
            .ToList();
    }
}
