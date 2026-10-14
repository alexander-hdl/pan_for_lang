using System.Collections.Immutable;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;
using PenForLang.Ink;

namespace PenForLang.Testing.SynthInk;

/// <summary>Мутации синтетической попытки слова с разметкой ожидаемой находки (ARCHITECTURE.md §10.3, A03).</summary>
public enum SynthWordMutation
{
    /// <summary>Без мутации: слитное письмо, отложенные штрихи после слова — без J1 и без D4.</summary>
    None,

    /// <summary>Отрыв пера на каждом соединении, в случайном месте соединения: оторвано и поставлено на то же место (J1, граница в допуске).</summary>
    LiftAtJoin,

    /// <summary>Пропуск соединения: тело левой буквы кончается, перо оторвано, тело правой буквы начинается без соединительного штриха (J1).</summary>
    SkipJoin,

    /// <summary>Точка или перекладина каждой буквы, кроме последней, поставлена сразу после тела своей буквы, до соединения (D4 и ни одного J1: отрыв ожидаем). У последней буквы «сразу» совпадает с «после слова».</summary>
    DeferredImmediately,

    /// <summary>Нет точки или перекладины (D1).</summary>
    MissingDeferred,

    /// <summary>Перо оторвано посреди тела каждой буквы и начато с того же места (A03): <c>LiftsInsideLetter</c>, не J1 и не стоп-ошибка.</summary>
    LiftInsideLetter,
}

/// <summary>Истинная граница между буквами в синтетической попытке: где кончается тело левой и начинается тело правой.</summary>
/// <param name="Join">Индекс соединения.</param>
/// <param name="End">Конец тела левой буквы.</param>
/// <param name="Start">Начало тела правой буквы.</param>
/// <param name="Lifted">Между ними оторвано перо (мутация).</param>
public sealed record SynthJoinTruth(int Join, SynthInkPoint End, SynthInkPoint Start, bool Lifted);

/// <summary>Точка синтетической попытки: штрих, номер точки в нём и положение в линейке.</summary>
/// <param name="Stroke"><see cref="RulerStroke.Index"/>.</param>
/// <param name="Point">Индекс в <see cref="RulerStroke.Points"/>.</param>
/// <param name="U">u.</param>
/// <param name="V">v.</param>
public sealed record SynthInkPoint(int Stroke, int Point, double U, double V);

/// <summary>Истинный отложенный штрих синтетической попытки слова.</summary>
/// <param name="Letter">Буква.</param>
/// <param name="SlotId">Слот глифа (<c>dot</c>, <c>bar</c>).</param>
/// <param name="Kind">Вид.</param>
/// <param name="Stroke"><see cref="RulerStroke.Index"/> штриха в попытке.</param>
/// <param name="Immediate">После штриха пишется ещё основной штрих (поставлен сразу, а не после слова).</param>
public sealed record SynthDeferredTruth(int Letter, string SlotId, DeferredKind Kind, int Stroke, bool Immediate);

/// <summary>Синтетическая попытка слова с разметкой.</summary>
/// <param name="Ink">Попытка в координатах линейки.</param>
/// <param name="Mutation">Внесённая мутация.</param>
/// <param name="Expected">Ожидаемые находки (все должны присутствовать); место соединения — идентификатор <c>join{k}</c>.</param>
/// <param name="Forbidden">Коды, которых быть не должно.</param>
/// <param name="Joins">Истинные границы букв.</param>
/// <param name="LiftsInsideLetter">Сколько отрывов внутри тел букв внесено мутацией.</param>
public sealed record SynthWordSample(
    RulerInk Ink,
    SynthWordMutation Mutation,
    ImmutableArray<ExpectedFinding> Expected,
    ImmutableArray<ErrorCode> Forbidden,
    ImmutableArray<SynthJoinTruth> Joins,
    int LiftsInsideLetter = 0)
{
    /// <summary>Отложенные штрихи попытки с номерами штрихов и временем постановки (для разметки корпуса).</summary>
    public ImmutableArray<SynthDeferredTruth> Deferred { get; init; } = [];
}

/// <summary>Синтетические попытки слов из <see cref="WordTemplate"/> (V0.10).</summary>
public sealed partial class SynthInkGenerator
{
    /// <summary>Идеальная траектория слова без вариаций: самооценка эталона слова должна дать ноль находок.</summary>
    /// <param name="word">Эталон слова.</param>
    public RulerInk Ideal(WordTemplate word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return Render(IdealWord(word), Variation.Identity, new Pcg32(0), noisy: false);
    }

