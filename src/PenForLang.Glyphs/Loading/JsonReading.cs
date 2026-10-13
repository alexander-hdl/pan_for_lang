using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Loading;

/// <summary>Приёмник замечаний одного файла.</summary>
internal sealed class IssueSink
{
    private readonly List<ValidationIssue> _issues;

    public IssueSink(string file, List<ValidationIssue> issues)
    {
        File = file;
        _issues = issues;
    }

    public string File { get; }

    /// <summary>Число ошибок во всём списке (для проверки «стало ли хуже»).</summary>
    public int ErrorCount => _issues.Count(i => i.Severity == IssueSeverity.Error);

    public void Error(string path, string code, string message) =>
        _issues.Add(new ValidationIssue(IssueSeverity.Error, code, File, path, message));

    public void Warning(string path, string code, string message) =>
        _issues.Add(new ValidationIssue(IssueSeverity.Warning, code, File, path, message));
}

/// <summary>Пределы правдоподобных координат глифа (в x-height): грубая защита от опечаток вроде <c>99</c> вместо <c>0.99</c>.</summary>
internal static class CoordinateLimits
{
    public const double MinU = -2.0;
    public const double MaxU = 8.0;
    public const double MinV = -3.0;
    public const double MaxV = 4.0;
}

/// <summary>Чтение полей JSON-объекта с путями полей в замечаниях и контролем неизвестных свойств.</summary>
internal sealed class ObjectReader
{
    private readonly JsonElement _element;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    private ObjectReader(JsonElement element, string path, IssueSink sink)
    {
        _element = element;
        Path = path;
        Sink = sink;
    }

    public string Path { get; }

    public IssueSink Sink { get; }

