using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenForLang.Tools.InkLab.Annotation;

/// <summary>Файл разметки не читается: не JSON, чужая схема или нет обязательных полей.</summary>
public sealed class AnnotationFormatException : FormatException
{
    /// <summary>Создаёт исключение.</summary>
    public AnnotationFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Чтение и запись файла разметки <c>pfl.annotation/1</c> (JSON, UTF-8 без BOM, отступы, camelCase, перечисления строками).</summary>
public static class AnnotationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Сериализует разметку.</summary>
    public static string Serialize(AnnotationFile annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return JsonSerializer.Serialize(annotation, Options) + "\n";
    }

    /// <summary>Читает разметку.</summary>
    /// <exception cref="AnnotationFormatException">Не JSON, чужая схема, пропущены обязательные поля.</exception>
    public static AnnotationFile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AnnotationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AnnotationFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AnnotationFormatException("Файл разметки повреждён: " + ex.Message, ex);
        }

        if (file is null)
        {
            throw new AnnotationFormatException("Файл разметки пуст.");
        }

        if (!string.Equals(file.Schema, AnnotationFormat.Schema, StringComparison.Ordinal))
        {
            throw new AnnotationFormatException($"Ожидается схема '{AnnotationFormat.Schema}', в файле '{file.Schema}'.");
        }

        if (file.Text is null || file.Quality is null || file.Junctions.IsDefault || file.Deferred.IsDefault || file.StrokeSamples.IsDefault)
        {
            throw new AnnotationFormatException("В файле разметки не хватает обязательных полей (text, quality, strokeSamples, junctions, deferred).");
        }

        return file with
        {
            Penlog = file.Penlog ?? string.Empty,
            Annotator = file.Annotator ?? string.Empty,
            Quality = file.Quality with { Flags = file.Quality.Flags.IsDefault ? [] : file.Quality.Flags },
        };
    }

    /// <summary>Читает файл разметки.</summary>
    public static AnnotationFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Пишет файл разметки атомарно.</summary>
    public static void Save(string path, AnnotationFile annotation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(annotation);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(annotation), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
