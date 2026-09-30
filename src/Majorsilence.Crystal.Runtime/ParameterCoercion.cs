using System.Collections;
using System.Globalization;
using Majorsilence.Crystal.Converter;
using Majorsilence.Crystal.Model;
using Majorsilence.Crystal.Model.Fields;

namespace Majorsilence.Crystal.Runtime;

/// <summary>
/// Turns the values a caller supplies for a report's parameters into what the render
/// engine is given, by the parameter's declared type, the way the real engine's callers
/// do it: names matched exactly then case-insensitively; booleans from true/false or 0/1;
/// numbers as an integer when they are one and a decimal otherwise, parsed invariant-first
/// and then in the current culture; dates taken as given, else ISO 8601, else invariant,
/// else current culture; strings as given. A blank value is the type's stand-in (false, 0,
/// today, a single space, since the engine refuses an empty string), and so is a required
/// parameter that was not supplied, with a warning. A value that cannot be read for its
/// type is a warning and the stand-in, never a failed render.
/// </summary>
public static class ParameterCoercion
{
    public static (IDictionary? Values, IReadOnlyList<string> Warnings) Coerce(
        ReportDefinition report, IReadOnlyDictionary<string, object?> supplied)
    {
        var warnings = new List<string>();
        var declared = report.Fields.OfType<ParameterField>().ToList();
        var values = new Hashtable();

        foreach (var (name, raw) in supplied)
        {
            var field = Find(declared, name);
            if (field is null)
            {
                warnings.Add($"Parameters: no parameter named '{name}'");
                continue;
            }
            values[RdlConverter.ParameterRdlName(field)] = CoerceValue(field, raw, warnings);
        }

        // A parameter with no saved default has nothing to render with unless the caller
        // supplied it; the real engine's callers set it empty and warn rather than fail.
        foreach (var field in declared.Where(f => string.IsNullOrEmpty(f.DefaultValue)))
        {
            string key = RdlConverter.ParameterRdlName(field);
            if (values.ContainsKey(key)) continue;
            warnings.Add($"Parameters: '{field.Name}' is required and was not supplied; rendered with an empty value");
            values[key] = StandIn(field);
        }

        return (values.Count == 0 ? null : values, warnings);
    }

    private static ParameterField? Find(List<ParameterField> declared, string name)
    {
        string wanted = Bare(name);
        return declared.FirstOrDefault(f => string.Equals(Bare(f.Name), wanted, StringComparison.Ordinal))
            ?? declared.FirstOrDefault(f => string.Equals(Bare(f.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string Bare(string name) =>
        FormulaTranspiler.StripSapParamWrapper(name).Trim().Trim('{', '}').TrimStart('?', '@');

    private static object CoerceValue(ParameterField field, object? raw, List<string> warnings)
    {
        string text = raw?.ToString() ?? "";
        bool blank = string.IsNullOrWhiteSpace(text);
        try
        {
            switch (field.DataType)
            {
                case "Boolean":
                    if (raw is bool b) return b;
                    if (blank) return false;
                    return text.Trim() switch
                    {
                        "0" => false,
                        "1" => true,
                        _ => bool.Parse(text.Trim())
                    };

                case "Float64" or "Float32" or "Int16" or "Int32" or "Currency":
                    if (raw is int or long or short or byte) return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    if (raw is decimal or double or float) return Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                    if (blank) return 0;
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
                    return ParseDecimal(text);

                case "DateTime":
                    return ParseDate(raw, text, blank);

                default:
                    return blank ? " " : text;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            warnings.Add($"Parameters: '{field.Name}' ({field.DataType}) cannot take '{text}'; rendered with an empty value");
            return StandIn(field);
        }
    }

    // Invariant first, with no group separators, so "1,5" is not read as 15 and falls
    // through to the culture that means a comma as the decimal point.
    private static decimal ParseDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d)
            ? d
            : decimal.Parse(text, NumberStyles.Number, CultureInfo.CurrentCulture);

    private static DateTime ParseDate(object? raw, string text, bool blank)
    {
        if (raw is DateTime dt) return dt;
        if (raw is DateOnly d) return d.ToDateTime(TimeOnly.MinValue);
        if (blank) return DateTime.Today;
        if (DateTime.TryParseExact(text, ["o", "s"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return iso;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant))
            return invariant;
        return DateTime.Parse(text, CultureInfo.CurrentCulture);
    }

    private static object StandIn(ParameterField field) => field.DataType switch
    {
        "Boolean" => false,
        "Float64" or "Float32" or "Int16" or "Int32" or "Currency" => 0,
        "DateTime" => DateTime.Today,
        _ => " "
    };
}
