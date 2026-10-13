using System.Collections.Immutable;

namespace PenForLang.Evaluation;

/// <summary>
/// Метрики одного сопоставления «штрих рукописи ↔ основной штрих эталона» (ARCHITECTURE.md §6.4.9). Все длины — в x-height
/// линейки; метрики формы посчитаны после нормализации (наклон, масштаб, сдвиг; §6.4.2, проход 1), метрики достижения
/// линий — в абсолютных единицах линейки (проход 2).
/// </summary>
/// <param name="StrokeId">Штрих эталона.</param>
/// <param name="InkStrokeIndex"><see cref="PenForLang.Ink.RulerStroke.Index"/> штриха рукописи.</param>
/// <param name="Reversed">Обращённый эталон лучше прямого по стоимости DTW (штрих написан в обратную сторону, P2).</param>
/// <param name="MatchCost">Стоимость DTW на точку рукописи (позиция² + направление) для выбранного направления.</param>
/// <param name="StartErr">Расстояние первой точки до ближайшего допустимого старта (подход leadIn, его продолжение влево или начало тела; для обратного — конец эталона).</param>
/// <param name="StartArcOffset">Длина дуги вдоль тела эталона от его начала до места, с которого DTW «начинает» рукопись (0 — на подходе или в начале тела).</param>
/// <param name="ReverseCostRatio">Отношение стоимости обращённого выравнивания к прямому (меньше 1 — обратное лучше).</param>
/// <param name="FirstSegmentAngleDeg">Угол между первым участком рукописи (0,2 x-h) и допустимым направлением старта, градусы.</param>
/// <param name="DtwMean">Среднее по точкам рукописи расстояние до выровненной точки эталона.</param>
/// <param name="DtwMax">Наибольшее такое расстояние.</param>
/// <param name="Frechet">Дискретное расстояние Фреше до участка эталона, покрытого путём DTW.</param>
/// <param name="CorridorShare">Доля точек рукописи в коридоре эталона.</param>
/// <param name="Coverage">Доля длины тела эталона, покрытая рукописью в коридоре.</param>
/// <param name="ClosedGap">Превышение зазора замыкания овала над эталонным (x-height) или <c>null</c>, если проверки нет.</param>
/// <param name="LoopOk">Нужная петля есть или <c>null</c>, если проверки нет.</param>
/// <param name="RetraceShare">Доля ретрейса, лежащая по написанному, относительно эталона или <c>null</c>, если проверки нет или она неприменима.</param>
/// <param name="StartOk">Старт в допуске.</param>
/// <param name="DirectionOk">Направление верно (штрих в целом и обход овалов и петель).</param>
/// <param name="RangeAreaRatio">Отношение ориентированных площадей участка (овал, петля) рукописи и эталона, если обход участка обратный (отрицательное), иначе <c>null</c>.</param>
public sealed record StrokeMatch(
    string StrokeId,
    int InkStrokeIndex,
    bool Reversed,
    double MatchCost,
    double StartErr,
    double StartArcOffset,
    double ReverseCostRatio,
    double FirstSegmentAngleDeg,
    double DtwMean,
    double DtwMax,
    double Frechet,
    double CorridorShare,
    double Coverage,
    double? ClosedGap,
    bool? LoopOk,
    double? RetraceShare,
    bool StartOk,
    bool DirectionOk,
    double? RangeAreaRatio);

/// <summary>Сопоставление штриха рукописи со слотом отложенного штриха.</summary>
/// <param name="SlotId">Идентификатор слота в глифе.</param>
/// <param name="InkStrokeIndex">Штрих рукописи или <c>null</c>, если слот пуст.</param>
/// <param name="Distance">Расстояние от середины штриха рукописи до ожидаемого положения (по реальному стволу), x-height, или <c>null</c>.</param>
/// <param name="Timing">Когда поставлен.</param>
public sealed record DeferredMatch(string SlotId, int? InkStrokeIndex, double? Distance, DeferredTiming Timing);

