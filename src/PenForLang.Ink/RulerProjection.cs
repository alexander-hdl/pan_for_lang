using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>Точка в системе линейки: baseline = 0, x-height = 1, ось V вверх (ARCHITECTURE.md §3.1).</summary>
/// <param name="U">Горизонталь в x-height от начала строки.</param>
/// <param name="V">Вертикаль в x-height от baseline вверх.</param>
/// <param name="TimeUs">Время, мкс от начала задания.</param>
/// <param name="Pressure">Давление.</param>
public readonly record struct RulerPoint(double U, double V, long TimeUs, float Pressure);

/// <summary>Штрих в системе линейки.</summary>
/// <param name="Index">Индекс исходного штриха в попытке (сохраняется при отбрасывании вспомогательных штрихов).</param>
/// <param name="Points">Точки.</param>
/// <param name="Clipped">Хотя бы одна точка штриха была за краем холста (A11).</param>
public sealed record RulerStroke(int Index, ImmutableArray<RulerPoint> Points, bool Clipped);

/// <summary>Попытка в системе линейки — вход оценщика и глифов.</summary>
/// <param name="Strokes">Штрихи без hover/барабанной кнопки/ластика.</param>
/// <param name="StimulusEndUs">Время окончания показа стимула, мкс.</param>
/// <param name="MmPerUnit">Миллиметров в одной единице линейки (x-height в мм).</param>
/// <param name="InterpolatedTime">У каких-либо точек время интерполировано.</param>
public sealed record RulerInk(ImmutableArray<RulerStroke> Strokes, long? StimulusEndUs, double MmPerUnit, bool InterpolatedTime);

/// <summary>
/// Проекция координат поверхности письма в систему линейки и обратно (ARCHITECTURE.md §3.1):
/// <c>u = (x − x0) / h</c>, <c>v = (yBaseline − y) / h</c>, <c>h = xHeightPx = xHeightMm · pxPerMm</c>.
/// </summary>
/// <remarks>
/// Поверхность уже в физических px, поэтому DPI в проекции не участвует (A12). Наклон линейки
/// (<see cref="GuideFrame.SlantDeg"/>) в v0 — заглушка и в проекцию не входит (в §3.1 формулы со сдвигом нет).
/// </remarks>
public static class RulerProjection
{
    /// <summary>Точка поверхности (физические px) → (u, v) линейки.</summary>
    public static (double U, double V) ToRuler(GuideFrame guides, double xPx, double yPx)
    {
        ArgumentNullException.ThrowIfNull(guides);
        return ((xPx - guides.OriginXPx) / guides.XHeightPx, (guides.BaselineYPx - yPx) / guides.XHeightPx);
    }

    /// <summary>(u, v) линейки → точка поверхности (физические px). Обратная к <see cref="ToRuler"/>.</summary>
    public static (double X, double Y) ToSurface(GuideFrame guides, double u, double v)
    {
        ArgumentNullException.ThrowIfNull(guides);
        return (guides.OriginXPx + (u * guides.XHeightPx), guides.BaselineYPx - (v * guides.XHeightPx));
    }

    /// <summary>
    /// Проецирует попытку. Точки hover, с барабанной кнопкой и ластиком отбрасываются; штрихи, в которых не осталось
    /// точек, пропускаются (индексы оставшихся сохраняются).
    /// </summary>
    public static RulerInk Project(InkAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        GuideFrame guides = attempt.Guides;
        var strokes = ImmutableArray.CreateBuilder<RulerStroke>(attempt.Strokes.Length);
        bool interpolated = false;

        foreach (InkStroke stroke in attempt.Strokes)
        {
            var points = ImmutableArray.CreateBuilder<RulerPoint>(stroke.Samples.Length);
            bool clipped = false;
            foreach (PenSample sample in stroke.Samples)
            {
                if (!sample.IsContact || sample.IsAuxiliary)
                {
                    continue;
                }

                (double u, double v) = ToRuler(guides, sample.X, sample.Y);
                points.Add(new RulerPoint(u, v, sample.TimeUs, sample.Pressure));
                clipped |= sample.IsClipped;
                interpolated |= (sample.Flags & SampleFlags.InterpolatedTime) != 0;
            }

            if (points.Count > 0)
            {
                strokes.Add(new RulerStroke(stroke.Index, points.ToImmutable(), clipped));
            }
        }

        return new RulerInk(strokes.ToImmutable(), attempt.StimulusEndUs, guides.XHeightMm, interpolated);
    }
}