    /// <summary>Корректный зашумлённый образец слова (слитно, отложенные после слова).</summary>
    /// <param name="word">Эталон слова.</param>
    /// <param name="seed">Зерно.</param>
    public RulerInk Clean(WordTemplate word, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(word);
        Pcg32 rng = Rng("word:" + word.Text, seed);
        Variation variation = Variation.Sample(_options, rng);
        return Render(IdealWord(word), variation, rng, noisy: true);
    }

    /// <summary>
    /// Образец слова с одной мутацией, разметкой ожидаемых находок и истинными границами букв. Возвращает <c>null</c>,
    /// если мутация к слову неприменима (нет соединений с геометрией, нет отложенных штрихов).
    /// </summary>
    /// <param name="word">Эталон слова.</param>
    /// <param name="mutation">Мутация.</param>
    /// <param name="seed">Зерно.</param>
    public SynthWordSample? Mutate(WordTemplate word, SynthWordMutation mutation, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(word);
        Pcg32 rng = Rng("word:" + word.Text, seed);
        Variation variation = Variation.Sample(_options, rng);
        List<IdealStroke> strokes = IdealWord(word);
        var lifted = new HashSet<int>();
        int insideLetter = 0;
        ImmutableArray<ExpectedFinding> expected = [];
        ImmutableArray<ErrorCode> forbidden = [ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4, ErrorCode.P6];
        List<int> geometric = [.. word.Joins.Where(j => j.Path is not null).Select(j => j.Index)];
        switch (mutation)
        {
            case SynthWordMutation.None:
                break;
            case SynthWordMutation.LiftAtJoin:
                {
                    if (geometric.Count == 0)
                    {
                        return null;
                    }

                    foreach (int k in geometric.OrderByDescending(x => x))
                    {
                        (int pen, WordPart join) = JoinPart(word, k);
                        double at = join.FromDistance + ((0.05 + (0.9 * rng.NextDouble())) * (join.ToDistance - join.FromDistance));
                        SplitAtDistance(strokes, pen, at);
                        lifted.Add(k);
                    }

                    expected = [.. geometric.Select(k => new ExpectedFinding(ErrorCode.J1, $"join{k}"))];
                    forbidden = [ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];
                    break;
                }

            case SynthWordMutation.SkipJoin:
                {
                    if (geometric.Count == 0)
                    {
                        return null;
                    }

                    foreach (int k in geometric.OrderByDescending(x => x))
                    {
                        (int pen, WordPart join) = JoinPart(word, k);
                        DropBetween(strokes, pen, join.FromDistance, join.ToDistance);
                        lifted.Add(k);
                    }

                    expected = [.. geometric.Select(k => new ExpectedFinding(ErrorCode.J1, $"join{k}"))];
                    forbidden = [ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];
                    break;
                }

            case SynthWordMutation.DeferredImmediately:
                {
                    if (word.Deferred.Length == 0)
                    {
                        return null;
                    }

                    var moved = new List<ExpectedFinding>();
                    var main = strokes.Where(s => !s.IsDeferred).ToList();
                    List<IdealStroke> deferred = [.. strokes.Where(s => s.IsDeferred)];
                    var placements = new List<(IdealStroke Piece, IdealStroke Mark)>();
                    for (int d = 0; d < word.Deferred.Length; d++)
                    {
                        WordDeferred slot = word.Deferred[d];
                        if (slot.Letter == word.Letters.Length - 1)
                        {
                            continue;
                        }

                        (int pen, WordPart body) = LastBodyPart(word, slot.Letter);
                        IdealStroke before = SplitAtDistance(main, pen, body.ToDistance)!;
                        placements.Add((before, deferred[d]));
                        moved.Add(new ExpectedFinding(ErrorCode.D4, slot.SlotId));
                    }

                    // Порядок записи: основные штрихи, перед которыми разрыв, получают точку сразу после них.
                    var ordered = new List<IdealStroke>();
                    foreach (IdealStroke piece in main)
                    {
                        ordered.Add(piece);
                        foreach ((IdealStroke before, IdealStroke mark) in placements)
                        {
                            if (ReferenceEquals(before, piece))
                            {
                                ordered.Add(mark);
                            }
                        }
                    }

                    ordered.AddRange(deferred.Where(m => !placements.Any(p => ReferenceEquals(p.Mark, m))));
                    strokes = ordered;
                    if (moved.Count == 0)
                    {
                        // Все отложенные штрихи — у последней буквы: «сразу» совпадает с «после слова».
                        expected = [];
                    }
                    else
                    {
                        expected = [.. moved];
                    }

                    forbidden = [ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.P6];
                    if (moved.Count == 0)
                    {
                        forbidden = [.. forbidden, ErrorCode.D4];
                    }

                    break;
                }

            case SynthWordMutation.MissingDeferred:
                if (word.Deferred.Length == 0)
                {
                    return null;
                }

                strokes.RemoveAll(s => s.IsDeferred);
                expected = [.. word.Deferred.Select(d => new ExpectedFinding(ErrorCode.D1, d.SlotId))];
                forbidden = [ErrorCode.J1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];
                break;
            case SynthWordMutation.LiftInsideLetter:
                {
                    // С конца слова к началу: расстояния кусков, ещё не разрезанных, остаются прежними.
                    for (int k = word.Letters.Length - 1; k >= 0; k--)
                    {
                        (int pen, WordPart first) = FirstBodyPart(word, k);
                        (_, WordPart last) = LastBodyPart(word, k);
                        SplitAtDistance(strokes, pen, (first.FromDistance + last.ToDistance) / 2.0);
                        insideLetter++;
                    }

                    forbidden = [ErrorCode.J1, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        Retime(strokes);
        List<IdealStroke> sorted = [.. strokes.OrderBy(s => s.StartMs)];
        RulerInk ink = Render(strokes, variation, rng, noisy: true);
        var deferredTruth = ImmutableArray.CreateBuilder<SynthDeferredTruth>();
        int lastMain = sorted.FindLastIndex(x => !x.IsDeferred);
        for (int i = 0; i < sorted.Count; i++)
        {
            if (!sorted[i].IsDeferred)
            {
                continue;
            }

            WordDeferred slot = word.Deferred.First(d => $"{d.SlotId}.{d.Letter}" == sorted[i].Id);
            deferredTruth.Add(new SynthDeferredTruth(slot.Letter, slot.SlotId, slot.Kind, ink.Strokes[i].Index, i < lastMain));
        }

        return new SynthWordSample(ink, mutation, expected, forbidden, Truth(word, sorted, ink, lifted), insideLetter) { Deferred = deferredTruth.ToImmutable() };
    }

    private List<IdealStroke> IdealWord(WordTemplate word) =>
        IdealFromScript(AnimationScriptBuilder.Build(word, _style.Animation, new AnimationOptions(IncludeLeads: true)));

    private static (int Pen, WordPart Part) JoinPart(WordTemplate word, int join)
    {
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts)
            {
                if (part.Kind == WordPartKind.Join && part.Letter == join)
                {
                    return (pen.Index, part);
                }
            }
        }

        throw new InvalidOperationException($"The word has no geometric join {join}.");
    }

    private static (int Pen, WordPart Part) LastBodyPart(WordTemplate word, int letter)
    {
        (int, WordPart)? found = null;
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts)
            {
                if (part.Kind == WordPartKind.Body && part.Letter == letter)
                {
                    found = (pen.Index, part);
                }
            }
        }