/// <summary>Вклад метрики в <c>form</c> (ARCHITECTURE.md §6.4.13).</summary>
/// <param name="Name">Имя метрики (ключ в <c>scoring.json → form.weights</c>).</param>
/// <param name="Value">Значение метрики.</param>
/// <param name="Score">Подбалл, [0, 1].</param>
/// <param name="Weight">Нормированный вес среди применимых метрик; сумма весов равна 1.</param>
public sealed record FormComponent(string Name, double Value, double Score, double Weight);

/// <summary>Параметры нормализации рукописи к эталону для выравнивания (§6.4.2, проход 1). Нормализация не к bounding box.</summary>
/// <param name="ShearDeg">Деслантинг рукописи к наклону эталона, градусы (0 — не применялся).</param>
/// <param name="Scale">Масштаб рукописи относительно эталона по робастной высоте (1 — не применялся).</param>
/// <param name="ShiftU">Итоговый сдвиг по u, x-height.</param>
/// <param name="ShiftV">Сдвиг по v (нормализация baseline), x-height.</param>
/// <param name="InkSlantDeg">Оценка наклона рукописи (медиана нисходящих квазивертикалей) или <c>null</c>.</param>
/// <param name="TemplateSlantDeg">То же для эталона или <c>null</c>.</param>
public sealed record NormalizationInfo(double ShearDeg, double Scale, double ShiftU, double ShiftV, double? InkSlantDeg, double? TemplateSlantDeg);

/// <summary>Точка рукописи: штрих и номер точки так же, как в <see cref="PenForLang.Ink.RulerInk"/> (и в разметке корпуса).</summary>
/// <param name="InkStrokeIndex"><see cref="PenForLang.Ink.RulerStroke.Index"/>.</param>
/// <param name="PointIndex">Индекс в <see cref="PenForLang.Ink.RulerStroke.Points"/>.</param>
/// <param name="U">u точки (линейка, без нормализации), x-height.</param>
/// <param name="V">v точки, x-height.</param>
/// <param name="TimeUs">Время точки, мкс.</param>
public sealed record InkPoint(int InkStrokeIndex, int PointIndex, double U, double V, long TimeUs);

/// <summary>
/// Граница между телами соседних букв слова (§6.4.5): конец тела левой буквы (<c>BodyEnd</c>) и начало тела правой
/// (<c>BodyStart</c>); участок между ними — соединение.
/// </summary>
/// <param name="Join">Индекс соединения (между буквами <c>Join</c> и <c>Join + 1</c>).</param>
/// <param name="BodyEnd">Где кончается тело левой буквы.</param>
/// <param name="BodyStart">Где начинается тело правой буквы.</param>
/// <param name="Lifted">Между ними оторвано перо (конец и начало лежат в разных штрихах рукописи).</param>
/// <param name="EndConfident">Граница <c>BodyEnd</c> уверенная: стоимость пути растёт при сдвиге на ±<c>word.boundaries.shiftU</c> (отрыв пера уверенный всегда).</param>
/// <param name="StartConfident">Граница <c>BodyStart</c> уверенная.</param>
/// <param name="EndRise">Наименьший рост полной стоимости пути при сдвиге <c>BodyEnd</c> на ±<c>shiftU</c>.</param>
/// <param name="StartRise">То же для <c>BodyStart</c>.</param>
public sealed record WordBoundary(int Join, InkPoint BodyEnd, InkPoint BodyStart, bool Lifted, bool EndConfident, bool StartConfident, double EndRise, double StartRise);

