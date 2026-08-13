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
        if (node.ChildNodes.Count == 1
            && name != CrystalFormulaGrammar.ArgListRule
            && name != CrystalFormulaGrammar.AtRefRule
            && name != CrystalFormulaGrammar.HashRefRule)
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
                    return $"Mid({baseExpr}, {index}, 1)";
                }
                string from = EmitNode(node.ChildNodes[1]);
                string to   = EmitNode(node.ChildNodes[2]);
                return $"Mid({baseExpr}, {from}, ({to}) - ({from}) + 1)";
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

        // Crystal's "Sum grouped by field" shorthand — =Sum({Orders.Amount},
        // {Customer.Name}) — passes the group-by field as the 2nd argument. This
        // engine's Sum/Count/etc. only accept a *DataSet* name for a quoted scope
        // (never a Grouping name — that's RunningValue-only), so a group-by field
        // reference here has no valid translation; drop it and emit the unscoped
        // 1-arg form instead of a scope that would always fail to resolve.
        if (ScopedAggregateFunctions.Contains(funcName)
            && GetTwoArgNodes(node) is (ParseTreeNode arg1, ParseTreeNode arg2)
            && TryGetPlainColumnName(arg2) is not null)
        {
            return $"{funcName}({EmitNode(arg1)})";
        }

        string args = node.ChildNodes.Count >= 2
            ? EmitNode(node.ChildNodes[1])
            : "";

        if (funcName == "StrConv" && !args.Contains("VbStrConv"))
            args = $"{args}, VbStrConv.ProperCase";

        return $"{funcName}({args})";
    }

    private static (ParseTreeNode, ParseTreeNode)? GetTwoArgNodes(ParseTreeNode funcCallNode)
    {
        if (funcCallNode.ChildNodes.Count < 2) return null;
        var argListNode = funcCallNode.ChildNodes[1];
        var args = argListNode.Term.Name == CrystalFormulaGrammar.ArgListRule
            ? argListNode.ChildNodes
            : [argListNode];
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
