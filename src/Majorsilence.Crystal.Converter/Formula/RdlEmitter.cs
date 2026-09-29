using System.Text;
using Irony.Parsing;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// Walks an Irony ParseTree produced by <see cref="CrystalFormulaGrammar"/> and emits
/// a VB.NET / RDL expression string.
///
/// Tree shape after MarkPunctuation/MarkTransient:
///   expr (binary) → [left:expr, op:KeyTerm, right:expr]  (3 children)
///   expr (unary)  → [op:KeyTerm, operand:expr]            (2 children)
///   expr (in+list) → [left:expr, "In", caseValueList]     (3 children)
///   expr (in+range) → [left:expr, "In", lo:expr, hi:expr]  (4 children -- "To" is
///                     grammar punctuation, stripped from the tree)
///   ifExpr        → [cond:expr, then:expr] or
///                   [cond:expr, then:expr, else:expr]      (2 or 3 children)
///   selectExpr    → [disc:expr, caseClauseList] or
///                   [disc:expr, caseClauseList, default:expr] (2 or 3 children)
///   caseClause    → [result:expr]                    for Case Else
///                   [op, rhs, result]                for Case Is &lt;op&gt; rhs
///                   [caseValueList, result]           for Case list
///   stmtList      → [stmt, stmt, ..., lastStmt]
///   varDecl       → [id] or [id, initExpr]
///   funcCall      → [id] or [id, argList]
/// </summary>
public static class RdlEmitter
{
    /// <summary>Crystal function name → RDL / VB.NET function or global.</summary>
    public static readonly Dictionary<string, string> FunctionMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Type conversion
            ["totext"]          = "CStr",
            ["tostring"]        = "CStr",
            ["cstr"]            = "CStr",
            ["tonumber"]        = "CDbl",
            ["cdbl"]            = "CDbl",
            ["cint"]            = "CInt",
            ["clng"]            = "CLng",
            ["cbool"]           = "CBool",
            ["cdate"]           = "CDate",
            ["ccur"]            = "CDec",
            // Null / empty
            ["isnull"]          = "IsNothing",
            ["isnullorempty"]   = "IsNothing",
            // HasValue is the opposite of IsNothing, so it cannot be a plain rename - it is
            // wrapped below. Listed here only so the two stay findable together.
            // String
            ["len"]             = "Len",
            ["length"]          = "Len",
            ["left"]            = "Left",
            ["right"]           = "Right",
            ["mid"]             = "Mid",
            ["trim"]            = "Trim",
            ["ltrim"]           = "LTrim",
            ["rtrim"]           = "RTrim",
            ["ucase"]           = "UCase",
            ["uppercase"]       = "UCase",
            ["lcase"]           = "LCase",
            ["lowercase"]       = "LCase",
            ["propercase"]      = "StrConv",
            ["instr"]           = "InStr",
            ["instrrev"]        = "InStrRev",
            ["replace"]         = "Replace",
            ["replaceall"]      = "Replace",
            ["space"]           = "Space",
            ["rept"]            = "StrDup",
            ["replicatestring"] = "StrDup",
            ["chr"]             = "Chr",
            ["asc"]             = "Asc",
            ["strreverse"]      = "StrReverse",
            // Crystal's DayOfWeek is VB's Weekday, which the engine has (VBFunctions.cs);
            // "DayOfWeek" it does not, and reached it as an unknown function.
            ["dayofweek"]       = "Weekday",
            // Math
            ["abs"]             = "Abs",
            ["round"]           = "Round",
            ["int"]             = "Int",
            ["truncate"]        = "Fix",
            ["fix"]             = "Fix",
            ["sgn"]             = "Sgn",
            ["sqrt"]            = "Sqr",
            ["exp"]             = "Exp",
            ["log"]             = "Log",
            ["sin"]             = "Sin",
            ["cos"]             = "Cos",
            ["tan"]             = "Tan",
            ["atn"]             = "Atn",
            ["pi"]              = "Math.PI",
            ["random"]          = "Rnd",
            // Date/time
            ["dateadd"]         = "DateAdd",
            ["datediff"]        = "DateDiff",
            ["datepart"]        = "DatePart",
            ["dateserial"]      = "DateSerial",
            // Crystal's Date(year, month, day) — confirmed corpus usage is always this
            // 3-arg constructor form, same shape as VB.NET's DateSerial. Crystal's other
            // Date() overloads (1-arg date-value coercion) aren't handled by this mapping.
            ["date"]            = "DateSerial",
            ["datevalue"]       = "DateValue",
            ["year"]            = "Year",
            ["month"]           = "Month",
            ["day"]             = "Day",
            ["hour"]            = "Hour",
            ["minute"]          = "Minute",
            ["second"]          = "Second",
            ["weekday"]         = "Weekday",
            ["monthname"]       = "MonthName",
            ["weekdayname"]     = "WeekdayName",
            ["now"]             = "Now",
            ["datetime"]        = "CDateTime",
            ["cdatetime"]       = "CDateTime",
            ["today"]           = "Today",
            ["currentdate"]     = "Today",
            ["currenttime"]     = "TimeOfDay",
            ["currentdatetime"] = "Now",
            ["timer"]           = "Timer",
            // Crystal-only helpers that now have engine counterparts: Roundup rounds
            // up at a decimal place (unlike Ceiling's "nearest multiple" second
            // argument) and Picture formats a string through an "x"-placeholder mask.
            ["roundup"]         = "RoundUp",
            ["picture"]         = "Picture",
            // Aggregates
            ["sum"]             = "Sum",
            ["count"]           = "Count",
            ["distinctcount"]   = "CountDistinct",
            ["average"]         = "Avg",
            ["avg"]             = "Avg",
            ["minimum"]         = "Min",
            ["maximum"]         = "Max",
            ["first"]           = "First",
            ["last"]            = "Last",
            ["previousvalue"]   = "Previous",
            // Report state
            ["pagenumber"]      = "Globals!PageNumber",
            ["totalpagecount"]  = "Globals!TotalPages",
            ["reportname"]      = "Globals!ReportName",
            // SAP Business One templates favor switch(cond1, val1, cond2, val2, ..., True,
            // default) as a plain function call instead of Crystal's native Select/Case —
            // same alternating-pairs shape EmitSelectCase already builds by hand, and the
            // target engine's expression parser recognizes "Switch" as a real construct
            // (Parser.cs: case "switch" -> new FunctionSwitch(args)), so the args need no
            // reshaping at all — just the function name capitalized.
            ["switch"]          = "Switch",
        };

    private static readonly Dictionary<string, string> BareIdentMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pagenumber"]      = "Globals!PageNumber",
            ["totalpagecount"]  = "Globals!TotalPages",
            ["reportname"]      = "Globals!ReportName",
            ["currentdate"]     = "Today()",
            ["currenttime"]     = "TimeOfDay()",
            ["currentdatetime"] = "Now()",
            ["today"]           = "Today()",
            ["now"]             = "Now()",
            ["pi"]              = "Math.PI",
            ["true"]            = "True",
            ["false"]           = "False",
            ["null"]            = "Nothing",
            // Crystal record-position predicates. The engine types a bare unknown
            // identifier as non-boolean, so "Not OnFirstRecord" was fatal ("NOT requires
            // boolean expression"); both RowNumber and CountRows are real engine
            // built-ins (ExprParser/Parser.cs), so express the predicates through them.
            ["onfirstrecord"]   = "(RowNumber() = 1)",
            ["onlastrecord"]    = "(RowNumber() = CountRows())",
            // Crystal's "Record Number" special field, written without the space when
            // referenced bare in a formula rather than placed as a field.
            ["recordnumber"]    = "RowNumber()",
            // Crystal color constants → CSS color strings for SSRS
            ["crBlack"]         = "\"Black\"",
            ["crMaroon"]        = "\"#800000\"",
            ["crGreen"]         = "\"Green\"",
            ["crOlive"]         = "\"Olive\"",
            ["crNavy"]          = "\"Navy\"",
            ["crPurple"]        = "\"Purple\"",
            ["crTeal"]          = "\"Teal\"",
            ["crSilver"]        = "\"Silver\"",
            ["crRed"]           = "\"Red\"",
            ["crLime"]          = "\"Lime\"",
            ["crYellow"]        = "\"Yellow\"",
            ["crBlue"]          = "\"Blue\"",
            ["crFuchsia"]       = "\"Fuchsia\"",
            ["crAqua"]          = "\"Aqua\"",
            ["crWhite"]         = "\"White\"",
            ["crNoColor"]       = "\"Transparent\"",
            // Crystal's DayOfWeek constants, used as DatePart/DateDiff's optional
            // first-day-of-week argument (e.g. DatePart("ww", {X.Date}, crMonday)).
            // Crystal mirrors VB's own FirstDayOfWeek enum values exactly (documented in
            // Crystal's own function reference): Sunday=1 through Saturday=7. Mapped to
            // plain integer literals since VBFunctions.cs's DatePart 3-arg overload takes
            // the day as a 1-7 number, not a .NET DayOfWeek enum value.
            ["crSunday"]        = "1",
            ["crMonday"]        = "2",
            ["crTuesday"]       = "3",
            ["crWednesday"]     = "4",
            ["crThursday"]      = "5",
            ["crFriday"]        = "6",
            ["crSaturday"]      = "7",
        };

    // Operator tokens that are logical/relational and stay VB.NET keywords
    private static readonly HashSet<string> VbKeywordOps =
        new(StringComparer.OrdinalIgnoreCase)
        { "And", "Or", "Not", "Xor", "Eqv", "Imp", "Mod", "Like" };

    // RDL aggregate functions whose optional 2nd argument is a *scope*. In this engine
    // (RdlEngine/ExprParser/Parser.cs), a quoted scope on these functions must name a
    // DataSet — unlike RunningValue, they never accept a Grouping name — so Crystal's
    // "Sum grouped by field" shorthand (2nd arg = a group-by field reference) has no
    // valid translation here; see EmitFuncCall, which drops that argument instead.
    private static readonly HashSet<string> ScopedAggregateFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        { "Sum", "Count", "CountDistinct", "Avg", "Min", "Max", "First", "Last" };

    public static string Emit(ParseTree tree)
    {
        return tree.HasErrors() || tree.Root == null ? "\"\"" : EmitNode(tree.Root).Trim();
    }

    private static string EmitNode(ParseTreeNode node)
    {
        string name = node.Term.Name;

        // ── Transparent single-child passthrough ──────────────────────────────
        // atRef/hashRef end up with exactly one child too (the "@"/"#" prefix is
        // punctuation, stripped before this ever runs) but still need their own
        // Fields!X.Value wrapping below, not a bare passthrough of the identifier.
        // An arrayLit is excluded for the same reason: its brackets are punctuation, so a
        // one-element literal would otherwise collapse into that element and lose the fact
        // that it was ever a list.
        if (node.ChildNodes.Count == 1
            && name != CrystalFormulaGrammar.ArgListRule
            && name != CrystalFormulaGrammar.AtRefRule
            && name != CrystalFormulaGrammar.HashRefRule
            && name != CrystalFormulaGrammar.ArrayLitRule)
            return EmitNode(node.ChildNodes[0]);

        switch (name)
        {
            // ── Statement list: return only the last expression ────────────────
            case CrystalFormulaGrammar.StmtListRule:
                return EmitNode(node.ChildNodes[^1]);

            // ── Expressions ────────────────────────────────────────────────────
            case CrystalFormulaGrammar.ExprRule:
                return EmitExpr(node);

            // ── If/Then/Else ───────────────────────────────────────────────────
            // After MarkPunctuation removes If/Then/Else, children are:
            //   [cond, then]          (2 children)
            //   [cond, then, else]    (3 children)
            case CrystalFormulaGrammar.IfExprRule:
            {
                string cond  = EmitNode(node.ChildNodes[0]);
                string thenV = EmitNode(node.ChildNodes[1]);
                string elseV = node.ChildNodes.Count >= 3
                    ? EmitNode(node.ChildNodes[2])
                    : "Nothing";
                return $"IIf({cond}, {thenV}, {elseV})";
            }

            // ── Select Case ────────────────────────────────────────────────────
            // After MarkPunctuation removes Select/Case/Default/:, children are:
            //   [disc, caseClauseList]           (2 children)
            //   [disc, caseClauseList, default]  (3 children)
            case CrystalFormulaGrammar.SelectExprRule:
                return EmitSelectCase(node);

            // ── Case clause list ───────────────────────────────────────────────
            case CrystalFormulaGrammar.CaseClauseListRule:
                return string.Join(", ", node.ChildNodes.Select(EmitNode));

            // ── Individual case clause ─────────────────────────────────────────
            // After MarkPunctuation removes Case/Is/Else/:
            //   [result]              → Case Else / Default
            //   [op, rhs, result]     → Case Is <op> rhs
            //   [caseValueList, result] → Case list
            case CrystalFormulaGrammar.CaseClauseRule:
                return EmitNode(node.ChildNodes[^1]);  // just the result; context built by EmitSelectCase

            // ── Case value list ────────────────────────────────────────────────
            case CrystalFormulaGrammar.CaseValueListRule:
                return string.Join(", ", node.ChildNodes.Select(EmitNode));

            // ── Range value: expr To expr (2 children after "To" is removed) ───
            case CrystalFormulaGrammar.CaseValueRule:
                return node.ChildNodes.Count == 2
                    ? $"{EmitNode(node.ChildNodes[0])} To {EmitNode(node.ChildNodes[1])}"
                    : EmitNode(node.ChildNodes[0]);

            // ── Function call ──────────────────────────────────────────────────
            // After MarkPunctuation removes (, ) → children: [id] or [id, argList]
            case CrystalFormulaGrammar.FuncCallRule:
                return EmitFuncCall(node);

            // ── Argument list ──────────────────────────────────────────────────
            case CrystalFormulaGrammar.ArgListRule:
                return string.Join(", ", node.ChildNodes.Select(EmitNode));

            // ── Array literal ──────────────────────────────────────────────────
            // Reached only for a literal no enclosing construct consumed — Join's is
            // rewritten into a concatenation in EmitFuncCall before this runs. RDL has
            // no array type to emit, so the elements are spread as the comma-separated
            // list they already are, which is what a function documented as taking "an
            // array" (Maximum([1,2,3])) means once there are no arrays.
            case CrystalFormulaGrammar.ArrayLitRule:
                return string.Join(", ", GetArrayElements(node).Select(EmitNode));

            case CrystalFormulaGrammar.ArrayElemsRule:
                return string.Join(", ", node.ChildNodes.Select(EmitNode));

            // ── Terminals ──────────────────────────────────────────────────────
            case CrystalFormulaGrammar.NumberTerm:
                return node.Token.ValueString;

            case CrystalFormulaGrammar.StringDqTerm:
            case CrystalFormulaGrammar.StringSqTerm:
                return $"\"{EscapeVb(node.Token.Value?.ToString() ?? "")}\"";

            // Crystal's own #date# literal syntax (e.g. #01/01/1900#, #17 Feb 2016
            // 23:59:59#) has no equivalent in RDL's expression grammar -- confirmed by
            // inspection of the RDL engine's own parser, which has no production for a
            // bare '#...#' token; that is VB6/classic-VB syntax, not part of the RDL
            // expression language this engine implements. Emitting the raw token
            // unchanged (as before) produced literally invalid RDL text and failed at
            // parse time with "Constant or Identifier expected... Found '#'" the moment
            // any formula used one, which real Crystal reports do constantly for
            // hardcoded date comparisons. CDate("...") on the same inner text is the
            // RDL-legal equivalent, using the same CDate coercion this emitter already
            // relies on elsewhere for date handling.
            case CrystalFormulaGrammar.DateLitTerm:
                return $"CDate(\"{EscapeVb(node.Token.ValueString.Trim('#').Trim())}\")";

            case CrystalFormulaGrammar.FieldRefTerm:
                return EmitFieldRef(node.Token.ValueString);

            case CrystalFormulaGrammar.IdentTerm:
                return EmitIdent(node.Token.ValueString);

            // ── Bare (unbracketed) references ───────────────────────────────────
            // Table.Column -> Fields!Column.Value (the table half is discarded, same
            // as EmitFieldRef already does for the braced {Table.Column} form).
            case CrystalFormulaGrammar.DottedRefRule:
            {
                string columnName = node.ChildNodes[^1].Token?.ValueString ?? "";
                return $"Fields!{FormulaTranspiler.SanitizeIdentifier(columnName)}.Value";
            }

            // @FormulaName -> Fields!FormulaName.Value, same as braced {@FormulaName}.
            case CrystalFormulaGrammar.AtRefRule:
            {
                string atName = node.ChildNodes[0].Token?.ValueString ?? "";
                return $"Fields!{FormulaTranspiler.SanitizeIdentifier(atName)}.Value";
            }

            // #RunningTotalName -> Fields!RunningTotalName.Value (running totals are
            // emitted as DataSet Fields too — see RdlConverter.WriteDataSets).
            case CrystalFormulaGrammar.HashRefRule:
            {
                string hashName = node.ChildNodes[0].Token?.ValueString ?? "";
                return $"Fields!{FormulaTranspiler.SanitizeIdentifier(hashName)}.Value";
            }

            // ── String slicing ───────────────────────────────────────────────────
            // Crystal's postfix "[n]" / "[n To m]" (1-based, inclusive) on a string
            // value. After MarkPunctuation removes [, ], To, children are:
            //   [base, index]        -> single character
            //   [base, from, to]     -> substring
            // VB.NET's Mid(str, start, length) uses the same 1-based start, so this
            // maps directly rather than needing any index-shifting.
            case CrystalFormulaGrammar.SliceExprRule:
            {
                string baseExpr = EmitNode(node.ChildNodes[0]);
                if (node.ChildNodes.Count == 2)
                {
                    string index = EmitNode(node.ChildNodes[1]);
                    // Subscripting an *array* selects an element, not a character. Crystal's
                    // Split returns an array, so "Split({x}, "-")[2]" means the second
                    // field — Mid would take the second character of it instead. The engine
                    // has no array indexing, so the pair collapses into one call.
                    if (baseExpr.StartsWith("Split(", StringComparison.OrdinalIgnoreCase)
                        && baseExpr.EndsWith(")", StringComparison.Ordinal))
                        return $"SplitPart({baseExpr[6..^1]}, {index})";
                    return $"Mid({baseExpr}, {index}, 1)";
                }
                string from = EmitNode(node.ChildNodes[1]);
                string to   = EmitNode(node.ChildNodes[2]);
                return $"Mid({baseExpr}, {from}, ({to}) - ({from}) + 1)";
            }

            // Crystal's array-literal indexing, ["Sun","Mon",...][Weekday({X.Date})] —
            // the RDL engine's Choose(index, choice1, choice2, ...) is 1-based (confirmed
            // by reading FunctionChoose.cs directly: index 1 selects the first choice
            // argument), matching Crystal's own array-literal indexing exactly, and
            // Weekday() already returns 1=Sunday..7=Saturday, the same convention — no
            // index adjustment needed in either direction.
            case CrystalFormulaGrammar.ArrayIndexExprRule:
            {
                string idx = EmitNode(node.ChildNodes[1]);
                var elems = GetArrayElements(node.ChildNodes[0]).Select(EmitNode);
                return $"Choose({idx}, {string.Join(", ", elems)})";
            }

            // Boolean/null keyword literals
            case "True":  return "True";
            case "False": return "False";
            case "Null":  return "Nothing";

            // ── Fallback: concatenate children ────────────────────────────────
            default:
                if (node.Token != null)
                    return node.Token.ValueString;
                return string.Join(" ", node.ChildNodes.Select(EmitNode));
        }
    }

    // ── Expression dispatch ───────────────────────────────────────────────────

    private static string EmitExpr(ParseTreeNode node)
    {
        int n = node.ChildNodes.Count;

        // Binary:  left op right  (3 children, middle is operator terminal)
        if (n == 3)
        {
            var mid = node.ChildNodes[1];
            string opStr = (mid.Token?.ValueString ?? mid.Term.Name).ToUpper();

            // In-expression: left "In" caseValueList (set membership), or left "In" expr
            // (Crystal's string-containment form, {X} in "USA" — substring test).
            if (opStr == "IN")
            {
                if (node.ChildNodes[2].Term.Name == CrystalFormulaGrammar.CaseValueListRule)
                    return EmitInExpr(node.ChildNodes[0], node.ChildNodes[2]);
                return $"(InStr({EmitNode(node.ChildNodes[2])}, {EmitNode(node.ChildNodes[0])}) > 0)";
            }

            string left  = EmitNode(node.ChildNodes[0]);
            string op    = NormalizeOp(mid.Token?.ValueString ?? mid.Term.Name);
            string right = EmitNode(node.ChildNodes[2]);
            return $"({left} {op} {right})";
        }

        // Bare range test: left "In" lo hi  (4 children) — Crystal's {X} in A to B,
        // equivalent to (X >= A And X <= B). "To" is grammar punctuation (MarkPunctuation
        // strips it from the tree, confirmed by inspection), so this rule's real shape is
        // 4 children, not 5 as the grammar's "+To+" text might suggest -- distinguished
        // from the 3-child "In" forms above purely by count, and verified via --parsetest
        // (a first attempt assuming 5 children silently fell through to the default
        // space-joined fallback below, producing visibly wrong output with "to" missing
        // entirely -- caught before this went further, not shipped on the first guess).
        if (n == 4)
        {
            string rangeLeft = EmitNode(node.ChildNodes[0]);
            string rangeLo   = EmitNode(node.ChildNodes[2]);
            string rangeHi   = EmitNode(node.ChildNodes[3]);
            return $"(({rangeLeft} >= {rangeLo}) And ({rangeLeft} <= {rangeHi}))";
        }

        // Unary:  op operand  (2 children, first is operator keyword/symbol)
        if (n == 2)
        {
            var opToken = node.ChildNodes[0];
            string opStr = (opToken.Token?.ValueString ?? opToken.Term.Name).ToUpper();
            string operand = EmitNode(node.ChildNodes[1]);

            if (opStr == "NOT") return $"Not ({operand})";
            if (opStr == "-")   return $"(-{operand})";
            return operand;  // unary + is a no-op
        }

        // Transparent
        if (n == 1) return EmitNode(node.ChildNodes[0]);

        return string.Join(" ", node.ChildNodes.Select(EmitNode));
    }

    // ── Select Case → Switch() ───────────────────────────────────────────────

    private static string EmitSelectCase(ParseTreeNode node)
    {
        // children: [disc, caseClauseList] or [disc, caseClauseList, defaultExpr]
        string disc = EmitNode(node.ChildNodes[0]);
        var clauseList = node.ChildNodes[1];
        ParseTreeNode? defaultExpr = node.ChildNodes.Count >= 3 ? node.ChildNodes[2] : null;

        var sb = new StringBuilder("Switch(");
        bool first = true;

        foreach (var clause in clauseList.ChildNodes)
        {
            // After MarkPunctuation removes Case/Is/Else/:, clause children:
            //   Case Else    → [result]
            //   Case Is op v → [op-token, rhs-expr, result-expr]
            //   Case list    → [caseValueList, result-expr]
            int cn = clause.ChildNodes.Count;

            if (cn == 1)
            {
                // Case Else / Default in list form — treat as default
                if (!first) sb.Append(", ");
                sb.Append($"True, {EmitNode(clause.ChildNodes[0])}");
                first = false;
            }
            else if (cn == 3 && clause.ChildNodes[0].Token != null
                              && IsRelOp(clause.ChildNodes[0].Token.ValueString))
            {
                // Case Is <op> val : result — e.g. "Case Is < 100 : ..."
                string op   = NormalizeOp(clause.ChildNodes[0].Token.ValueString);
                string rhs  = EmitNode(clause.ChildNodes[1]);
                string res  = EmitNode(clause.ChildNodes[2]);
                if (!first) sb.Append(", ");
                sb.Append($"({disc} {op} {rhs}), {res}");
                first = false;
            }
            else if (cn >= 2)
            {
                // Case valueList : result
                var valListNode = clause.ChildNodes[0];
                string result = EmitNode(clause.ChildNodes[^1]);
                string cond = BuildCaseCond(disc, valListNode);
                if (!first) sb.Append(", ");
                sb.Append($"{cond}, {result}");
                first = false;
            }
        }

        if (defaultExpr != null)
        {
            if (!first) sb.Append(", ");
            sb.Append($"True, {EmitNode(defaultExpr)}");
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildCaseCond(string disc, ParseTreeNode valListNode)
    {
        if (valListNode.Term.Name != CrystalFormulaGrammar.CaseValueListRule)
        {
            // Single value node — emit as equality
            return $"({disc} = {EmitNode(valListNode)})";
        }

        var parts = new List<string>();
        foreach (var v in valListNode.ChildNodes)
        {
            if (v.Term.Name == CrystalFormulaGrammar.CaseValueRule)
            {
                if (v.ChildNodes.Count == 2)  // range: lo To hi
                {
                    string lo = EmitNode(v.ChildNodes[0]);
                    string hi = EmitNode(v.ChildNodes[1]);
                    parts.Add($"({disc} >= {lo} AndAlso {disc} <= {hi})");
                }
                else
                {
                    string val = EmitNode(v.ChildNodes[0]);
                    parts.Add($"({disc} = {val})");
                }
            }
            else
            {
                parts.Add($"({disc} = {EmitNode(v)})");
            }
        }
        return parts.Count == 1
            ? parts[0]
            : $"({string.Join(" OrElse ", parts)})";
    }

    // ── In-expression ─────────────────────────────────────────────────────────

    private static string EmitInExpr(ParseTreeNode leftNode, ParseTreeNode listNode)
    {
        string disc = EmitNode(leftNode);
        var parts = new List<string>();

        IEnumerable<ParseTreeNode> values =
            listNode.Term.Name == CrystalFormulaGrammar.CaseValueListRule
                ? listNode.ChildNodes
                : [listNode];

        foreach (var v in values)
        {
            if (v.Term.Name == CrystalFormulaGrammar.CaseValueRule && v.ChildNodes.Count == 2)
            {
                string lo = EmitNode(v.ChildNodes[0]);
                string hi = EmitNode(v.ChildNodes[1]);
                parts.Add($"({disc} >= {lo} AndAlso {disc} <= {hi})");
            }
            else
            {
                string val = EmitNode(v);
                parts.Add($"({disc} = {val})");
            }
        }

        return parts.Count == 0 ? "False"
             : parts.Count == 1 ? parts[0]
             : $"({string.Join(" OrElse ", parts)})";
    }

    // ── Function call ─────────────────────────────────────────────────────────

    private static string EmitFuncCall(ParseTreeNode node)
    {
        // Children after MarkPunctuation removes ( ): [id] or [id, argList]
        string funcName = node.ChildNodes[0].Token?.ValueString ?? "";

        // Crystal's HasValue({?p}) asks whether a parameter was answered. The engine has no
        // such function - it reached it verbatim and failed with "Function HasValue is not
        // known", which is fatal and loses the whole report. It is the negation of
        // IsNothing, so it needs wrapping rather than renaming.
        if (string.Equals(funcName, "HasValue", StringComparison.OrdinalIgnoreCase)
            && node.ChildNodes.Count > 1)
        {
            var hvArgs = GetArgNodes(node);
            if (hvArgs.Count == 1)
                return $"Not (IsNothing({EmitNode(hvArgs[0])}))";
        }

        // Crystal's Join(array, delimiter) concatenates an array's elements with the
        // delimiter between them. The engine has neither a Join function nor any array
        // type, so the only form with a translation is the one Crystal reports actually
        // write — a literal array — and that one is just the concatenation it stands for:
        // Join([a, b, c], " - ") → (a & " - " & b & " - " & c). A Join over a non-literal
        // (Join(Split(...), ",")) has no array to unroll and is left alone.
        if (string.Equals(funcName, "Join", StringComparison.OrdinalIgnoreCase))
        {
            var joinArgs = GetArgNodes(node);
            if (joinArgs.Count is 1 or 2
                && joinArgs[0].Term.Name == CrystalFormulaGrammar.ArrayLitRule)
            {
                var elems = GetArrayElements(joinArgs[0]).Select(EmitNode).ToList();
                // Crystal's one-argument Join concatenates with no separator.
                string sep = joinArgs.Count == 2 ? EmitNode(joinArgs[1]) : "\"\"";
                return elems.Count == 0 ? "\"\"" : $"({string.Join($" & {sep} & ", elems)})";
            }
        }

        if (FunctionMap.TryGetValue(funcName, out string? rdl))
            funcName = rdl;

        if (funcName.Contains('!'))  // Globals!PageNumber etc.
            return funcName;

        // Property-style mappings (e.g. Math.PI) must not be called as functions
        if (funcName.Contains('.') && !funcName.EndsWith(')'))
            return funcName;

        // Crystal's "Sum grouped by field" shorthand — =Sum({Orders.Amount},
        // {Customer.Name}) — passes the group-by field as the 2nd argument. This
        // engine's Sum/Count/etc. only accept a *DataSet* name for a quoted scope
        // (never a Grouping name — that's RunningValue-only), so a group-by field
        // reference here has no valid translation; drop it and emit the unscoped
        // 1-arg form instead of a scope that would always fail to resolve.
        // The test is on the *emitted* argument rather than only the node shape: the
        // engine's rule is that a scope must be a constant, so any second argument that
        // comes out as a field reference is invalid however it was written, and shapes
        // TryGetPlainColumnName doesn't recognize otherwise reach the engine as
        // "Fields!X.Value function's scope must be a constant".
        //
        // Crystal also has a 3-arg form for the same shorthand — Sum({Orders.Amount},
        // {Orders.Date}, "daily") — adding a date-grouping interval ("daily", "weekly",
        // ...) as a 3rd argument. GetTwoArgNodes only matched exactly 2 args, so this
        // shape fell straight through to the naive emitter, which passed the group-by
        // field through unchanged as the RDL scope argument and hit the exact same
        // "scope must be a constant" error this guard exists to prevent. Same
        // reasoning applies regardless of the interval argument's presence: there is no
        // RDL translation for either the group-by field or the interval, so both are
        // dropped together.
        //
        // A real report's Sum(field, Val({X.StockAccount})) wraps the group-by field in
        // a function call, so the emitted argument starts with "Val(" rather than
        // "Fields!" and slipped past the check above — same underlying rule ("scope
        // must be a constant"), just not caught by name-shape matching. Generalized to
        // the actual rule instead of another specific shape: the *only* valid scope RDL
        // accepts is a quoted string literal (a DataSet name); anything else, wrapped or
        // bare, is invalid and gets dropped the same way.
        if (ScopedAggregateFunctions.Contains(funcName) && GetArgNodes(node) is { Count: 2 or 3 } scopedArgs
            && !EmitNode(scopedArgs[1]).StartsWith('"'))
        {
            return $"{funcName}({EmitNode(scopedArgs[0])})";
        }

        string args = node.ChildNodes.Count >= 2
            ? EmitNode(node.ChildNodes[1])
            : "";

        // Crystal's Date() is overloaded: Date(y,m,d) constructs (→ DateSerial, the
        // FunctionMap default) but Date(x) coerces a value to a date — a different VB
        // function entirely. Pick by arity, or the 1-arg form emits DateSerial(x), which
        // has no such overload and dies in reflection binding ("DateSerial is not known").
        if (funcName == "DateSerial" && GetArgCount(node) == 1)
            funcName = "CDate";

        // Crystal's NextIsNull({field}) tests whether the field is null on the *next*
        // record. This engine already has a first-class Next(expression[, scope])
        // aggregate function (FunctionAggrNext) that walks the current data scope
        // forward from the current row and returns null past the end — exactly the
        // row-lookahead Crystal's own Next/NextIsNull family needs, already fully
        // implemented and unrelated to this converter (confirmed by reading
        // FunctionAggrNext.cs directly, not assumed). No engine change needed: rewrite
        // to the RDL-native equivalent instead of trying to invent a new capability.
        // Previous/PreviousIsNull would need the identical treatment (Previous() exists
        // the same way) but aren't rewritten here — no repro has needed it yet, and
        // this only claims to fix what's actually been observed.
        if (funcName.Equals("NextIsNull", StringComparison.OrdinalIgnoreCase) && GetArgCount(node) == 1)
            return $"IsNothing(Next({EmitNode(GetArgNodes(node)[0])}))";

        // Crystal's GroupName({field}) is "the current group's value for this group-by
        // field" — in a grouped RDL row context that is simply the field itself. There
        // is no engine function to call; unwrap to the argument. The 2-arg form adds a
        // date-grouping condition ("daily", "monthly", ...) — degrade to the field too.
        if (funcName.Equals("GroupName", StringComparison.OrdinalIgnoreCase) && GetArgCount(node) >= 1)
            return EmitNode(GetArgNodes(node)[0]);

        // Crystal indents hierarchical groups with HierarchyLevel(GroupingLevel({field})):
        // the inner call names the group and the outer one gives its depth, and the result
        // is multiplied out into a left margin. RDL has no hierarchical grouping to name,
        // but its Level() is the same quantity - the nesting depth of the current group -
        // so both collapse to it. The argument only identified the group and is dropped
        // with it; Level() reads the depth from the row it is evaluated on.
        if (funcName.Equals("HierarchyLevel", StringComparison.OrdinalIgnoreCase)
            || funcName.Equals("GroupingLevel", StringComparison.OrdinalIgnoreCase))
            return "Level()";

        // Crystal's NthLargest(N, field [, groupField]) is the Nth largest value in a
        // set. Every observed use is N = 1, which is exactly Max(field). The optional
        // third argument names the group to evaluate within; RDL scope arguments accept
        // only DataSet names, so it is dropped for the same reason Sum's is above. An N
        // that isn't a literal 1 has no Max equivalent and is left alone deliberately —
        // better to surface than to silently report the wrong number.
        if (funcName.Equals("NthLargest", StringComparison.OrdinalIgnoreCase))
        {
            var nthArgs = GetArgNodes(node);
            if (nthArgs.Count >= 2 && EmitNode(nthArgs[0]).Trim() == "1")
                return $"Max({EmitNode(nthArgs[1])})";
        }

        // Crystal's ProperCase(x) maps to VB's StrConv(x, conversion). The conversion is
        // emitted as VB's plain numeric constant (3 = proper case) — the VbStrConv enum
        // has no meaning to this engine's expression parser, which resolves bare dotted
        // names as identifiers and reports "VbStrConv.ProperCase is an unknown identifer".
        if (funcName == "StrConv" && GetArgCount(node) == 1)
            args = $"{args}, 3";

        return $"{funcName}({args})";
    }

    /// <summary>
    /// The argument nodes of a funcCall. A single argument is the argument node itself
    /// rather than an argList, so unwrapping that shape is needed everywhere arguments
    /// are inspected.
    /// </summary>
    private static IList<ParseTreeNode> GetArgNodes(ParseTreeNode funcCallNode)
    {
        if (funcCallNode.ChildNodes.Count < 2) return [];
        var argListNode = funcCallNode.ChildNodes[1];
        return argListNode.Term.Name == CrystalFormulaGrammar.ArgListRule
            ? argListNode.ChildNodes
            : [argListNode];
    }

    /// <summary>
    /// The element nodes of an arrayLit. Irony collapses a one-element list, so the
    /// literal's only child is the arrayElems list for two or more elements and the
    /// element itself for one — the same unwrapping argList needs.
    /// </summary>
    private static IList<ParseTreeNode> GetArrayElements(ParseTreeNode arrayLitNode)
    {
        if (arrayLitNode.ChildNodes.Count == 0) return [];
        var inner = arrayLitNode.ChildNodes[0];
        return inner.Term.Name == CrystalFormulaGrammar.ArrayElemsRule
            ? inner.ChildNodes
            : [inner];
    }

    private static int GetArgCount(ParseTreeNode funcCallNode)
        => GetArgNodes(funcCallNode).Count;

    private static (ParseTreeNode, ParseTreeNode)? GetTwoArgNodes(ParseTreeNode funcCallNode)
    {
        var args = GetArgNodes(funcCallNode);
        return args.Count == 2 ? (args[0], args[1]) : null;
    }

    // Extracts a bare column name from an argument node *without* emitting it as the
    // usual Fields!X.Value — needed to check for a group-scope match before deciding
    // whether this argument is a value or a Grouping-name reference. Returns null for
    // anything that isn't a plain column reference (parameter/formula/running-total
    // refs, expressions, literals, ...), which correctly leaves those to the normal
    // EmitNode path unchanged.
    private static string? TryGetPlainColumnName(ParseTreeNode node)
    {
        while (node.ChildNodes.Count == 1
               && node.Term.Name != CrystalFormulaGrammar.AtRefRule
               && node.Term.Name != CrystalFormulaGrammar.HashRefRule)
            node = node.ChildNodes[0];

        switch (node.Term.Name)
        {
            case CrystalFormulaGrammar.FieldRefTerm:
            {
                string inner = node.Token.ValueString.TrimStart('{').TrimEnd('}');
                if (inner.StartsWith('?') || inner.StartsWith('@')) return null;
                int dot = inner.LastIndexOf('.');
                return dot >= 0 ? inner[(dot + 1)..] : inner;
            }
            case CrystalFormulaGrammar.DottedRefRule:
                return node.ChildNodes[^1].Token?.ValueString;
            case CrystalFormulaGrammar.IdentTerm:
                return node.Token.ValueString;
            default:
                return null;
        }
    }

    // ── Field references ──────────────────────────────────────────────────────

    private static string EmitFieldRef(string raw)
    {
        string inner = raw.TrimStart('{').TrimEnd('}');

        if (inner.StartsWith('?'))
            return $"Parameters!{FormulaTranspiler.SanitizeIdentifier(FormulaTranspiler.StripSapParamWrapper(inner[1..]))}.Value";

        if (inner.StartsWith('@'))
            return $"Fields!{FormulaTranspiler.SanitizeIdentifier(inner[1..])}.Value";

        // {#RunningTotal} — the running-total marker was never stripped in the braced
        // form (bare #X and {@X} both were), so SanitizeIdentifier turned "#RTotal0"
        // into "_RTotal0" while the DataSet declares the field as "RTotal0".
        if (inner.StartsWith('#'))
            return $"Fields!{FormulaTranspiler.SanitizeIdentifier(inner[1..])}.Value";

        int dot = inner.LastIndexOf('.');
        string fieldName = dot >= 0 ? inner[(dot + 1)..] : inner;
        return $"Fields!{FormulaTranspiler.SanitizeIdentifier(fieldName)}.Value";
    }

    private static string EmitIdent(string name)
    {
        return BareIdentMap.TryGetValue(name, out string? mapped) ? mapped : name;
    }

    // ── Operator normalisation ────────────────────────────────────────────────

    private static string NormalizeOp(string op)
    {
        // VB.NET keyword operators must be title-cased
        if (VbKeywordOps.Contains(op))
            return char.ToUpper(op[0]) + op[1..].ToLower();
        // Crystal (SAP Business One flavor) also spells modulus "%", but the RDL
        // engine's own expression grammar only recognizes VB's "Mod" keyword, not the
        // symbol — translate at emission time rather than trying to teach the RDL
        // parser a new symbol for something it already has a working keyword for.
        if (op == "%")
            return "Mod";
        return op;  // symbols stay as-is
    }

    private static bool IsRelOp(string s) =>
        s is "=" or "<>" or "<" or ">" or "<=" or ">=";

    private static string EscapeVb(string s) => s.Replace("\"", "\"\"");
}
