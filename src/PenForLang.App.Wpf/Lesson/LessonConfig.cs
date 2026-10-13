using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PenForLang.Core;
using PenForLang.Evaluation;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Вид шага фиксированного сценария V0.8.</summary>
public enum StepKind
{
    /// <summary>Показ: анимация с нумерованными стрелками, кнопка «Показать ещё». Письма нет.</summary>
    Demo,

    /// <summary>Обводка (E02): эталон бледно под пером поверх линеек.</summary>
    Trace,

    /// <summary>Копия (E03): эталон над строкой, писать на строке под ним.</summary>
    Copy,

    /// <summary>По памяти (E04): эталон на 2 с, скрыт, пауза, письмо.</summary>
    Memory,
}

/// <summary>Где и как стоит эталон на шаге.</summary>
public enum StepModel
{
    /// <summary>Анимация над строкой (показ).</summary>
    Animated,

    /// <summary>Бледный эталон под пером на строке письма.</summary>
    UnderPen,

    /// <summary>Эталон над строкой письма.</summary>
    Above,

    /// <summary>Вспышка над строкой: виден <c>flashMs</c>, затем скрыт.</summary>
    Flash,
}

/// <summary>Уровень опоры (METHODOLOGY.md §4.1).</summary>
public enum SupportLevel
{
    /// <summary>L4: демонстрация и обводка — анимация, нумерованные стрелки, эталон под пером.</summary>
    L4,

    /// <summary>L3: копия — эталон над строкой, стрелки и точка старта на эталоне.</summary>
    L3,

    /// <summary>L2: на строке видна только точка старта.</summary>
    L2,

    /// <summary>L1: эталон показан и скрыт, пишем по памяти.</summary>
    L1,

    /// <summary>L0: звук, эталона нет.</summary>
    L0,
}

/// <summary>Шаг сценария урока (METHODOLOGY.md §4.1, §4.2, §6.2; код упражнения — по каталогу).</summary>
/// <param name="Code">Код упражнения: <c>E01</c> показ, <c>E02</c> обводка, <c>E03</c> копия, <c>E04</c> по памяти.</param>
/// <param name="Kind">Вид шага.</param>
/// <param name="Attempts">Наибольшее число попыток письма; у показа — 0.</param>
/// <param name="MinAttempts">Наименьшее число попыток: вторая попытка обводки нужна, только если в первой была процессная ошибка.</param>
/// <param name="Level">Уровень опоры.</param>
/// <param name="LiveCue">Разрешён ли мгновенный мягкий сигнал неверного старта (L4–L2; в копии L3 выключен по решению ревью, см. <c>meta</c>).</param>
/// <param name="StartDot">Видна ли точка старта (на эталоне или, у письма по памяти на L2, на строке письма).</param>
/// <param name="Model">Где стоит эталон.</param>
/// <param name="FlashMs">Сколько виден эталон по памяти, мс.</param>
/// <param name="DelayMs">Пауза после скрытия эталона до «пиши», мс (перо не принимается).</param>
/// <param name="HideArrowsAfterCorrect">Стрелки на эталоне скрываются после стольких верных попыток шага; 0 — не скрываются.</param>
/// <param name="AutoPlays">Сколько раз анимация показа проигрывается сама.</param>
public sealed record LessonStep(
    string Code,
    StepKind Kind,
    int Attempts,
    int MinAttempts,
    SupportLevel Level,
    bool LiveCue,
    bool StartDot,
    StepModel Model,
    int FlashMs,
    int DelayMs,
    int HideArrowsAfterCorrect,
    int AutoPlays);

