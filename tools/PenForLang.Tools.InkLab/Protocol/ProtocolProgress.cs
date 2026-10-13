using System.Text.Json;
using System.Text.Json.Serialization;

namespace PenForLang.Tools.InkLab.Protocol;

/// <summary>Состояние подсказки протокола.</summary>
public enum PromptStatus
{
    /// <summary>Ещё не записана.</summary>
    Pending,

    /// <summary>Записана (есть penlog).</summary>
    Done,

    /// <summary>Пропущена по решению записывающего.</summary>
    Skipped,
}

/// <summary>Субъект записи, как он хранится в файле прогресса. Имени нет: только анонимный идентификатор.</summary>
/// <param name="Id">Идентификатор субъекта-взрослого (<c>subject.anon</c> в penlog).</param>
/// <param name="Handedness">Ведущая рука: <c>right</c> или <c>left</c>.</param>
/// <param name="AgeYears">Возраст, лет (необязательно).</param>
/// <param name="ConsentId">Номер согласия (необязательно; для взрослого согласие не требуется, поле есть для учёта).</param>
public sealed record SubjectInfo(string Id, string Handedness = SubjectInfo.Right, int? AgeYears = null, int? ConsentId = null)
{
    /// <summary>Правая рука.</summary>
    public const string Right = "right";

    /// <summary>Левая рука.</summary>
    public const string Left = "left";

    /// <summary>Вид субъекта: в InkLab пишут только взрослые (<c>subject.kind = adult</c>).</summary>
    public const string Kind = "adult";

    /// <summary>Идентификатор, пригодный для имени папки: буквы, цифры, «-», «_»; остальное заменено на «_».</summary>
    public string FolderName => SanitizeForFolder(Id);

    /// <summary>Допустим ли идентификатор: непустой, без управляющих символов, не длиннее 40.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().Length <= 40 && !id.Any(char.IsControl);

    /// <summary>Заменяет недопустимые для имени папки символы.</summary>
    public static string SanitizeForFolder(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        char[] chars = [.. id.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')];
        return chars.Length == 0 ? "_" : new string(chars);
    }
}

/// <summary>Запись о подсказке в файле прогресса.</summary>
public sealed class PromptProgress
{
    /// <summary>Состояние.</summary>
    public PromptStatus Status { get; set; }

    /// <summary>Файл penlog относительно папки субъекта (для <see cref="PromptStatus.Done"/>).</summary>
    public string? File { get; set; }

    /// <summary>Сколько раз нажимали «Заново» в принятой попытке.</summary>
    public int Restarts { get; set; }

    /// <summary>Сколько penlog записано для подсказки всего (с перезаписями).</summary>
    public int Takes { get; set; }
}

/// <summary>
/// Прогресс прохождения протокола одним субъектом; хранится в <c>progress.json</c> папки субъекта, чтобы запись можно было
/// продолжить позже. Формат <c>pfl.inklab.progress/1</c>.
/// </summary>
public sealed class ProtocolProgress
{
    /// <summary>Значение поля <c>schema</c>.</summary>
    public const string SchemaId = "pfl.inklab.progress/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Схема файла.</summary>
    public string Schema { get; set; } = SchemaId;

    /// <summary>Идентификатор протокола (<see cref="RecordingProtocol.Id"/>).</summary>
    public string ProtocolId { get; set; } = string.Empty;

    /// <summary>Субъект записи.</summary>
    public SubjectInfo Subject { get; set; } = new("?");

    /// <summary>Идентификатор текущей подсказки (где остановились).</summary>
    public string? Current { get; set; }

    /// <summary>Записи по идентификаторам подсказок; отсутствие записи = <see cref="PromptStatus.Pending"/>.</summary>
    public Dictionary<string, PromptProgress> Prompts { get; set; } = [];

    /// <summary>Состояние подсказки (по умолчанию <see cref="PromptStatus.Pending"/>).</summary>
    public PromptStatus StatusOf(string promptId) =>
        Prompts.TryGetValue(promptId, out PromptProgress? p) ? p.Status : PromptStatus.Pending;

    /// <summary>Запись о подсказке; создаётся при первом обращении.</summary>
    public PromptProgress Entry(string promptId)
    {
        if (!Prompts.TryGetValue(promptId, out PromptProgress? entry))
        {
            entry = new PromptProgress();
            Prompts[promptId] = entry;
        }

        return entry;
    }

    /// <summary>Сериализует в JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Читает JSON.</summary>
    /// <exception cref="FormatException">Не тот формат.</exception>
    public static ProtocolProgress FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            ProtocolProgress? result = JsonSerializer.Deserialize<ProtocolProgress>(json, Options);
            if (result is null || !string.Equals(result.Schema, SchemaId, StringComparison.Ordinal))
            {
                throw new FormatException($"Progress file must have schema '{SchemaId}'.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new FormatException("Progress file is not valid JSON: " + ex.Message, ex);
        }
    }
}
