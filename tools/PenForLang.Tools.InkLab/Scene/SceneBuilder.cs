using System.Collections.Immutable;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;

namespace PenForLang.Tools.InkLab.Scene;

/// <summary>Строит <see cref="InkScene"/> из штрихов записи (повтор и разметка) и ищет ближайшую точку для клика.</summary>
public static class SceneBuilder
{
    /// <summary>Группа «без буквы» (повтор, неразмеченный след).</summary>
    public const int NeutralGroup = -1;

    /// <summary>Группа соединительного участка между концом левой и началом правой буквы.</summary>
    public const int JoinGroup = -2;

    /// <summary>Точка пера в координатах линейки.</summary>
    public static ScenePoint ToScene(GuideFrame frame, in PenSample sample)
    {
        ArgumentNullException.ThrowIfNull(frame);
        (double u, double v) = RulerProjection.ToRuler(frame, sample.X, sample.Y);
        return new ScenePoint(u, v);
    }

    /// <summary>Сколько точек в каждом штрихе (по порядку) — для проверки привязки разметки.</summary>
    public static ImmutableArray<int> StrokeSampleCounts(IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return [.. strokes.Select(s => s.Samples.Length)];
    }

    /// <summary>Сцена из штрихов без разметки.</summary>
    public static InkScene FromStrokes(IReadOnlyList<InkStroke> strokes, GuideFrame frame, SceneGuides guides, SceneStyle style = SceneStyle.Normal)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(guides);
        var lines = ImmutableArray.CreateBuilder<ScenePolyline>(strokes.Count);
        for (int s = 0; s < strokes.Count; s++)
        {
            ImmutableArray<ScenePoint> points = [.. strokes[s].Samples.Select(p => ToScene(frame, p))];
            if (points.Length > 0)
            {
                lines.Add(new ScenePolyline(points, NeutralGroup, style, s));
            }
        }

