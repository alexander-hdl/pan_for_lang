using System.Collections.Immutable;
using System.IO;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;
using PenForLang.Glyphs.Loading;
using PenForLang.Tools.InkLab.Protocol;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.Content;

/// <summary>Шаг экранного эталона: сценарий одной буквы, смещённый по линейке.</summary>
/// <param name="Symbol">Буква.</param>
/// <param name="Script">Сценарий анимации.</param>
/// <param name="OffsetU">Смещение начала буквы по <c>u</c> (x-height) от начала эталона.</param>
public sealed record ReferenceStep(string Symbol, AnimationScript Script, double OffsetU);

/// <summary>Экранный эталон подсказки: буквы по порядку.</summary>
/// <param name="Steps">Шаги; следующая буква анимируется после предыдущей.</param>
/// <param name="WidthXHeights">Ширина эталона, x-height (для раскладки).</param>
public sealed record ReferencePlan(ImmutableArray<ReferenceStep> Steps, double WidthXHeights);

/// <summary>
/// Контент InkLab: стиль-пакет <c>zaner-bloser</c> и экранный эталон подсказок протокола. Слово показывается составным из
/// изолированных глифов (после V0.10 его заменит <c>WordTemplateBuilder</c> с настоящими соединениями).
/// </summary>
public sealed class LabContent
{
    /// <summary>Каталог стиль-пакета внутри корня контента.</summary>
    public const string StyleDirectory = "styles/zaner-bloser";

    private readonly Dictionary<string, ReferencePlan> _plans = new(StringComparer.Ordinal);

    private LabContent(StylePack pack)
    {
        Pack = pack;
    }

    /// <summary>Загруженный стиль-пакет.</summary>
    public StylePack Pack { get; }

    /// <summary>Описание стиля.</summary>
    public StyleManifest Style => Pack.Manifest;

    /// <summary>Линии линейки стиля для рисования.</summary>
    public SceneGuides SceneGuides =>
        new(Style.Lines.Ascender, Style.Lines.Cap, Style.Lines.XHeight, Style.Lines.Baseline, Style.Lines.Descender);

    /// <summary>Загружает пакет.</summary>
    /// <exception cref="StyleLoadException">Пакет содержит ошибки.</exception>
    public static LabContent Load(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LabContent(StyleLoader.LoadOrThrow(source, StyleDirectory));
    }

    /// <summary>
    /// Ищет корень контента: заданный явно, затем <c>content</c> рядом с программой, затем вверх по каталогам от программы
    /// (запуск из <c>bin</c> в дереве репозитория). Возвращает корень, где есть пакет стиля, либо <c>null</c>.
    /// </summary>
    public static string? FindContentRoot(string? explicitDirectory, string baseDirectory)
    {
        static bool HasStyle(string root) => File.Exists(Path.Combine(root, "styles", "zaner-bloser", "style.json"));

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return HasStyle(explicitDirectory) ? Path.GetFullPath(explicitDirectory) : null;
        }

        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content");
            if (HasStyle(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Экранный эталон подсказки.</summary>
    /// <exception cref="KeyNotFoundException">В пакете нет глифа для буквы подсказки.</exception>
    public ReferencePlan PlanFor(RecordingPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (_plans.TryGetValue(prompt.Id, out ReferencePlan? cached))
        {
            return cached;
        }

        var steps = ImmutableArray.CreateBuilder<ReferenceStep>();
        double offset = 0;
        for (int i = 0; i < prompt.Text.Length; i++)
        {
            string symbol = prompt.Text[i].ToString();
            Glyph glyph = Pack.GetGlyph(symbol);

            // Входящий и выходящий штрихи — только у первой буквы слова: дальше соединение рисует сам пишущий.
            var options = new AnimationOptions(IncludeLeads: i == 0 || prompt.Text.Length == 1);
            steps.Add(new ReferenceStep(symbol, AnimationScriptBuilder.Build(glyph, Style.Animation, options), offset));
            offset += glyph.Advance + Style.LetterSpacing;
        }

        var plan = new ReferencePlan(steps.ToImmutable(), offset);
        _plans[prompt.Id] = plan;
        return plan;
    }
}
