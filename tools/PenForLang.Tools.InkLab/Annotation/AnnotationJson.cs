using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PenForLang.Tools.InkLab.Corpus;

namespace PenForLang.Tools.InkLab.Annotation;

/// <summary>Файл разметки не читается: не JSON, неподдерживаемая версия, неизвестные поля или нет обязательных полей.</summary>
public sealed class AnnotationFormatException : FormatException
{
    /// <summary>Создаёт исключение.</summary>
    public AnnotationFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Чтение и запись файла разметки (JSON, UTF-8 без BOM, отступы, camelCase, перечисления строками). Чтение выбирает разбор по
/// полю <c>schema</c>: поддерживается <c>pfl.annotation/1</c>; другая версия — понятная ошибка, а не молчаливая потеря полей.
/// Неизвестные поля внутри версии 1 отвергаются (формат закрытый: новые поля — новая версия).
/// </summary>
public static class AnnotationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Сериализует разметку.</summary>
    public static string Serialize(AnnotationFile annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return JsonSerializer.Serialize(annotation, Options) + "\n";
    }

    /// <summary>Читает разметку.</summary>
    /// <exception cref="AnnotationFormatException">Не JSON, чужая версия, неизвестные поля, пропущены обязательные поля.</exception>
    public static AnnotationFile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        string? schema;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AnnotationFormatException("Файл разметки должен быть JSON-объектом.");
            }

            schema = document.RootElement.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new AnnotationFormatException("Файл разметки повреждён: " + ex.Message, ex);
        }

        return schema switch
        {
            AnnotationFormat.Schema => ReadV1(json),
            null => throw new AnnotationFormatException("В файле разметки нет поля schema."),
            _ => throw new AnnotationFormatException($"Неподдерживаемая версия '{schema}': эта программа читает только '{AnnotationFormat.Schema}'."),
        };
    }

    /// <summary>Читает файл разметки.</summary>
    public static AnnotationFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Пишет файл разметки атомарно (с принудительным сбросом на диск).</summary>
    public static void Save(string path, AnnotationFile annotation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(annotation);
        AtomicFile.WriteText(path, Serialize(annotation));
    }

    private static AnnotationFile ReadV1(string json)
    {
        AnnotationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AnnotationFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AnnotationFormatException("Файл разметки не соответствует формату: " + ex.Message, ex);
        }

        if (file is null || file.Text is null || file.Quality is null || file.Strokes.IsDefault || file.Junctions.IsDefault
            || file.Deferred.IsDefault || file.PenlogSha256 is null || file.Indexing is null || file.Annotator is null || file.Tool is null)
        {
            throw new AnnotationFormatException(
                "В файле разметки не хватает обязательных полей (penlogSha256, indexing, annotator, tool, text, quality, strokes, junctions, deferred).");
        }

        foreach (Junction j in file.Junctions)
        {
            if (j.End is null || j.Start is null)
            {
                throw new AnnotationFormatException("У каждого стыка должны быть точки end и start.");
            }
        }

        return file with
        {
            Penlog = file.Penlog ?? string.Empty,
            Quality = file.Quality with { Flags = file.Quality.Flags.IsDefault ? [] : file.Quality.Flags },
        };
    }
}
