using System.Text;
using System.Text.RegularExpressions;

namespace Majorsilence.Crystal.Converter.Formula;

/// <summary>
/// A Basic-syntax formula written as statements, restated in Crystal syntax for
/// <see cref="CrystalStatements"/> to reduce (#19).
///
/// Basic syntax is the dialect most of the production reports measured here are written in,
/// and its formulas declare variables far more readily: an address built up line by line is
/// <c>Shared MailingAddress As String</c>, an assignment, a few <c>If ... Then ... End If</c>
/// blocks appending to it, and <c>formula = MailingAddress</c>. The variable is Shared so that a
/// subreport can read it, but for the formula's own value it is written before it is read,
/// and the formula depends only on the current record. Those degraded to an empty value.
///
/// The statements map one for one: a declaration to a Crystal declaration (Dim is Local),
/// <c>x = value</c> at the start of a statement to <c>x := value</c>, an If block (with
/// ElseIf and Else) to nested <c>if ... then ( ... ) else ( ... )</c>, and
/// <c>formula = value</c> to an assignment to a result variable read at the end. Expressions
/// pass through: Basic's operators and functions are ones the Crystal-syntax transpiler
/// already reads. Anything else (Select Case, loops, Exit, several declarations on a line,
/// a variable with no type) gives up, and the formula keeps the treatment it had.
/// </summary>
internal static class BasicStatements
{
    private const string Result = "crystalFormulaResult";

