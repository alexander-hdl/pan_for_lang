using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Animation;

/// <summary>Узел временной шкалы штриха: к моменту <see cref="TimeMs"/> (от начала штриха) перо прошло <see cref="Distance"/>.</summary>
/// <param name="TimeMs">Время от начала штриха, мс.</param>
/// <param name="Distance">Пройденная длина дуги пути штриха, x-height.</param>
public readonly record struct TimeKnot(double TimeMs, double Distance);

/// <summary>
/// Штрих анимации: что рисует перо за одно касание. Для первого основного штриха путь начинается с входящего штриха,
/// для последнего основного — заканчивается выходящим (если они включены в <see cref="AnimationOptions"/>).
/// Время и расстояние связаны кусочно-линейной функцией <see cref="Knots"/> (горизонтальные участки — паузы).
/// </summary>
public sealed class AnimatedStroke
{
    /// <summary>Создаёт штрих анимации. Узлы должны не убывать по времени и расстоянию.</summary>
    /// <param name="strokeId">Идентификатор штриха глифа.</param>
    /// <param name="number">Номер штриха в порядке написания, с 1.</param>
    /// <param name="isDeferred">Отложенный штрих.</param>
    /// <param name="path">Анимируемый путь.</param>
    /// <param name="bodyStart">Расстояние от начала <paramref name="path"/> до начала тела штриха (длина входящего штриха).</param>
    /// <param name="bodyLength">Длина тела штриха.</param>
    /// <param name="startMs">Начало штриха на общей шкале, мс.</param>
    /// <param name="knots">Узлы шкалы времени (первый — <c>(0, 0)</c>, последний — вся длина пути).</param>
    public AnimatedStroke(
        string strokeId,
        int number,
        bool isDeferred,
        GlyphPath path,
        double bodyStart,
        double bodyLength,
        double startMs,
        ImmutableArray<TimeKnot> knots)
    {
        ArgumentNullException.ThrowIfNull(strokeId);
        ArgumentNullException.ThrowIfNull(path);
        if (knots.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A stroke needs at least one time knot.", nameof(knots));
        }

        StrokeId = strokeId;
        Number = number;
        IsDeferred = isDeferred;
        Path = path;
        BodyStart = bodyStart;
        BodyLength = bodyLength;
        StartMs = startMs;
        Knots = knots;
        EndMs = startMs + knots[^1].TimeMs;
    }

    /// <summary>Идентификатор штриха глифа.</summary>
    public string StrokeId { get; }

    /// <summary>Номер в порядке написания, с 1.</summary>
    public int Number { get; }

    /// <summary>Отложенный штрих (ставится после основных).</summary>
    public bool IsDeferred { get; }

    /// <summary>Анимируемый путь (с входящим и выходящим штрихами, если они включены).</summary>
    public GlyphPath Path { get; }

    /// <summary>Расстояние от начала <see cref="Path"/> до начала тела штриха.</summary>
    public double BodyStart { get; }

    /// <summary>Длина тела штриха.</summary>
    public double BodyLength { get; }

    /// <summary>Начало штриха на общей шкале, мс.</summary>
    public double StartMs { get; }

    /// <summary>Конец штриха на общей шкале, мс.</summary>
    public double EndMs { get; }

    /// <summary>Узлы шкалы времени штриха.</summary>
    public ImmutableArray<TimeKnot> Knots { get; }

    /// <summary>Расстояние, пройденное пером к моменту <paramref name="timeMs"/> общей шкалы (0 до начала, длина пути после конца).</summary>
    /// <param name="timeMs">Время на общей шкале, мс.</param>
    public double DistanceAt(double timeMs)
    {
        double local = timeMs - StartMs;
        if (!(local > 0.0))
        {
            return 0.0;
        }

        if (local >= Knots[^1].TimeMs)
        {
            return Path.Length;
        }

        for (int i = 1; i < Knots.Length; i++)
        {
            TimeKnot a = Knots[i - 1];
            TimeKnot b = Knots[i];
            if (local <= b.TimeMs)
            {
                double span = b.TimeMs - a.TimeMs;
                return span <= 0.0 ? b.Distance : a.Distance + ((b.Distance - a.Distance) * ((local - a.TimeMs) / span));
            }
        }

        return Path.Length;
    }

    /// <summary>Первый момент общей шкалы, когда перо достигает расстояния <paramref name="distance"/>.</summary>
    /// <param name="distance">Расстояние от начала пути.</param>
    public double TimeAtDistance(double distance)
    {
        if (!(distance > 0.0))
        {
            return StartMs;
        }

        for (int i = 1; i < Knots.Length; i++)
        {
            TimeKnot a = Knots[i - 1];
            TimeKnot b = Knots[i];
            if (distance <= b.Distance && b.Distance > a.Distance)
            {
                double share = (distance - a.Distance) / (b.Distance - a.Distance);
                return StartMs + a.TimeMs + (share * (b.TimeMs - a.TimeMs));
            }
        }

        return EndMs;
    }
}