/// <summary>Метрики одного соединения слова (§6.4.9).</summary>
/// <param name="Join">Индекс соединения.</param>
/// <param name="Lifted">В диапазоне соединения оторвано перо.</param>
/// <param name="LiftExpected">Отрыв ожидаем (разрыв по стилю или отложенный штрих «сразу»).</param>
/// <param name="Drawn">Рукопись прошла не меньше <c>joins.drawnShare</c> точек эталона соединения.</param>
/// <param name="Coverage">Доля точек эталона соединения, пройденных рукописью.</param>
/// <param name="MinV">Наименьшее v рукописи в диапазоне соединения (нормализованное) или <c>null</c>, если соединение не нарисовано.</param>
/// <param name="EntryErr">Расстояние точки начала тела правой буквы в рукописи от эталонного входа после подгонки масштаба и сдвига тела этой буквы к эталону (то есть относительно буквы ребёнка, а не общей рамки слова), x-height.</param>
/// <param name="CornerExcessDeg">Наибольший угол поворота в соединении рукописи сверх эталонного, градусы, или <c>null</c>, если не проверялось.</param>
/// <param name="Loops">Число лишних петель в соединении.</param>
/// <param name="EntryAngleDeg">Угол между направлением прихода соединения в начало тела и эталонным входом, градусы, или <c>null</c>, если перо оторвано перед входом (прихода нет).</param>
public sealed record JoinMetrics(int Join, bool Lifted, bool LiftExpected, bool Drawn, double Coverage, double? MinV, double EntryErr, double? CornerExcessDeg, int Loops, double? EntryAngleDeg = null)
{
    /// <summary>J1 не выдаётся: в диапазоне нет отрыва, либо отрыв ожидаем (§6.4.9).</summary>
    public bool Present => !Lifted || LiftExpected;
}

/// <summary>Отрыв пера в основном следе слова (между штрихами рукописи).</summary>
/// <param name="BeforeInkStroke"><see cref="PenForLang.Ink.RulerStroke.Index"/> штриха, начатого после отрыва.</param>
/// <param name="Join">Индекс соединения, в диапазоне которого отрыв, или −1.</param>
/// <param name="Letter">Индекс буквы, внутри тела которой отрыв (<c>LiftsInsideLetter</c>), или −1.</param>
/// <param name="Expected">Отрыв ожидаем: рядом поставлен отложенный штрих «сразу» либо разрыв по правилу стиля.</param>
public sealed record WordLift(int BeforeInkStroke, int Join, int Letter, bool Expected);

/// <summary>Детализация оценки слова: границы букв, метрики соединений, отрывы и результаты по буквам.</summary>
/// <param name="Boundaries">Границы по порядку соединений.</param>
/// <param name="Joins">Метрики соединений.</param>
/// <param name="Lifts">Отрывы основного следа по порядку.</param>
/// <param name="LetterResults">Результат оценки каждой буквы на её участке (без отложенных штрихов; контекст <see cref="LetterContext.WordFirst"/> или <see cref="LetterContext.WordInner"/>).</param>
/// <param name="AlignmentCost">Стоимость выравнивания на точку рукописи (нормировка гипотез, §6.4.6).</param>
/// <param name="LiftsInsideLetter">Число неожиданных отрывов внутри тел букв (не стоп-ошибка).</param>
public sealed record WordDetail(
    ImmutableArray<WordBoundary> Boundaries,
    ImmutableArray<JoinMetrics> Joins,
    ImmutableArray<WordLift> Lifts,
    ImmutableArray<EvaluationResult> LetterResults,
    double AlignmentCost,
    int LiftsInsideLetter);

/// <summary>
/// Детализация результата (v0-provisional, ARCHITECTURE.md §6.2): сопоставления штрихов, подбаллы формы, нормализация и
/// диагностика. Изменяется свободно до решения T0.8; потребители, кроме UI и InkLab, на неё не опираются.
/// </summary>
/// <param name="Strokes">Сопоставленные основные штрихи в порядке эталона.</param>
/// <param name="Deferred">Сопоставления слотов отложенных штрихов в порядке глифа.</param>
/// <param name="FormComponents">Подбаллы формы и веса в порядке <c>form.weights</c>.</param>
/// <param name="Normalization">Параметры нормализации.</param>
/// <param name="Diagnostics">Прочие числа (отсортированы по имени, порядок не зависит от платформы).</param>
public sealed record EvaluationDetail(
    ImmutableArray<StrokeMatch> Strokes,
    ImmutableArray<DeferredMatch> Deferred,
    ImmutableArray<FormComponent> FormComponents,
    NormalizationInfo Normalization,
    ImmutableSortedDictionary<string, double> Diagnostics)
{
    /// <summary>Детализация слова (границы, соединения, отрывы, буквы) или <c>null</c> для изолированной буквы.</summary>
    public WordDetail? Word { get; init; }
}