    // One or more names, then the type: "Dim x As String", "Local xDay, xMonth, xYear As Number".
    // VB's own numeric type names are written too ("Global t As Double"), and mean Number.
    private static readonly Regex Declaration = new(
        @"^(Dim|Shared|Global|Local)\s+([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s+As\s+(String|Number|Currency|Boolean|Date|DateTime|Time|Double|Single|Integer|Long|Decimal)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Assignment = new(
        @"^([A-Za-z_]\w*)\s*=(?!=)\s*(.+)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private sealed class GiveUp : Exception;

    /// <summary>
    /// The formula in Crystal syntax, or null. <paramref name="resultType"/> is the formula's
    /// recorded type (FormulaField.ResultType), which decides what an unassigned result is.
    /// </summary>
    internal static string? ToCrystal(string formula, string? resultType)
    {
        try
        {
            var lines = Lines(formula);
            if (lines.Count == 0) return null;
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            string body = Block(lines, ref pos, declared, stopAt: null);
            if (pos != lines.Count || !body.Contains(Result + " :=")) return null;
            string resultVar = resultType switch
            {
                "Float64" => "NumberVar",
                "Currency" => "CurrencyVar",
                "Boolean" => "BooleanVar",
                "Date" or "DateTime" => "DateTimeVar",
                _ => "StringVar",
            };
            return $"Local {resultVar} {Result};\n{body}{Result}";
        }
        catch (GiveUp)
        {
            return null;
        }
    }

    // Statements until the end, or until a line starting one of stopAt (ElseIf, Else, End If)
    // at this level, which is left for the caller.
    private static string Block(List<string> lines, ref int pos, HashSet<string> declared, string[]? stopAt)
    {
        var sb = new StringBuilder();
        while (pos < lines.Count)
        {
            string line = lines[pos];
            if (stopAt is not null && stopAt.Any(s => Regex.IsMatch(line, $@"^{s}\b", RegexOptions.IgnoreCase)))
                break;
            pos++;
            sb.Append(Statement(line, lines, ref pos, declared));
        }
        return sb.ToString();
    }

    private static string Statement(string line, List<string> lines, ref int pos, HashSet<string> declared)
    {
        var d = Declaration.Match(line);
        if (d.Success)
        {
            string scope = d.Groups[1].Value.Equals("Dim", StringComparison.OrdinalIgnoreCase) ? "Local" : d.Groups[1].Value;
            string type = d.Groups[3].Value.ToLowerInvariant() switch
            {
                "string" => "StringVar",
                "currency" => "CurrencyVar",
                "boolean" => "BooleanVar",
                "date" => "DateVar",
                "datetime" => "DateTimeVar",
                "time" => "TimeVar",
                _ => "NumberVar",
            };
            var sb = new StringBuilder();
            foreach (var name in d.Groups[2].Value.Split(',').Select(n => n.Trim()))
            {
                declared.Add(name);
                sb.Append($"{scope} {type} {name};\n");
            }
            return sb.ToString();
        }

        if (Regex.IsMatch(line, @"^If\b", RegexOptions.IgnoreCase))
            return If(line, lines, ref pos, declared);

        var a = Assignment.Match(line);
        if (a.Success)
        {
            string name = a.Groups[1].Value;
            string value = a.Groups[2].Value.Trim();
            if (name.Equals("formula", StringComparison.OrdinalIgnoreCase)) return $"{Result} := ({value});\n";
            if (declared.Contains(name)) return $"{name} := ({value});\n";
        }
        throw new GiveUp();
    }

    // A block If (the line ends in Then) or a one-line one ("If c Then stmt [Else stmt]").
    private static string If(string line, List<string> lines, ref int pos, HashSet<string> declared)
    {
        int then = TopLevelWord(line, "Then");
        if (then < 0) throw new GiveUp();
        string condition = line[2..then].Trim();
        string rest = line[(then + 4)..].Trim();
        if (condition.Length == 0) throw new GiveUp();

        if (rest.Length > 0)
        {
            // "If c Then x = 1 End If" on one line closes itself.
            rest = Regex.Replace(rest, @"\s+End\s+If\s*$", "", RegexOptions.IgnoreCase);
            int els = TopLevelWord(rest, "Else");
            string thenPart = els < 0 ? rest : rest[..els].Trim();
            string elsePart = els < 0 ? "" : rest[(els + 4)..].Trim();
            int p = 0;
            string thenBody = Statement(thenPart, [], ref p, declared);
            string elseBody = elsePart.Length == 0 ? "" : Statement(elsePart, [], ref p, declared);
            return elseBody.Length == 0
                ? $"if ({condition}) then (\n{thenBody});\n"
                : $"if ({condition}) then (\n{thenBody}) else (\n{elseBody});\n";
        }

        string[] stops = [@"ElseIf", @"Else\s*If", @"Else", @"End\s+If"];
        string thenBlock = Block(lines, ref pos, declared, stops);
        if (pos >= lines.Count) throw new GiveUp();
        string next = lines[pos];
        if (Regex.IsMatch(next, @"^End\s+If\b", RegexOptions.IgnoreCase))
        {
            pos++;
            return $"if ({condition}) then (\n{thenBlock});\n";
        }
        if (Regex.IsMatch(next, @"^(ElseIf|Else\s+If)\b", RegexOptions.IgnoreCase))
        {
            // The rest of the chain is an If of its own, ending at the same End If.
            string chained = Regex.Replace(next, @"^(ElseIf|Else\s+If)\b", "If", RegexOptions.IgnoreCase);
            pos++;
            string elseIf = If(chained, lines, ref pos, declared);
            return $"if ({condition}) then (\n{thenBlock}) else (\n{elseIf});\n";
        }
        // Else
        if (next.Trim().Length != 4) throw new GiveUp();
        pos++;
        string elseBlock = Block(lines, ref pos, declared, [@"End\s+If"]);
        if (pos >= lines.Count) throw new GiveUp();
        pos++;   // End If
        return $"if ({condition}) then (\n{thenBlock}) else (\n{elseBlock});\n";
    }

    // Where a keyword stands as a word outside strings and brackets, or -1.
    private static int TopLevelWord(string s, string word)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                     && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))
                     && (i + word.Length == s.Length || !char.IsLetterOrDigit(s[i + word.Length])))
                return i;
        }
        return -1;
    }

    // The formula's statements one per line: comments ("'" outside a string, "//" in some
    // files) removed, continuation lines (" _") joined, blank lines dropped. A ":" between
    // statements, or a Basic construct this does not follow, gives up.
    private static List<string> Lines(string formula)
    {
        var result = new List<string>();
        var pending = new StringBuilder();
        foreach (var raw in formula.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = StripComment(raw).Trim();
            if (line.EndsWith(" _") || line == "_")
            {
                pending.Append(line[..^1]).Append(' ');
                continue;
            }
            pending.Append(line);
            string full = pending.ToString().Trim();
            pending.Clear();
            if (full.Length == 0) continue;
            if (TopLevelChar(full, ':') >= 0) throw new GiveUp();
            if (Regex.IsMatch(full, @"^(Select|Case|For|Next|Do|Loop|While|Wend|Exit|ReDim|Function|Sub)\b", RegexOptions.IgnoreCase))
                throw new GiveUp();
            result.Add(full);
        }
        if (pending.Length > 0) throw new GiveUp();
        return result;
    }

    // A field name can hold an apostrophe ({Customer.Last Year's Sales}), so a {...}
    // reference is skipped like a string.
    private static string StripComment(string line)
    {
        bool quoted = false, field = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!quoted && !field && c == '{') field = true;
            else if (field) { if (c == '}') field = false; }
            else if (c == '"') quoted = !quoted;
            else if (!quoted && c == '\'') return line[..i];
            else if (!quoted && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static int TopLevelChar(string s, char ch)
    {
        bool quoted = false;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ch && depth == 0) return i;
        }
        return -1;
    }
}