        return found ?? throw new InvalidOperationException($"The word has no letter {letter}.");
    }

    private static (int Pen, WordPart Part) FirstBodyPart(WordTemplate word, int letter)
    {
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts)
            {
                if (part.Kind == WordPartKind.Body && part.Letter == letter)
                {
                    return (pen.Index, part);
                }
            }
        }

        throw new InvalidOperationException($"The word has no letter {letter}.");
    }

    /// <summary>Находит кусок штриха слова <paramref name="pen"/>, содержащий расстояние <paramref name="distance"/>, и его индекс в списке.</summary>
    private static int FindPiece(List<IdealStroke> strokes, int pen, double distance)
    {
        for (int i = 0; i < strokes.Count; i++)
        {
            IdealStroke s = strokes[i];
            if (s.PenIndex != pen)
            {
                continue;
            }

            double lo = double.PositiveInfinity;
            double hi = double.NegativeInfinity;
            foreach (double d in s.Distances)
            {
                if (d >= 0.0)
                {
                    lo = Math.Min(lo, d);
                    hi = Math.Max(hi, d);
                }
            }

            if (distance >= lo - 1e-9 && distance <= hi + 1e-9)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No piece of the word stroke holds the distance.");
    }

    private static int IndexAtDistance(IdealStroke s, double distance)
    {
        for (int i = 0; i < s.Distances.Count; i++)
        {
            if (s.Distances[i] >= distance - 1e-9)
            {
                return i;
            }
        }

        return s.Distances.Count - 1;
    }

    private static IdealStroke Slice(IdealStroke source, int from, int to)
    {
        var piece = new IdealStroke(source.Id, source.IsDeferred, source.BodyStartDistance, source.BodyLength) { PenIndex = source.PenIndex };
        for (int i = from; i <= to; i++)
        {
            piece.Points.Add(source.Points[i]);
            piece.TimesMs.Add(source.TimesMs[i]);
            piece.Distances.Add(source.Distances[i]);
        }

        return piece;
    }

    /// <summary>Разрезает штрих слова в точке на расстоянии <paramref name="distance"/>: перо оторвано и поставлено на то же место. Возвращает первую часть.</summary>
    private static IdealStroke? SplitAtDistance(List<IdealStroke> strokes, int pen, double distance)
    {
        int at = FindPiece(strokes, pen, distance);
        IdealStroke piece = strokes[at];
        int cut = Math.Clamp(IndexAtDistance(piece, distance), 1, piece.Points.Count - 2);
        IdealStroke first = Slice(piece, 0, cut);
        IdealStroke second = Slice(piece, cut, piece.Points.Count - 1);
        strokes[at] = first;
        strokes.Insert(at + 1, second);
        return first;
    }

    /// <summary>Убирает точки штриха между расстояниями: тело левой буквы кончается, перо оторвано, тело правой начинается заново.</summary>
    private static void DropBetween(List<IdealStroke> strokes, int pen, double from, double to)
    {
        int at = FindPiece(strokes, pen, from);
        IdealStroke piece = strokes[at];
        int end = Math.Max(1, IndexAtDistance(piece, from));
        int start = Math.Min(piece.Points.Count - 2, IndexAtDistance(piece, to));
        if (start <= end)
        {
            start = Math.Min(piece.Points.Count - 1, end + 1);
        }

        strokes[at] = Slice(piece, 0, end);
        strokes.Insert(at + 1, Slice(piece, start, piece.Points.Count - 1));
    }

    /// <summary>Расставляет штрихи по порядку списка: каждый начинается через <see cref="GapMs"/> после конца предыдущего.</summary>
    private static void Retime(List<IdealStroke> ordered)
    {
        double cursor = ordered[0].StartMs;
        foreach (IdealStroke stroke in ordered)
        {
            ShiftTime(stroke, cursor - stroke.StartMs);
            cursor = stroke.EndMs + GapMs;
        }
    }

    private static ImmutableArray<SynthJoinTruth> Truth(WordTemplate word, List<IdealStroke> sorted, RulerInk ink, HashSet<int> lifted)
    {
        var result = ImmutableArray.CreateBuilder<SynthJoinTruth>();
        for (int k = 0; k + 1 < word.Letters.Length; k++)
        {
            (int endPen, WordPart endPart) = LastBodyPart(word, k);
            (int startPen, WordPart startPart) = FirstBodyPart(word, k + 1);
            SynthInkPoint end = Nearest(sorted, ink, endPen, endPart.ToDistance, firstOnTie: true);
            SynthInkPoint start = Nearest(sorted, ink, startPen, startPart.FromDistance, firstOnTie: false);
            result.Add(new SynthJoinTruth(k, end, start, lifted.Contains(k)));
        }

        return result.ToImmutable();
    }

    private static SynthInkPoint Nearest(List<IdealStroke> sorted, RulerInk ink, int pen, double distance, bool firstOnTie)
    {
        double best = double.PositiveInfinity;
        int bestStroke = 0;
        int bestPoint = 0;
        for (int s = 0; s < sorted.Count; s++)
        {
            if (sorted[s].PenIndex != pen)
            {
                continue;
            }

            for (int i = 0; i < sorted[s].Distances.Count; i++)
            {
                double d = sorted[s].Distances[i];
                if (d < 0.0)
                {
                    continue;
                }

                double diff = Math.Abs(d - distance);
                if (diff < best || (!firstOnTie && diff <= best))
                {
                    best = diff;
                    bestStroke = s;
                    bestPoint = i;
                }
            }
        }

        RulerPoint p = ink.Strokes[bestStroke].Points[bestPoint];
        return new SynthInkPoint(ink.Strokes[bestStroke].Index, bestPoint, p.U, p.V);
    }
}