    /// <summary>Открывает объект; для не-объекта фиксирует ошибку и возвращает <c>null</c>.</summary>
    public static ObjectReader? Open(JsonElement element, string path, IssueSink sink)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            sink.Error(path, "json.type", $"expected an object but found {Describe(element)}");
            return null;
        }

        return new ObjectReader(element, path, sink);
    }

    public static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "an undefined value",
    };

    public static string Child(string path, string name) => path.Length == 0 ? name : path + "." + name;

    public static string Index(string path, int index) => path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    public string ChildPath(string name) => Child(Path, name);

    /// <summary>Сырое значение поля. Отсутствие обязательного поля — ошибка.</summary>
    public JsonElement? Get(string name, bool required = true)
    {
        _known.Add(name);
        if (_element.TryGetProperty(name, out JsonElement value))
        {
            return value;
        }

        if (required)
        {
            Sink.Error(ChildPath(name), "json.missing", $"required property '{name}' is missing");
        }

        return null;
    }

    public string? String(string name, bool required = true, bool allowEmpty = false)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : ReadString(value.Value, ChildPath(name), Sink, allowEmpty);
    }

    public double? Number(string name, double min, double max, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : ReadNumber(value.Value, ChildPath(name), Sink, min, max);
    }

    public int? Integer(string name, int min, int max, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : ReadInteger(value.Value, ChildPath(name), Sink, min, max);
    }

    public bool? Bool(string name, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : ReadBool(value.Value, ChildPath(name), Sink);
    }

    public Vec2? Point(string name, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : ReadPoint(value.Value, ChildPath(name), Sink);
    }

    public ObjectReader? Object(string name, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : Open(value.Value, ChildPath(name), Sink);
    }

    /// <summary>Массив: проверяет вид и число элементов.</summary>
    public JsonElement? Array(string name, int minItems, int maxItems, bool required = true)
    {
        JsonElement? value = Get(name, required);
        return value is null ? null : CheckArray(value.Value, ChildPath(name), Sink, minItems, maxItems);
    }

    /// <summary>Значение из набора допустимых имён.</summary>
    public T? EnumValue<T>(string name, IReadOnlyDictionary<string, T> names, bool required = true)
        where T : struct, Enum
    {
        string? text = String(name, required);
        if (text is null)
        {
            return null;
        }

        if (names.TryGetValue(text, out T value))
        {
            return value;
        }

        Sink.Error(ChildPath(name), "json.enum", $"unknown value '{text}', expected one of: {string.Join(", ", names.Keys)}");
        return null;
    }

    /// <summary>Фиксирует ошибку на каждое свойство, не запрошенное ни одним <c>Get</c>.</summary>
    public void RejectUnknown()
    {
        foreach (JsonProperty property in _element.EnumerateObject())
        {
            if (!_known.Contains(property.Name))
            {
                string allowed = string.Join(", ", _known.Order(StringComparer.Ordinal));
                Sink.Error(ChildPath(property.Name), "json.unknown", $"unknown property '{property.Name}', allowed: {allowed}");
            }
        }
    }

    public static string? ReadString(JsonElement element, string path, IssueSink sink, bool allowEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            sink.Error(path, "json.type", $"expected a string but found {Describe(element)}");
            return null;
        }

        string text = element.GetString() ?? string.Empty;
        if (!allowEmpty && text.Length == 0)
        {
            sink.Error(path, "json.empty", "string must not be empty");
            return null;
        }

        return text;
    }

    public static double? ReadNumber(JsonElement element, string path, IssueSink sink, double min, double max)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            sink.Error(path, "json.type", $"expected a number but found {Describe(element)}");
            return null;
        }

        if (!element.TryGetDouble(out double value) || !double.IsFinite(value))
        {
            sink.Error(path, "json.range", "number is not a finite double");
            return null;
        }

        if (value < min || value > max)
        {
            sink.Error(path, "json.range", $"value {Format(value)} is outside the allowed range [{Format(min)}, {Format(max)}]");
            return null;
        }

        return value;
    }

    public static int? ReadInteger(JsonElement element, string path, IssueSink sink, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            sink.Error(path, "json.type", $"expected an integer but found {Describe(element)}");
            return null;
        }

        if (!element.TryGetInt32(out int value))
        {
            sink.Error(path, "json.type", "expected an integer but found a non-integer number");
            return null;
        }

        if (value < min || value > max)
        {
            sink.Error(path, "json.range", $"value {value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}]");
            return null;
        }

        return value;
    }

    public static bool? ReadBool(JsonElement element, string path, IssueSink sink)
    {
        if (element.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        sink.Error(path, "json.type", $"expected a boolean but found {Describe(element)}");
        return null;
    }

    /// <summary>Точка <c>[u, v]</c> в пределах <see cref="CoordinateLimits"/>.</summary>
    public static Vec2? ReadPoint(JsonElement element, string path, IssueSink sink)
    {
        if (CheckArray(element, path, sink, 2, 2) is not { } array)
        {
            return null;
        }

        double? u = null;
        double? v = null;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string coordinatePath = Index(path, i);
            if (i == 0)
            {
                u = ReadNumber(item, coordinatePath, sink, CoordinateLimits.MinU, CoordinateLimits.MaxU);
            }
            else
            {
                v = ReadNumber(item, coordinatePath, sink, CoordinateLimits.MinV, CoordinateLimits.MaxV);
            }

            i++;
        }

        return u is { } x && v is { } y ? new Vec2(x, y) : null;
    }

    public static JsonElement? CheckArray(JsonElement element, string path, IssueSink sink, int minItems, int maxItems)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            sink.Error(path, "json.type", $"expected an array but found {Describe(element)}");
            return null;
        }

        int count = element.GetArrayLength();
        if (count < minItems || count > maxItems)
        {
            string expected = minItems == maxItems
                ? minItems.ToString(CultureInfo.InvariantCulture)
                : $"{minItems.ToString(CultureInfo.InvariantCulture)}..{(maxItems == int.MaxValue ? "n" : maxItems.ToString(CultureInfo.InvariantCulture))}";
            sink.Error(path, "json.length", $"expected {expected} element(s) but found {count.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }

        return element;
    }

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Читает массив строк.</summary>
    public static ImmutableArray<string>? ReadStringArray(JsonElement element, string path, IssueSink sink, int minItems, int maxItems)
    {
        if (CheckArray(element, path, sink, minItems, maxItems) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<string>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? text = ReadString(item, Index(path, i), sink);
            if (text is null)
            {
                ok = false;
            }
            else
            {
                result.Add(text);
            }

            i++;
        }

        return ok ? result.ToImmutable() : null;
    }
}