/// <summary>Номер штриха, появляющийся на анимации.</summary>
/// <param name="StrokeIndex">Индекс штриха в <see cref="AnimationScript.Strokes"/>.</param>
/// <param name="Number">Показываемый номер, с 1.</param>
/// <param name="Position">Положение значка в координатах линейки.</param>
/// <param name="AppearMs">Когда появляется (с этого момента виден до конца).</param>
public sealed record ScriptNumber(int StrokeIndex, int Number, Vec2 Position, double AppearMs);

/// <summary>Стрелка направления, появляющаяся, когда перо проходит её положение.</summary>
/// <param name="StrokeIndex">Индекс штриха в <see cref="AnimationScript.Strokes"/>.</param>
/// <param name="Position">Положение стрелки.</param>
/// <param name="Direction">Единичное направление движения пера в этой точке.</param>
/// <param name="AppearMs">Когда появляется (момент прохождения пером; с него виден до конца).</param>
public sealed record ScriptArrow(int StrokeIndex, Vec2 Position, Vec2 Direction, double AppearMs);

/// <summary>Точка старта штриха.</summary>
/// <param name="StrokeIndex">Индекс штриха в <see cref="AnimationScript.Strokes"/>.</param>
/// <param name="Position">Положение точки.</param>
/// <param name="AppearMs">Когда появляется (с этого момента видна до конца).</param>
public sealed record ScriptStartDot(int StrokeIndex, Vec2 Position, double AppearMs);

/// <summary>
/// Состояние отрисовки анимации в заданный момент: что нарисовано, где перо, какие стрелки и номера видны.
/// Индексы ссылаются на списки <see cref="AnimationScript"/>.
/// </summary>
/// <param name="TimeMs">Момент времени (после обрезки в [0, длительность]), мс.</param>
/// <param name="StrokeProgress">Для каждого штриха доля нарисованной длины пути: 0 — не начат, 1 — готов целиком.</param>
/// <param name="ActiveStroke">Индекс штриха, который перо рисует сейчас, или −1.</param>
/// <param name="PenDown">Перо касается поверхности (рисует), иначе перо в воздухе.</param>
/// <param name="PenPosition">Положение пера в координатах линейки.</param>
/// <param name="VisibleNumbers">Индексы видимых номеров (<see cref="AnimationScript.Numbers"/>).</param>
/// <param name="VisibleArrows">Индексы видимых стрелок (<see cref="AnimationScript.Arrows"/>).</param>
/// <param name="VisibleStartDots">Индексы видимых точек старта (<see cref="AnimationScript.StartDots"/>).</param>
/// <param name="IsFinished">Анимация закончилась (момент не раньше конца).</param>
public sealed record AnimationState(
    double TimeMs,
    ImmutableArray<double> StrokeProgress,
    int ActiveStroke,
    bool PenDown,
    Vec2 PenPosition,
    ImmutableArray<int> VisibleNumbers,
    ImmutableArray<int> VisibleArrows,
    ImmutableArray<int> VisibleStartDots,
    bool IsFinished)
{
    /// <summary>Индексы штрихов, нарисованных целиком.</summary>
    public IEnumerable<int> CompletedStrokes =>
        Enumerable.Range(0, StrokeProgress.Length).Where(i => StrokeProgress[i] >= 1.0);
}

/// <summary>
/// Временная шкала анимации глифа: порядок штрихов, скорость по длине дуги, паузы, появление стрелок и номеров,
/// отложенные штрихи в конце. Чистый детерминированный объект; состояние на момент времени даёт <see cref="StateAt"/>.
/// Создаётся <see cref="AnimationScriptBuilder"/>.
/// </summary>
public sealed class AnimationScript
{
    /// <summary>Создаёт сценарий. Используйте <see cref="AnimationScriptBuilder"/>.</summary>
    /// <param name="glyph">Глиф; <c>null</c> для сценария слова.</param>
    /// <param name="settings">Настройки анимации стиля.</param>
    /// <param name="strokes">Штрихи по порядку на общей шкале (основные, затем отложенные).</param>
    /// <param name="numbers">Номера.</param>
    /// <param name="arrows">Стрелки.</param>
    /// <param name="startDots">Точки старта.</param>
    /// <param name="durationMs">Полная длительность, мс.</param>
    /// <param name="word">Эталон слова для сценария слова; для глифа <c>null</c>.</param>
    public AnimationScript(
        Glyph? glyph,
        AnimationSettings settings,
        ImmutableArray<AnimatedStroke> strokes,
        ImmutableArray<ScriptNumber> numbers,
        ImmutableArray<ScriptArrow> arrows,
        ImmutableArray<ScriptStartDot> startDots,
        double durationMs,
        WordTemplate? word = null)
    {
        if (glyph is null == word is null)
        {
            throw new ArgumentException("A script is built for exactly one of: a glyph or a word.", nameof(glyph));
        }

        ArgumentNullException.ThrowIfNull(settings);
        if (strokes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A script needs at least one stroke.", nameof(strokes));
        }

        Glyph = glyph;
        Word = word;
        Settings = settings;
        Strokes = strokes;
        Numbers = numbers;
        Arrows = arrows;
        StartDots = startDots;
        DurationMs = durationMs;
    }

