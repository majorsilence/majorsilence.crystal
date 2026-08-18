using Irony.Parsing;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// Irony grammar for Crystal Reports formula language (Crystal Syntax dialect).
///
/// Design notes:
///   - Binary operators are placed DIRECTLY in expr.Rule (not through an intermediate
///     non-terminal) so that RegisterOperators() can resolve shift-reduce conflicts.
///   - Structural keywords (If, Then, Else, Select, Case, Default, Var, Is, To) are
///     marked as punctuation so they are stripped from the parse tree, leaving only
///     the semantic children.
///   - Operator keywords (And, Or, Not, Xor, Mod, Like, In, Eqv, Imp) are NOT marked
///     as punctuation because the emitter needs to read their values.
/// </summary>
[Language("CrystalFormula", "1.0", "Crystal Reports Formula Language")]
public sealed class CrystalFormulaGrammar : Grammar
{
    // ── Rule name constants (used by RdlEmitter to identify nodes) ─────────────
    public const string ProgramRule        = "program";
    public const string StmtListRule       = "stmtList";
    public const string ExprRule           = "expr";
    public const string IfExprRule         = "ifExpr";
    public const string SelectExprRule     = "selectExpr";
    public const string CaseClauseListRule = "caseClauseList";
    public const string CaseClauseRule     = "caseClause";
    public const string CaseValueListRule  = "caseValueList";
    public const string CaseValueRule      = "caseValue";
    public const string FuncCallRule       = "funcCall";
    public const string ArgListRule        = "argList";
    public const string FieldRefTerm       = "fieldRef";
    public const string NumberTerm         = "number";
    public const string StringDqTerm       = "strDq";
    public const string StringSqTerm       = "strSq";
    public const string DateLitTerm        = "date";
    public const string IdentTerm          = "id";
    public const string DottedRefRule      = "dottedRef";
    public const string AtRefRule          = "atRef";
    public const string HashRefRule        = "hashRef";
    public const string SliceExprRule      = "sliceExpr";

