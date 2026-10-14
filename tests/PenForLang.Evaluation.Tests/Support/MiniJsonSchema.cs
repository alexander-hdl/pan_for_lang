using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PenForLang.Evaluation.Tests.Support;

/// <summary>
/// Минимальный проверяющий JSON Schema (подмножество draft 2020-12, которое используют схемы проекта):
/// <c>type</c>, <c>enum</c>, <c>const</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>, <c>propertyNames.pattern</c>,
/// <c>items</c>, <c>prefixItems</c>, <c>minItems</c>/<c>maxItems</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>,
/// <c>minimum</c>/<c>maximum</c>/<c>exclusiveMinimum</c>, <c>oneOf</c>, локальные <c>$ref</c> на <c>#/$defs/...</c>.
/// Нужен, чтобы схемы в <c>schemas/</c> проверялись на реальных файлах без внешнего пакета.
/// Неизвестное ключевое слово — ошибка теста (нельзя молча «проверять» меньше, чем написано в схеме).
/// </summary>
internal sealed class MiniJsonSchema
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "$ref", "title", "description", "type", "enum", "const", "required", "properties",
        "additionalProperties", "propertyNames", "items", "prefixItems", "minItems", "maxItems", "minLength", "maxLength",
        "pattern", "minimum", "maximum", "exclusiveMinimum", "oneOf",
    };

    private readonly JsonElement _root;

    public MiniJsonSchema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        _root = document.RootElement.Clone();
    }

    public static MiniJsonSchema FromRepo(string fileName) => new(File.ReadAllText(Repo.Path("schemas", fileName)));

    public List<string> Validate(string instanceJson)
    {
        using JsonDocument instance = JsonDocument.Parse(instanceJson);
        var errors = new List<string>();
        Validate(instance.RootElement, _root, string.Empty, errors);
        return errors;
    }

    private void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            if (!Known.Contains(keyword.Name))
            {
                throw new NotSupportedException($"Schema keyword '{keyword.Name}' is not supported by MiniJsonSchema.");
            }
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            Validate(value, Resolve(reference.GetString()!), path, errors);
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !TypeMatches(value, type))
        {
            errors.Add($"{path}: expected type {type}, found {value.ValueKind}");
            return;
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonEquals(value, constant))
        {
            errors.Add($"{path}: expected the constant {constant}");
        }

        if (schema.TryGetProperty("enum", out JsonElement enumeration) && !enumeration.EnumerateArray().Any(e => JsonEquals(value, e)))
        {
            errors.Add($"{path}: {value} is not one of {enumeration}");
        }

        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int matches = oneOf.EnumerateArray().Count(branch =>
            {
                var branchErrors = new List<string>();
                Validate(value, branch, path, branchErrors);
                return branchErrors.Count == 0;
            });
            if (matches != 1)
            {
                errors.Add($"{path}: must match exactly one of the oneOf branches, matches {matches}");
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(value, schema, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(value, schema, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(value, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(value, schema, path, errors);
                break;
            default:
                break;
        }
    }

    private void ValidateObject(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (!value.TryGetProperty(name.GetString()!, out _))
                {
                    errors.Add($"{path}: missing required property '{name.GetString()}'");
                }
            }
        }

        JsonElement properties = schema.TryGetProperty("properties", out JsonElement p) ? p : default;
        bool hasAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string childPath = $"{path}.{property.Name}";
            if (schema.TryGetProperty("propertyNames", out JsonElement names)
                && names.TryGetProperty("pattern", out JsonElement namePattern)
                && !Regex.IsMatch(property.Name, namePattern.GetString()!))
            {
                errors.Add($"{childPath}: property name does not match {namePattern}");
            }

            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out JsonElement childSchema))
            {
                Validate(property.Value, childSchema, childPath, errors);
            }
            else if (hasAdditional)
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    errors.Add($"{childPath}: unknown property");
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Validate(property.Value, additional, childPath, errors);
                }
            }
        }
    }

    private void ValidateArray(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        int count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out JsonElement min) && count < min.GetInt32())
        {
            errors.Add($"{path}: expected at least {min} item(s), found {count}");
        }

        if (schema.TryGetProperty("maxItems", out JsonElement max) && count > max.GetInt32())
        {
            errors.Add($"{path}: expected at most {max} item(s), found {count}");
        }

        int index = 0;
        JsonElement prefix = schema.TryGetProperty("prefixItems", out JsonElement pi) ? pi : default;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string childPath = $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (prefix.ValueKind == JsonValueKind.Array && index < prefix.GetArrayLength())
            {
                Validate(item, prefix[index], childPath, errors);
            }
            else if (schema.TryGetProperty("items", out JsonElement items))
            {
                Validate(item, items, childPath, errors);
            }

            index++;
        }
    }

    private static void ValidateString(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        string text = value.GetString()!;
        if (schema.TryGetProperty("minLength", out JsonElement min) && text.Length < min.GetInt32())
        {
            errors.Add($"{path}: string is shorter than {min}");
        }

        if (schema.TryGetProperty("maxLength", out JsonElement max) && text.Length > max.GetInt32())
        {
            errors.Add($"{path}: string is longer than {max}");
        }

        if (schema.TryGetProperty("pattern", out JsonElement pattern) && !Regex.IsMatch(text, pattern.GetString()!))
        {
            errors.Add($"{path}: '{text}' does not match {pattern}");
        }
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        double number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out JsonElement min) && number < min.GetDouble())
        {
            errors.Add($"{path}: {number} is below the minimum {min}");
        }

        if (schema.TryGetProperty("maximum", out JsonElement max) && number > max.GetDouble())
        {
            errors.Add($"{path}: {number} is above the maximum {max}");
        }

        if (schema.TryGetProperty("exclusiveMinimum", out JsonElement exclusive) && number <= exclusive.GetDouble())
        {
            errors.Add($"{path}: {number} must be greater than {exclusive}");
        }
    }

    private JsonElement Resolve(string reference)
    {
        const string Prefix = "#/$defs/";
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Only local '#/$defs/...' references are supported, found '{reference}'.");
        }

        return _root.GetProperty("$defs").GetProperty(reference[Prefix.Length..]);
    }

    private static bool TypeMatches(JsonElement value, JsonElement type)
    {
        IEnumerable<string> names = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()!)
            : [type.GetString()!];
        return names.Any(name => name switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => throw new NotSupportedException($"Unknown type '{name}'."),
        });
    }

    private static bool JsonEquals(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && a.ValueKind switch
        {
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.GetRawText() == b.GetRawText(),
        };
}
