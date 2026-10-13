using PenForLang.Glyphs.Loading;

namespace PenForLang.Glyphs.Tests.Support;

/// <summary>Стиль-пакет <c>content/styles/zaner-bloser</c>, загруженный из репозитория один раз.</summary>
internal static class Shipped
{
    private static readonly Lazy<StyleLoadResult> Result = new(() => StyleLoader.Load(Repo.LoadContent(), Repo.StyleDirectory));

    public static StyleLoadResult LoadResult => Result.Value;

    public static StylePack Pack => LoadResult.Pack
        ?? throw new InvalidOperationException("The shipped style failed to load:\n" + string.Join("\n", LoadResult.Issues));

    public static StyleManifest Style => Pack.Manifest;

    public static Glyph Glyph(string symbol) => Pack.GetGlyph(symbol);
}