    public CrystalFormulaGrammar() : base(caseSensitive: false)
    {
        // ─── Terminals ────────────────────────────────────────────────────────
        var number   = new NumberLiteral(NumberTerm, NumberOptions.AllowSign);
        var strDq    = new StringLiteral(StringDqTerm, "\"",
                           StringOptions.AllowsDoubledQuote | StringOptions.AllowsLineBreak);
        var strSq    = new StringLiteral(StringSqTerm, "'",
                           StringOptions.AllowsDoubledQuote | StringOptions.AllowsLineBreak);
        var dateLit  = new RegexBasedTerminal(DateLitTerm, @"#[^#\r\n]+#");
        var fieldRef = new RegexBasedTerminal(FieldRefTerm, @"\{[^}\r\n]*\}");
        var id       = new IdentifierTerminal(IdentTerm);

        // ─── Non-terminals ────────────────────────────────────────────────────
        var program          = new NonTerminal(ProgramRule);
        var stmtList         = new NonTerminal(StmtListRule);
        var stmt             = new NonTerminal("stmt");
        var expr             = new NonTerminal(ExprRule);
        var primary          = new NonTerminal("primary");
        var ifExpr           = new NonTerminal(IfExprRule);
        var selectExpr       = new NonTerminal(SelectExprRule);
        var caseClauseList   = new NonTerminal(CaseClauseListRule);
        var caseClause       = new NonTerminal(CaseClauseRule);
        var caseValueList    = new NonTerminal(CaseValueListRule);
        var caseValue        = new NonTerminal(CaseValueRule);
        var funcCall         = new NonTerminal(FuncCallRule);
        var argList          = new NonTerminal(ArgListRule);
        var argListOpt       = new NonTerminal("argListOpt");
        var dottedRef        = new NonTerminal(DottedRefRule);
        var atRef            = new NonTerminal(AtRefRule);
        var hashRef          = new NonTerminal(HashRefRule);
        var sliceExpr        = new NonTerminal(SliceExprRule);

        // ─── Grammar rules ────────────────────────────────────────────────────

        // Crystal terminates statements with ";" and permits a trailing one on the last
        // statement ("CStr({X.Num}, '#');" is a complete, valid formula). MakePlusRule
        // only allows ";" *between* statements, so the trailing form has to be spelled
        // out explicitly or the whole formula fails to parse and falls through to the
        // regex fallback, which passes the stray ";" straight into the emitted RDL.
        program.Rule   = stmtList | stmtList + ";";
        stmtList.Rule  = MakePlusRule(stmtList, ToTerm(";"), stmt);

        // No varDecl rule, deliberately. Crystal's "Local StringVar x := ..." (and its
        // scopeless "stringvar x := ..." form) declares a local variable, which RDL
        // expressions have no equivalent for at all — a later "x" reference can't be
        // emitted as anything meaningful. There *was* a varDecl rule here, but it never
        // matched: it spelled the declaration as three tokens (scope + type + "Var")
        // while the lexer reads "StringVar" as a single identifier. That accident is
        // what makes these formulas work as well as they currently do — the parse fails,
        // FormulaTranspiler falls through to RegexTranspile, and its CrystalVarDecl
        // guard degrades the whole formula to "" so the RDL stays valid instead of
        // fatally referencing an undefined identifier. Making the rule parse would
        // *bypass* that guard and emit worse output, so the rule is removed rather than
        // repaired, leaving one mechanism for variable declarations instead of two.
        stmt.Rule      = expr;

        // Primary atoms
        primary.Rule   = number
                       | strDq
                       | strSq
                       | dateLit
                       | fieldRef
                       | ToTerm("True")
                       | ToTerm("False")
                       | ToTerm("Null")
                       | funcCall
                       | dottedRef
                       | atRef
                       | hashRef
                       | id
                       | sliceExpr
                       | "(" + expr + ")";

        // Crystal string-slice syntax — a postfix "[n]" (single character) or
        // "[n To m]" (substring, inclusive) on any string-valued primary, e.g.
        // {Customer.Name}[1 To 3] or {@Formula}[5].
        sliceExpr.Rule = primary + "[" + expr + "To" + expr + "]"
                       | primary + "[" + expr + "]";

        funcCall.Rule    = id + "(" + argListOpt + ")";
        argListOpt.Rule  = argList | Empty;
        argList.Rule     = MakePlusRule(argList, ToTerm(","), expr);

        // Crystal allows database-field and formula/running-total references without the
        // {...} bracket wrapper the fieldRef terminal expects — e.g. a formula whose whole
        // body is just "Customer.Region", "@AnotherFormula", or "#RTotal0". Braced forms
        // ({Table.Column}, {@Formula}, {?Param}) already work via fieldRef/EmitFieldRef;
        // these three cover the same references written bare.
        dottedRef.Rule = id + "." + id;   // Table.Column -> Fields!Column.Value
        atRef.Rule     = ToTerm("@") + id; // @FormulaName -> Fields!FormulaName.Value
        hashRef.Rule   = ToTerm("#") + id; // #RunningTotalName -> Fields!RunningTotalName.Value

        // Expressions — operators DIRECTLY in rule so RegisterOperators can see them
        expr.Rule
            = expr + "^"   + expr
            | expr + "*"   + expr
            | expr + "/"   + expr
            | expr + "\\"  + expr
            | expr + "Mod" + expr
            | expr + "+"   + expr
            | expr + "-"   + expr
            | expr + "&"   + expr
            | expr + "="   + expr
            | expr + "<>"  + expr
            | expr + "<"   + expr
            | expr + ">"   + expr
            | expr + "<="  + expr
            | expr + ">="  + expr
            | expr + "Like" + expr
            | expr + "In"  + "[" + caseValueList + "]"
            | expr + "In"  + "(" + caseValueList + ")"
            // String containment: {X} in "USA" — Crystal's `in` doubles as a substring
            // test when the right side is a plain value rather than a [list].
            | expr + "In"  + expr
            | expr + "And" + expr
            | expr + "Xor" + expr
            | expr + "Or"  + expr
            | expr + "Eqv" + expr
            | expr + "Imp" + expr
            | ToTerm("Not") + expr
            | ToTerm("-")   + expr
            | ToTerm("+")   + expr
            | ifExpr
            | selectExpr
            | primary;

        // If/Then/Else — "If", "Then", "Else" are marked as punctuation below
        ifExpr.Rule  = ToTerm("If") + expr + "Then" + expr + "Else" + expr
                     | ToTerm("If") + expr + "Then" + expr;

        // Select Case — structural keywords marked as punctuation below. Crystal's own
        // spelling is "Select <expr> Case v: r ..." (no "Case" after "Select"); the
        // "Select Case <expr>" form is the Basic-dialect/VB spelling. Both appear in
        // real files, so accept both.
        selectExpr.Rule     = ToTerm("Select") + "Case" + expr + caseClauseList
                            | ToTerm("Select") + "Case" + expr + caseClauseList
                              + "Default" + ":" + expr
                            | ToTerm("Select") + expr + caseClauseList
                            | ToTerm("Select") + expr + caseClauseList
                              + "Default" + ":" + expr;
        caseClauseList.Rule = MakePlusRule(caseClauseList, caseClause);
        caseClause.Rule     = "Case" + "Else"     + ":" + expr   // Default/Else alias
                            | "Case" + "Is" + "=" + expr + ":" + expr   // Case Is = val
                            | "Case" + "Is" + "<>" + expr + ":" + expr
                            | "Case" + "Is" + "<"  + expr + ":" + expr
                            | "Case" + "Is" + ">"  + expr + ":" + expr
                            | "Case" + "Is" + "<=" + expr + ":" + expr
                            | "Case" + "Is" + ">=" + expr + ":" + expr
                            | "Case" + caseValueList + ":" + expr;
        caseValueList.Rule  = MakePlusRule(caseValueList, ToTerm(","), caseValue);
        caseValue.Rule      = expr + "To" + expr
                            | expr;

        // ─── Operator precedence (higher number = tighter binding) ─────────────
        RegisterOperators(10, Associativity.Right, "^");
        RegisterOperators(9,  Associativity.Left,  "*", "/", "\\");
        RegisterOperators(8,  Associativity.Left,  "Mod");
        RegisterOperators(7,  Associativity.Left,  "+", "-");
        RegisterOperators(6,  Associativity.Left,  "&");
        RegisterOperators(5,  Associativity.Left,  "=", "<>", "<", ">", "<=", ">=",
                                                    "Like", "In");
        RegisterOperators(4,  Associativity.Right, "Not");
        RegisterOperators(3,  Associativity.Left,  "And");
        RegisterOperators(2,  Associativity.Left,  "Xor");
        RegisterOperators(1,  Associativity.Left,  "Or", "Eqv", "Imp");

        // Unary minus/plus/not at same or higher than highest binary to avoid ambiguity
        // Irony handles unary automatically by context; no extra registration needed.

        // ─── Reserved words ───────────────────────────────────────────────────
        // Variable-declaration keywords (Local/Global/Shared, the type names) are
        // deliberately absent — see the varDecl note above: those formulas are meant to
        // fail the parse so FormulaTranspiler's CrystalVarDecl guard can degrade them.
        MarkReservedWords(
            "If", "Then", "Else", "ElseIf", "Select", "Case", "Default", "End",
            "In", "To", "Is", "And", "Or", "Not", "Xor", "Eqv", "Imp", "Mod", "Like",
            "True", "False", "Null"
        );

        // ─── Structural keywords removed from parse tree ─────────────────────
        // These are grammar scaffolding — their presence is implied by the node type.
        MarkPunctuation(
            ";", ",", ":", "(", ")", "[", "]",
            "If", "Then", "Else",
            "Select", "Case", "Default",
            "Is", "To",
            ".", "@", "#"
        );

        // Transparent single-child nodes — elided from tree
        MarkTransient(program, stmt, primary, argListOpt);

        // ─── Comments ─────────────────────────────────────────────────────────
        NonGrammarTerminals.Add(new CommentTerminal("lineComment", "//", "\n", "\r"));
        NonGrammarTerminals.Add(new CommentTerminal("blockComment", "/*", "*/"));

        this.Root = program;
        this.LanguageFlags = LanguageFlags.NewLineBeforeEOF;
    }
}