/// <summary>Числа защиты от перегруза, затухания частоты и ограничений текста (METHODOLOGY.md §7).</summary>
/// <param name="RepeatLimit">Сколько раз подряд показывается одна подсказка: на последнем повторе текст заменяется сменой способа.</param>
/// <param name="MaxWords">Наибольшее число слов в подсказке ребёнку.</param>
/// <param name="UnrecognizedRetries">Сколько повторных попыток даёт «не распознано» (U0) до перехода дальше без оценки.</param>
/// <param name="RestartsBeforeReplay">После скольки «Заново» подряд в одной попытке повторяется анимация.</param>
/// <param name="RaisedAidsAttempts">На сколько попыток добавляется опора после смены способа.</param>
/// <param name="StopCodes">Коды, которые показываются на любой ступени частоты (стоп-ошибки).</param>
/// <param name="ProcessCodes">Коды процессных ошибок (для возврата частоты на ступень выше).</param>
/// <param name="MasteredEveryN">Для «освоена»: сводная обратная связь после каждой N-й попытки.</param>
/// <param name="StableFormFloor">Для «устойчива»: обратная связь, только если <c>form</c> ниже этого порога.</param>
/// <param name="ProcessErrorsToRaise">Сколько процессных ошибок подряд возвращают частоту на ступень выше.</param>
/// <param name="AutoAdvanceMs">Через сколько мс попытка без сообщения переходит дальше сама.</param>
/// <param name="EvaluationTimeoutMs">Сколько мс ждать оценку, затем «спасибо, дальше» без оценки.</param>
/// <param name="HighlightPulses">Сколько периодов пульсирует точка старта в подсветке, затем остаётся статичной.</param>
public sealed record FeedbackSettings(
    int RepeatLimit,
    int MaxWords,
    int UnrecognizedRetries,
    int RestartsBeforeReplay,
    int RaisedAidsAttempts,
    ImmutableHashSet<ErrorCode> StopCodes,
    ImmutableHashSet<ErrorCode> ProcessCodes,
    int MasteredEveryN,
    double StableFormFloor,
    int ProcessErrorsToRaise,
    int AutoAdvanceMs,
    int EvaluationTimeoutMs,
    int HighlightPulses);

/// <summary>Числа мягкого сигнала по ходу письма.</summary>
/// <param name="Enabled">Сигнал включён.</param>
/// <param name="StartClearance">Насколько (в x-height) первая точка штриха должна быть дальше от допустимых мест старта, чтобы подать сигнал.</param>
/// <param name="DurationMs">Сколько держится сигнал (мигание точки старта), мс.</param>
/// <param name="MaxPerAttempt">Не больше сигналов за попытку.</param>
/// <param name="PulsePeriodMs">Период мигания точки старта, мс.</param>
public sealed record LiveCueSettings(bool Enabled, double StartClearance, int DurationMs, int MaxPerAttempt, int PulsePeriodMs);

/// <summary>
/// Числа сценария урока V0.8 (<c>content/config/lesson.json</c>, схема <c>pfl.config.lesson/1</c>): шаги, защита от перегруза и мягкий сигнал.
/// Числа методики лежат в данных, а не в коде (CLAUDE.md).
/// </summary>
/// <param name="Steps">Шаги по порядку.</param>
/// <param name="Feedback">Настройки обратной связи.</param>
/// <param name="LiveCue">Настройки мягкого сигнала.</param>
public sealed record LessonConfig(ImmutableArray<LessonStep> Steps, FeedbackSettings Feedback, LiveCueSettings LiveCue)
{
    /// <summary>Путь файла в корне контента.</summary>
    public const string DefaultPath = "config/lesson.json";

    /// <summary>Загружает конфигурацию из источника контента.</summary>
    /// <exception cref="InvalidDataException">Файл отсутствует или не соответствует схеме.</exception>
    public static LessonConfig Load(IContentSource source, string path = DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Exists(path))
        {
            throw new InvalidDataException($"Lesson config '{path}' not found.");
        }