        return new InkScene(guides, lines.ToImmutable(), []);
    }

    /// <summary>
    /// Сцена с разметкой: основной след раскрашен по буквам (по <paramref name="junctions"/>), отложенные штрихи — отдельным видом,
    /// на стыках — метки. Допускает неполный набор стыков (разметка в процессе).
    /// </summary>
    /// <param name="strokes">Штрихи записи.</param>
    /// <param name="frame">Линейка записи.</param>
    /// <param name="guides">Линии линейки для рисования.</param>
    /// <param name="text">Что написано (для подписей меток).</param>
    /// <param name="junctions">Расставленные стыки (в любом порядке).</param>
    /// <param name="deferred">Отложенные штрихи.</param>
    public static InkScene FromAnnotation(
        IReadOnlyList<InkStroke> strokes,
        GuideFrame frame,
        SceneGuides guides,
        string text,
        IReadOnlyList<Junction> junctions,
        IReadOnlyList<DeferredMark> deferred)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(junctions);
        ArgumentNullException.ThrowIfNull(deferred);

        Junction[] byPosition = [.. junctions.OrderBy(j => j.End.Point)];
        HashSet<int> deferredStrokes = [.. deferred.Select(d => d.Stroke)];
        var lines = ImmutableArray.CreateBuilder<ScenePolyline>();

        for (int s = 0; s < strokes.Count; s++)
        {
            ImmutableArray<PenSample> samples = strokes[s].Samples;
            if (samples.Length == 0)
            {
                continue;
            }

            if (deferredStrokes.Contains(s))
            {
                lines.Add(new ScenePolyline([.. samples.Select(p => ToScene(frame, p))], NeutralGroup, SceneStyle.Deferred, s));
                continue;
            }

            var run = new List<ScenePoint>();
            int runGroup = int.MinValue;
            for (int i = 0; i < samples.Length; i++)
            {
                int group = GroupOf(byPosition, new StrokePoint(s, i));
                ScenePoint point = ToScene(frame, samples[i]);
                if (group != runGroup && run.Count > 0)
                {
                    // Новая ломаная начинается в последней точке предыдущей, чтобы линия не рвалась на стыке.
                    ScenePoint carry = run[^1];
                    lines.Add(new ScenePolyline([.. run], runGroup, SceneStyle.Normal, s));
                    run.Clear();
                    run.Add(carry);
                }

                runGroup = group;
                run.Add(point);
            }

            if (run.Count > 0)
            {
                lines.Add(new ScenePolyline([.. run], runGroup, SceneStyle.Normal, s));
            }
        }

        var markers = ImmutableArray.CreateBuilder<SceneMarker>();
        foreach (Junction j in byPosition)
        {
            bool single = j.Start.Point == j.End.Point;
            string full = Label(text, j.After);
            if (TryPoint(strokes, frame, j.End.Point, out ScenePoint end))
            {
                // Две точки стыка подписываются «a|» (выход левой буквы) и «|t» (вход правой); одна — «a|t».
                markers.Add(new SceneMarker(end.U, end.V, single ? full : EndLabel(text, j.After), MarkerKind.Junction));
            }

            if (!single && TryPoint(strokes, frame, j.Start.Point, out ScenePoint start))
            {
                markers.Add(new SceneMarker(start.U, start.V, StartLabel(text, j.After), MarkerKind.Junction));
            }
        }

        return new InkScene(guides, lines.ToImmutable(), markers.ToImmutable());
    }

    /// <summary>
    /// Номер буквы, которой принадлежит точка основного следа: по первому стыку (в порядке следа), конец которого не раньше точки;
    /// между концом и началом стыка — <see cref="JoinGroup"/>; после последнего стыка — следующая за ним буква.
    /// </summary>
    public static int GroupOf(IReadOnlyList<Junction> byPosition, StrokePoint point)
    {
        ArgumentNullException.ThrowIfNull(byPosition);
        foreach (Junction j in byPosition)
        {
            if (point <= j.End.Point)
            {
                return j.After;
            }

            if (point < j.Start.Point)
            {
                return JoinGroup;
            }
        }

        return byPosition.Count == 0 ? NeutralGroup : byPosition[^1].After + 1;
    }

    /// <summary>
    /// Ближайшая точка штрихов к месту клика (в координатах линейки) среди штрихов, допущенных <paramref name="include"/>, и точек, допущенных <paramref name="includePoint"/>.
    /// </summary>
    /// <returns>Точка и расстояние в x-height или <c>null</c>, если подходящих точек нет.</returns>
    public static (StrokePoint Point, double Distance)? Nearest(
        IReadOnlyList<InkStroke> strokes,
        GuideFrame frame,
        double u,
        double v,
        Func<int, bool>? include = null,
        Func<StrokePoint, bool>? includePoint = null)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(frame);
        (StrokePoint Point, double Distance)? best = null;
        for (int s = 0; s < strokes.Count; s++)
        {
            if (include is not null && !include(s))
            {
                continue;
            }

            ImmutableArray<PenSample> samples = strokes[s].Samples;
            for (int i = 0; i < samples.Length; i++)
            {
                if (includePoint is not null && !includePoint(new StrokePoint(s, i)))
                {
                    continue;
                }

                ScenePoint p = ToScene(frame, samples[i]);
                double distance = Math.Sqrt(((p.U - u) * (p.U - u)) + ((p.V - v) * (p.V - v)));
                if (best is null || distance < best.Value.Distance)
                {
                    best = (new StrokePoint(s, i), distance);
                }
            }
        }

        return best;
    }

    /// <summary>Подпись выхода левой буквы: «a|».</summary>
    public static string EndLabel(string text, int after) => after >= 0 && after < text.Length ? text[after] + "|" : "|";

    /// <summary>Подпись входа правой буквы: «|t».</summary>
    public static string StartLabel(string text, int after) => after >= 0 && after + 1 < text.Length ? "|" + text[after + 1] : "|";

    /// <summary>Подпись стыка: «a|t».</summary>
    public static string Label(string text, int after) =>
        after >= 0 && after + 1 < text.Length ? string.Concat(text.AsSpan(after, 1), "|", text.AsSpan(after + 1, 1)) : "|";

    private static bool TryPoint(IReadOnlyList<InkStroke> strokes, GuideFrame frame, StrokePoint point, out ScenePoint result)
    {
        if (point.Stroke >= 0 && point.Stroke < strokes.Count && point.Sample >= 0 && point.Sample < strokes[point.Stroke].Samples.Length)
        {
            result = ToScene(frame, strokes[point.Stroke].Samples[point.Sample]);
            return true;
        }

        result = default;
        return false;
    }
}
