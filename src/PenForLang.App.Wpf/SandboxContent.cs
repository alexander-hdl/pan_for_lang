using System.IO;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;
using PenForLang.Glyphs.Loading;

namespace PenForLang.App.Wpf;

/// <summary>
/// Контент «урока-песочницы» V0.6: стиль-пакет <c>content/styles/zaner-bloser</c>, загруженный через <see cref="IContentSource"/>,
/// и сценарии анимации букв a, i, t.
/// </summary>
public sealed class SandboxContent
{
    /// <summary>Каталог стиль-пакета внутри корня контента.</summary>
    public const string StyleDirectory = "styles/zaner-bloser";

    /// <summary>Буквы песочницы (V0: a, i, t).</summary>
    public static readonly IReadOnlyList<string> Letters = ["a", "i", "t"];

    private readonly Dictionary<string, AnimationScript> _scripts = new(StringComparer.Ordinal);

    private SandboxContent(StylePack pack)
    {
        Pack = pack;
        foreach (string letter in Letters)
        {
            _scripts[letter] = AnimationScriptBuilder.Build(pack.GetGlyph(letter), pack.Manifest.Animation);
        }
    }

    /// <summary>Загруженный стиль-пакет.</summary>
    public StylePack Pack { get; }

    /// <summary>Описание стиля (линейки, наклон, анимация).</summary>
    public StyleManifest Style => Pack.Manifest;

    /// <summary>Сценарий анимации буквы.</summary>
    /// <exception cref="KeyNotFoundException">Такой буквы в песочнице нет.</exception>
    public AnimationScript GetScript(string letter) =>
        _scripts.TryGetValue(letter, out AnimationScript? script) ? script : throw new KeyNotFoundException($"The sandbox has no letter '{letter}'.");

    /// <summary>Загружает пакет из источника контента.</summary>
    /// <exception cref="StyleLoadException">Пакет содержит ошибки.</exception>
    public static SandboxContent Load(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SandboxContent(StyleLoader.LoadOrThrow(source, StyleDirectory));
    }

    /// <summary>
    /// Ищет корень контента: заданный явно, затем <c>content</c> рядом с программой (туда его копирует сборка и публикация), затем
    /// вверх по каталогам от программы (запуск из <c>bin</c> в дереве репозитория). Возвращает корень, в котором есть пакет стиля, либо <c>null</c>.
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
}
