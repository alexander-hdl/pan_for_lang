namespace PenForLang.Glyphs;

/// <summary>Имена значений перечислений в JSON (формат <c>pfl.glyph/1</c>, <c>pfl.style/1</c>) и обратно. Только для сборки.</summary>
internal static class WireNames
{
    public static readonly IReadOnlyDictionary<string, GlyphCase> Cases =
        new Dictionary<string, GlyphCase>(StringComparer.Ordinal) { ["lower"] = GlyphCase.Lower, ["upper"] = GlyphCase.Upper };

    public static readonly IReadOnlyDictionary<string, DeferredKind> DeferredKinds =
        new Dictionary<string, DeferredKind>(StringComparer.Ordinal)
        {
            ["dot"] = DeferredKind.Dot,
            ["bar"] = DeferredKind.Bar,
            ["cross"] = DeferredKind.Cross,
        };

    public static readonly IReadOnlyDictionary<string, DeferredDirection> DeferredDirections =
        new Dictionary<string, DeferredDirection>(StringComparer.Ordinal)
        {
            ["any"] = DeferredDirection.Any,
            ["ltr"] = DeferredDirection.LeftToRight,
        };

    public static readonly IReadOnlyDictionary<string, KeyPointType> KeyPointTypes =
        new Dictionary<string, KeyPointType>(StringComparer.Ordinal)
        {
            ["start"] = KeyPointType.Start,
            ["end"] = KeyPointType.End,
            ["ymax"] = KeyPointType.YMax,
            ["ymin"] = KeyPointType.YMin,
            ["cusp"] = KeyPointType.Cusp,
            ["loopTop"] = KeyPointType.LoopTop,
            ["humpTop"] = KeyPointType.HumpTop,
            ["baselineTouch"] = KeyPointType.BaselineTouch,
        };

    public static readonly IReadOnlyDictionary<string, LineName> Lines =
        new Dictionary<string, LineName>(StringComparer.Ordinal)
        {
            ["ascender"] = LineName.Ascender,
            ["cap"] = LineName.Cap,
            ["xHeight"] = LineName.XHeight,
            ["baseline"] = LineName.Baseline,
            ["descender"] = LineName.Descender,
        };

    public static readonly IReadOnlyDictionary<string, ExtremeKind> Extremes =
        new Dictionary<string, ExtremeKind>(StringComparer.Ordinal) { ["max"] = ExtremeKind.Max, ["min"] = ExtremeKind.Min };

    public static readonly IReadOnlyDictionary<string, StartDotMode> StartDots =
        new Dictionary<string, StartDotMode>(StringComparer.Ordinal)
        {
            ["none"] = StartDotMode.None,
            ["leadIn"] = StartDotMode.LeadIn,
            ["body"] = StartDotMode.Body,
        };

    public static readonly IReadOnlyDictionary<string, StyleRole> Roles =
        new Dictionary<string, StyleRole>(StringComparer.Ordinal)
        {
            ["teach"] = StyleRole.Teach,
            ["print"] = StyleRole.Print,
            ["confuser"] = StyleRole.Confuser,
        };

    public static readonly IReadOnlyDictionary<string, JoinShape> JoinShapes =
        new Dictionary<string, JoinShape>(StringComparer.Ordinal)
        {
            ["hermite"] = JoinShape.Hermite,
            ["hermite-over"] = JoinShape.HermiteOver,
            ["break"] = JoinShape.Break,
        };

    public static readonly IReadOnlyDictionary<string, CapitalJoinMode> CapitalJoinModes =
        new Dictionary<string, CapitalJoinMode>(StringComparer.Ordinal)
        {
            ["break"] = CapitalJoinMode.Break,
            ["join"] = CapitalJoinMode.Join,
        };

    public static readonly IReadOnlyDictionary<string, DeferredTeachTiming> TeachTimings =
        new Dictionary<string, DeferredTeachTiming>(StringComparer.Ordinal)
        {
            ["afterWord"] = DeferredTeachTiming.AfterWord,
            ["afterLetter"] = DeferredTeachTiming.AfterLetter,
        };

    /// <summary>Допустимые источники форм глифа (<c>meta.source</c>).</summary>
    public static readonly IReadOnlyList<string> GlyphSources = ["illustrative", "authored", "traced"];

    /// <summary>Допустимые признаки тела (кроме <c>humps:N</c>).</summary>
    public static readonly IReadOnlyList<string> Features =
        ["retrace", "upperLoop", "lowerLoop", "closedOval", "ascender", "descender", "deferred"];

    public static string Name(KeyPointType value) => Find(KeyPointTypes, value);

    public static string Name(LineName value) => Find(Lines, value);

    public static string Name(DeferredKind value) => Find(DeferredKinds, value);

    private static string Find<T>(IReadOnlyDictionary<string, T> map, T value)
        where T : struct, Enum
    {
        foreach (KeyValuePair<string, T> pair in map)
        {
            if (EqualityComparer<T>.Default.Equals(pair.Value, value))
            {
                return pair.Key;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown value.");
    }
}
