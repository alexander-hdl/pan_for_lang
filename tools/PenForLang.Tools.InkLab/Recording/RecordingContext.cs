using System.Globalization;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Protocol;

namespace PenForLang.Tools.InkLab.Recording;

/// <summary>Сведения об окружении записи, нужные для заголовка penlog: устройство, экран, линейка письма.</summary>
/// <param name="Device">Устройство ввода.</param>
/// <param name="Screen">Монитор пера и его масштаб.</param>
/// <param name="Guides">Линейка письма на холсте (px поверхности).</param>
/// <param name="Os">Версия ОС.</param>
/// <param name="App">Версия приложения.</param>
/// <param name="QpcFreq">Частота QPC, Гц.</param>
public sealed record RecordingContext(PenDeviceInfo Device, PenScreenInfo Screen, GuideFrame Guides, string Os, string App, long QpcFreq);

/// <summary>Сборка заголовка penlog для записи InkLab.</summary>
public static class RecordingHeaders
{
    /// <summary>Упражнение в <c>task.exercise</c> для всех записей InkLab.</summary>
    public const string Exercise = "INKLAB";

    /// <summary>Режим в <c>task.mode</c>: копирование экранного эталона.</summary>
    public const string Mode = "copy";

    /// <summary>Вид согласия в <c>consent.kind</c>.</summary>
    public const string ConsentKind = "trajectory_recording";

    /// <summary>Версия текста согласия.</summary>
    public const string ConsentVersion = "1.0";

    /// <summary>
    /// Заголовок penlog: <c>task</c> — что просили написать (<c>taskId = &lt;протокол&gt;/&lt;подсказка&gt;</c>, цель, режим copy),
    /// <c>subject</c> — взрослый без имени, <c>consent</c> — только если номер указан.
    /// </summary>
    public static PenLogHeader Build(RecordingContext context, SubjectInfo subject, string protocolId, RecordingPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(prompt);
        var task = new PenLogTask(TaskId(protocolId, prompt.Id), Exercise, prompt.Target, "L0", Mode, subject.Handedness);
        var who = new PenLogSubject(subject.Id.Trim(), subject.AgeYears, PenSubjectKind.Adult);
        PenLogConsent? consent = subject.ConsentId is { } id ? new PenLogConsent(id, ConsentKind, ConsentVersion) : null;
        return new PenLogHeader(context.App, context.Os, context.Device, context.Screen, context.Guides, task, who, consent, context.QpcFreq);
    }

    /// <summary>Идентификатор задания: <c>inklab-v0.9/at-lift-003</c>.</summary>
    public static string TaskId(string protocolId, string promptId) => protocolId + "/" + promptId;

    /// <summary>Разбирает идентификатор задания: протокол и подсказка; вариант — из подсказки вида <c>at-lift-003</c>.</summary>
    public static bool TryParseTaskId(string? taskId, out string protocolId, out string promptId, out string? variant)
    {
        protocolId = string.Empty;
        promptId = string.Empty;
        variant = null;
        if (string.IsNullOrEmpty(taskId))
        {
            return false;
        }

        int slash = taskId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == taskId.Length - 1)
        {
            return false;
        }

        protocolId = taskId[..slash];
        promptId = taskId[(slash + 1)..];
        string[] parts = promptId.Split('-');
        if (parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            variant = parts[1];
        }

        return true;
    }
}