        using Stream stream = source.Open(path);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Разбирает текст конфигурации.</summary>
    /// <exception cref="InvalidDataException">Текст не соответствует схеме.</exception>
    public static LessonConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!string.Equals(Str(root, "schema"), "pfl.config.lesson/1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Lesson config schema must be 'pfl.config.lesson/1'.");
            }

            ImmutableArray<LessonStep> steps = ParseSteps(Require(root, "scenario"));
            JsonElement fb = Require(root, "feedback");
            JsonElement frequency = Require(fb, "frequency");
            var feedback = new FeedbackSettings(
                Int(fb, "repeatLimit", min: 2),
                Int(fb, "maxWords", min: 1),
                Int(fb, "unrecognizedRetries", min: 0),
                Int(fb, "restartsBeforeReplay", min: 1),
                Int(fb, "raisedAidsAttempts", min: 1),
                Codes(Require(fb, "stopCodes")),
                Codes(Require(fb, "processCodes")),
                Int(frequency, "masteredEveryN", min: 1),
                Num(frequency, "stableFormFloor"),
                Int(frequency, "processErrorsToRaise", min: 1),
                Int(fb, "autoAdvanceMs", min: 1),
                Int(fb, "evaluationTimeoutMs", min: 1),
                Int(fb, "highlightPulses", min: 1));
            JsonElement cue = Require(root, "liveCue");
            var liveCue = new LiveCueSettings(
                Bool(cue, "enabled"),
                Num(cue, "startClearance"),
                Int(cue, "durationMs", min: 1),
                Int(cue, "maxPerAttempt", min: 0),
                Int(cue, "pulsePeriodMs", min: 1));
            return new LessonConfig(steps, feedback, liveCue);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Lesson config is not valid JSON: " + ex.Message, ex);
        }
    }

    private static ImmutableArray<LessonStep> ParseSteps(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            throw new InvalidDataException("'scenario' must be a non-empty array.");
        }

        var steps = ImmutableArray.CreateBuilder<LessonStep>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            string kindText = Str(item, "kind");
            StepKind kind = kindText switch
            {
                "demo" => StepKind.Demo,
                "trace" => StepKind.Trace,
                "copy" => StepKind.Copy,
                "memory" => StepKind.Memory,
                _ => throw new InvalidDataException($"Unknown step kind '{kindText}'."),
            };
            int attempts = Int(item, "attempts", min: 0);
            if ((kind == StepKind.Demo) != (attempts == 0))
            {
                throw new InvalidDataException("A demo step has no attempts; writing steps need at least one.");
            }

            string levelText = Str(item, "level");
            if (!Enum.TryParse(levelText, ignoreCase: false, out SupportLevel level) || !Enum.IsDefined(level))
            {
                throw new InvalidDataException($"Unknown support level '{levelText}'.");
            }

            int minAttempts = item.TryGetProperty("minAttempts", out _) ? Int(item, "minAttempts", min: 0) : attempts;
            if (minAttempts > attempts || (kind != StepKind.Demo && minAttempts < 1))
            {
                throw new InvalidDataException("'minAttempts' must be between 1 and 'attempts'.");
            }

            string modelText = Str(item, "model");
            StepModel model = modelText switch
            {
                "animated" => StepModel.Animated,
                "underPen" => StepModel.UnderPen,
                "above" => StepModel.Above,
                "flash" => StepModel.Flash,
                _ => throw new InvalidDataException($"Unknown model '{modelText}'."),
            };
            int flashMs = item.TryGetProperty("flashMs", out _) ? Int(item, "flashMs", min: 1) : 0;
            int delayMs = item.TryGetProperty("delayMs", out _) ? Int(item, "delayMs", min: 0) : 0;
            if (kind == StepKind.Memory && (flashMs == 0 || model != StepModel.Flash))
            {
                throw new InvalidDataException("A memory step needs 'flashMs' and the 'flash' model.");
            }

            int hide = item.TryGetProperty("hideArrowsAfterCorrect", out _) ? Int(item, "hideArrowsAfterCorrect", min: 0) : 0;
            int autoPlays = item.TryGetProperty("autoPlays", out _) ? Int(item, "autoPlays", min: 1) : 1;
            bool liveCue = item.TryGetProperty("liveCue", out JsonElement cue) && cue.ValueKind == JsonValueKind.True;
            bool startDot = item.TryGetProperty("startDot", out JsonElement dot) && dot.ValueKind == JsonValueKind.True;
            steps.Add(new LessonStep(Str(item, "code"), kind, attempts, minAttempts, level, liveCue, startDot, model, flashMs, delayMs, hide, autoPlays));
        }

        return steps.ToImmutable();
    }

    private static ImmutableHashSet<ErrorCode> Codes(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Code lists must be arrays.");
        }

        ImmutableHashSet<ErrorCode>.Builder set = ImmutableHashSet.CreateBuilder<ErrorCode>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            string text = item.GetString() ?? string.Empty;
            if (!Enum.TryParse(text, ignoreCase: false, out ErrorCode code) || !Enum.IsDefined(code))
            {
                throw new InvalidDataException($"Unknown error code '{text}'.");
            }

            set.Add(code);
        }

        return set.ToImmutable();
    }

    private static JsonElement Require(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException($"Missing '{name}'.");

    private static string Str(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"'{name}' must be a string.");
    }

    private static int Int(JsonElement parent, string name, int min)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < min)
        {
            throw new InvalidDataException($"'{name}' must be an integer of at least {min.ToString(CultureInfo.InvariantCulture)}.");
        }

        return number;
    }

    private static double Num(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number) || number < 0)
        {
            throw new InvalidDataException($"'{name}' must be a non-negative number.");
        }

        return number;
    }

    private static bool Bool(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"'{name}' must be true or false.");
    }
}
