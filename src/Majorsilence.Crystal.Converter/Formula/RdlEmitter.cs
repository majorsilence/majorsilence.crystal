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
            ["chr"]             = "Chr",
            ["asc"]             = "Asc",
            ["strreverse"]      = "StrReverse",
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
            ["today"]           = "Today",
            ["currentdate"]     = "Today",
            ["currenttime"]     = "TimeOfDay",
            ["currentdatetime"] = "Now",
            ["timer"]           = "Timer",
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
            // Report state
            ["pagenumber"]      = "Globals!PageNumber",
            ["totalpagecount"]  = "Globals!TotalPages",
            ["reportname"]      = "Globals!ReportName",
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
        };

    // Operator tokens that are logical/relational and stay VB.NET keywords
    private static readonly HashSet<string> VbKeywordOps =
        new(StringComparer.OrdinalIgnoreCase)
        { "And", "Or", "Not", "Xor", "Eqv", "Imp", "Mod", "Like" };

    public static string Emit(ParseTree tree) =>
        tree.HasErrors() || tree.Root == null ? "\"\"" : EmitNode(tree.Root).Trim();

    private static string EmitNode(ParseTreeNode node)
    {
        string name = node.Term.Name;

        // ── Transparent single-child passthrough ──────────────────────────────
        if (node.ChildNodes.Count == 1 && name != CrystalFormulaGrammar.ArgListRule)
            return EmitNode(node.ChildNodes[0]);

        switch (name)
        {
            // ── Statement list: return only the last expression ────────────────
            case CrystalFormulaGrammar.StmtListRule:
                return EmitNode(node.ChildNodes[^1]);

            // ── Variable declaration: return its init value if present ─────────
            case CrystalFormulaGrammar.VarDeclRule:
            {
                // Children after MarkPunctuation removes Var, :=:
                //   [id]          → no initialiser, returns Nothing
                //   [id, expr]    → returns the expr
                var exprChild = node.ChildNodes.LastOrDefault(c =>
                    c.Term.Name == CrystalFormulaGrammar.ExprRule);
                return exprChild != null ? EmitNode(exprChild) : "Nothing";
            }

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

            // ── Terminals ──────────────────────────────────────────────────────
            case CrystalFormulaGrammar.NumberTerm:
                return node.Token.ValueString;

            case CrystalFormulaGrammar.StringDqTerm:
            case CrystalFormulaGrammar.StringSqTerm:
                return $"\"{EscapeVb(node.Token.Value?.ToString() ?? "")}\"";

            case CrystalFormulaGrammar.DateLitTerm:
                return node.Token.ValueString;

            case CrystalFormulaGrammar.FieldRefTerm:
                return EmitFieldRef(node.Token.ValueString);

            case CrystalFormulaGrammar.IdentTerm:
                return EmitIdent(node.Token.ValueString);

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

            // In-expression: left "In" caseValueList
            if (opStr == "IN")
                return EmitInExpr(node.ChildNodes[0], node.ChildNodes[2]);

            string left  = EmitNode(node.ChildNodes[0]);
            string op    = NormalizeOp(mid.Token?.ValueString ?? mid.Term.Name);
            string right = EmitNode(node.ChildNodes[2]);
            return $"({left} {op} {right})";
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
        if (FunctionMap.TryGetValue(funcName, out string? rdl))
            funcName = rdl;

        if (funcName.Contains('!'))  // Globals!PageNumber etc.
            return funcName;

        // Property-style mappings (e.g. Math.PI) must not be called as functions
        if (funcName.Contains('.') && !funcName.EndsWith(')'))
            return funcName;

        string args = node.ChildNodes.Count >= 2
            ? EmitNode(node.ChildNodes[1])
            : "";

        if (funcName == "StrConv" && !args.Contains("VbStrConv"))
            args = $"{args}, VbStrConv.ProperCase";

        return $"{funcName}({args})";
    }

    // ── Field references ──────────────────────────────────────────────────────

    private static string EmitFieldRef(string raw)
    {
        string inner = raw.TrimStart('{').TrimEnd('}');

        if (inner.StartsWith('?'))
            return $"Parameters!{FormulaTranspiler.SanitizeIdentifier(inner[1..])}.Value";

        if (inner.StartsWith('@'))
        {
            string fName = inner[1..];
            return $"\"\"  /* formula ref '{fName}' */";
        }

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
        return op;  // symbols stay as-is
    }

    private static bool IsRelOp(string s) =>
        s is "=" or "<>" or "<" or ">" or "<=" or ">=";

    private static string EscapeVb(string s) => s.Replace("\"", "\"\"");
}
