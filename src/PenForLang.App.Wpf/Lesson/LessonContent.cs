using System.IO;
using PenForLang.Core;
using PenForLang.Evaluation;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>
/// Всё, что нужно уроку V0.8 из контента: стиль-пакет и анимации a/i/t (<see cref="SandboxContent"/>), оценщик буквы, числа сценария и
/// строки RU/EN. Загружается один раз при запуске через <see cref="IContentSource"/>.
/// </summary>
public sealed class LessonContent
{
    /// <summary>Языки интерфейса: первый — по умолчанию.</summary>
    public static readonly IReadOnlyList<string> Languages = ["ru", "en"];

    private readonly SandboxContent _sandbox;
    private readonly Dictionary<string, LocaleCatalog> _locales;

    private LessonContent(SandboxContent sandbox, LetterEvaluator evaluator, LessonConfig config, Dictionary<string, LocaleCatalog> locales)
    {
        _sandbox = sandbox;
        Evaluator = evaluator;
        Config = config;
        _locales = locales;
    }

    /// <summary>Описание стиля письма.</summary>
    public StyleManifest Style => _sandbox.Style;

    /// <summary>Оценщик изолированной буквы.</summary>
    public ILetterEvaluator Evaluator { get; }

    /// <summary>Числа сценария и обратной связи.</summary>
    public LessonConfig Config { get; }

    /// <summary>Эталон буквы.</summary>
    /// <exception cref="KeyNotFoundException">Такой буквы в уроке нет.</exception>
    public Glyph GetGlyph(string letter) => _sandbox.Pack.GetGlyph(letter);

    /// <summary>Сценарий анимации буквы.</summary>
    public AnimationScript GetScript(string letter) => _sandbox.GetScript(letter);

    /// <summary>Каталог строк языка.</summary>
    /// <exception cref="KeyNotFoundException">Языка нет.</exception>
    public LocaleCatalog Locale(string language) =>
        _locales.TryGetValue(language, out LocaleCatalog? catalog) ? catalog : throw new KeyNotFoundException($"No locale '{language}'.");

    /// <summary>Загружает контент урока.</summary>
    /// <exception cref="InvalidDataException">Конфигурация или строки не соответствуют схеме.</exception>
    /// <exception cref="Glyphs.Loading.StyleLoadException">Стиль-пакет содержит ошибки.</exception>
    public static LessonContent Load(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SandboxContent sandbox = SandboxContent.Load(source);
        var evaluator = new LetterEvaluator(ScoringConfigLoader.Load(source));
        LessonConfig config = LessonConfig.Load(source);
        var locales = new Dictionary<string, LocaleCatalog>(StringComparer.Ordinal);
        foreach (string language in Languages)
        {
            locales[language] = LocaleCatalog.Load(source, language);
        }

        return new LessonContent(sandbox, evaluator, config, locales);
    }
}