    /// <summary>Глиф, для которого построен сценарий, или <c>null</c> для сценария слова.</summary>
    public Glyph? Glyph { get; }

    /// <summary>Слово, для которого построен сценарий, или <c>null</c> для сценария глифа.</summary>
    public WordTemplate? Word { get; }

    /// <summary>Настройки, по которым построена шкала.</summary>
    public AnimationSettings Settings { get; }

    /// <summary>Штрихи в порядке написания: основные, затем отложенные.</summary>
    public ImmutableArray<AnimatedStroke> Strokes { get; }

    /// <summary>Номера штрихов.</summary>
    public ImmutableArray<ScriptNumber> Numbers { get; }

    /// <summary>Стрелки направления.</summary>
    public ImmutableArray<ScriptArrow> Arrows { get; }

    /// <summary>Точки старта.</summary>
    public ImmutableArray<ScriptStartDot> StartDots { get; }

    /// <summary>Полная длительность, мс.</summary>
    public double DurationMs { get; }

    /// <summary>
    /// Состояние отрисовки в момент <paramref name="timeMs"/> (обрезается до [0, <see cref="DurationMs"/>]).
    /// Прогресс штрихов, множества видимых стрелок, номеров и точек не убывают с ростом времени.
    /// </summary>
    /// <param name="timeMs">Время от начала анимации, мс.</param>
    public AnimationState StateAt(double timeMs)
    {
        double t = double.IsNaN(timeMs) ? 0.0 : Math.Clamp(timeMs, 0.0, DurationMs);

        // Последний штрих, начавшийся к моменту t (или -1 до первого штриха).
        int started = -1;
        for (int i = 0; i < Strokes.Length; i++)
        {
            if (Strokes[i].StartMs <= t)
            {
                started = i;
            }
        }

        var progress = ImmutableArray.CreateBuilder<double>(Strokes.Length);
        for (int i = 0; i < Strokes.Length; i++)
        {
            AnimatedStroke stroke = Strokes[i];
            double value;
            if (i > started)
            {
                value = 0.0;
            }
            else if (t >= stroke.EndMs)
            {
                value = 1.0;
            }
            else
            {
                value = stroke.Path.Length > 0.0 ? Math.Clamp(stroke.DistanceAt(t) / stroke.Path.Length, 0.0, 1.0) : 1.0;
            }

            progress.Add(value);
        }

        int active = -1;
        bool penDown = false;
        Vec2 pen;
        if (started < 0)
        {
            pen = Strokes[0].Path.Start;
        }
        else if (t < Strokes[started].EndMs)
        {
            active = started;
            penDown = true;
            pen = Strokes[started].Path.PointAtDistance(Strokes[started].DistanceAt(t));
        }
        else if (started + 1 < Strokes.Length)
        {
            AnimatedStroke from = Strokes[started];
            AnimatedStroke to = Strokes[started + 1];
            double span = to.StartMs - from.EndMs;
            pen = span > 0.0 ? Vec2.Lerp(from.Path.End, to.Path.Start, (t - from.EndMs) / span) : to.Path.Start;
        }
        else
        {
            pen = Strokes[started].Path.End;
        }

        return new AnimationState(
            t,
            progress.MoveToImmutable(),
            active,
            penDown,
            pen,
            VisibleIndices(Numbers.Length, i => Numbers[i].AppearMs <= t),
            VisibleIndices(Arrows.Length, i => Arrows[i].AppearMs <= t),
            VisibleIndices(StartDots.Length, i => StartDots[i].AppearMs <= t),
            t >= DurationMs);
    }

    private static ImmutableArray<int> VisibleIndices(int count, Func<int, bool> predicate)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < count; i++)
        {
            if (predicate(i))
            {
                result.Add(i);
            }
        }

        return result.ToImmutable();
    }
}
