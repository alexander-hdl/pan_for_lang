using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Evaluation.Config;

/// <summary>
/// Кусочно-линейная функция подбалла (ARCHITECTURE.md §6.4.13): точки <c>(x, y)</c> с возрастающим <c>x</c>;
/// слева от первой и справа от последней точки значение постоянно. Мёртвая зона — горизонтальный участок в начале.
/// </summary>
/// <param name="Points">Точки (x — значение метрики, y — подбалл в [0, 1]).</param>
/// <param name="ToleranceScaled">Ось x масштабируется допуском профиля (метрики-ошибки в x-height): <c>y = f(x / scale)</c>.</param>
public sealed record ScoreFunction(ImmutableArray<Vec2> Points, bool ToleranceScaled)
{
    /// <summary>Значение функции в точке <paramref name="x"/> при множителе допуска <paramref name="toleranceScale"/> (&gt; 0).</summary>
    /// <param name="x">Значение метрики.</param>
    /// <param name="toleranceScale">Множитель допуска профиля; для функций без масштабирования не используется.</param>
    public double Evaluate(double x, double toleranceScale)
    {
        double v = ToleranceScaled ? x / toleranceScale : x;
        ImmutableArray<Vec2> p = Points;
        if (v <= p[0].X)
        {
            return p[0].Y;
        }

        for (int i = 1; i < p.Length; i++)
        {
            if (v <= p[i].X)
            {
                double span = p[i].X - p[i - 1].X;
                return p[i - 1].Y + ((p[i].Y - p[i - 1].Y) * ((v - p[i - 1].X) / span));
            }
        }

        return p[^1].Y;
    }
}

/// <summary>Сглаживание Савицкого–Голея (окно и порядок).</summary>
/// <param name="Window">Нечётное окно в точках ресэмплированного штриха.</param>
/// <param name="Order">Порядок многочлена.</param>
public sealed record SmoothingConfig(int Window, int Order);

/// <summary>Предобработка (§6.4.1).</summary>
/// <param name="SpikeJumpMedianSteps">Точка-выброс: оба шага к ней длиннее стольких медианных шагов, а сосед-к-соседу близко.</param>
/// <param name="DotMaxLength">Микроштрих: длина меньше (x-height) ...</param>
/// <param name="DotMaxDurationMs">... и длительность меньше (мс) — <c>dotCandidate</c>.</param>
/// <param name="MinPointDistance">Прореживание: точки ближе этого к последней оставленной отбрасываются (подавляет дрожание на уровне отсчётов).</param>
/// <param name="SmoothHalfWindowMs">Полуширина скользящего среднего по времени до прореживания, мс (0 — без сглаживания); концы штриха не сдвигаются. Подавляет дрожание отсчётов у медленного письма.</param>
/// <param name="MaxResamplePoints">Наибольшее число точек штриха после ресэмплинга (длинные штрихи ресэмплируются реже).</param>
/// <param name="StepScaleMax">Шаг ресэмплинга растёт пропорционально размеру буквы (оценка по межквартильному размаху v против эталона), но не более чем в столько раз: крупная буква не даёт больше точек, чем обычная.</param>
public sealed record PreprocessConfig(
    double SpikeJumpMedianSteps,
    double DotMaxLength,
    double DotMaxDurationMs,
    double MinPointDistance,
    double SmoothHalfWindowMs,
    int MaxResamplePoints,
    double StepScaleMax);

/// <summary>Деслантинг рукописи к наклону эталона.</summary>
/// <param name="Enabled">Включён.</param>
/// <param name="DeadbandDeg">Разница наклонов меньше этого не исправляется (оценка наклона шумит).</param>
/// <param name="MaxDeg">Наибольшая поправка, градусы.</param>
/// <param name="MinRunLength">Минимальная длина участка для оценки наклона, x-height.</param>
/// <param name="MaxDeviationDeg">Допустимое отклонение хорды от вертикали при оценке, градусы.</param>
/// <param name="MaxSpreadDeg">Если углы участков расходятся больше (стандартное отклонение, градусы), наклон не определён (x, K) и деслантинг не применяется.</param>
public sealed record DeslantConfig(bool Enabled, double DeadbandDeg, double MaxDeg, double MinRunLength, double MaxDeviationDeg, double MaxSpreadDeg);

/// <summary>Масштаб рукописи для выравнивания (по робастной высоте, не по bounding box).</summary>
/// <param name="Enabled">Включён.</param>
/// <param name="Min">Наименьший масштаб.</param>
/// <param name="Max">Наибольший масштаб.</param>
/// <param name="LowPercentile">Нижний процентиль v, (0, 0,5).</param>
/// <param name="HighPercentile">Верхний процентиль v, (0,5, 1).</param>
public sealed record ScaleConfig(bool Enabled, double Min, double Max, double LowPercentile, double HighPercentile);

/// <summary>Нормализация для выравнивания (§6.4.2, проход 1).</summary>
/// <param name="DominantStrokeShare">Штрихи длиннее этой доли самого длинного определяют наклон, масштаб и сдвиг.</param>
/// <param name="Deslant">Деслантинг.</param>
/// <param name="Scale">Масштаб.</param>
/// <param name="BaselineShiftMax">Наибольший сдвиг baseline, оцениваемый в проходе 1 (по нижнему процентилю v против эталона), x-height.</param>
/// <param name="Refine">Уточнение масштаба и сдвига по соответствиям выравнивания.</param>
public sealed record NormalizationConfig(
    double DominantStrokeShare,
    DeslantConfig Deslant,
    ScaleConfig Scale,
    double BaselineShiftMax,
    RefineConfig Refine);

/// <summary>
/// Уточнение нормализации после первого выравнивания (робастный метод наименьших квадратов для масштаба и сдвига; §6.4.2).
/// </summary>
/// <param name="Enabled">Включено.</param>
/// <param name="Iterations">Число итераций отсечения худших соответствий.</param>
/// <param name="KeepShare">Доля лучших соответствий, участвующих в подгонке, (0, 1].</param>
/// <param name="ScaleMin">Наименьшая поправка масштаба.</param>
/// <param name="ScaleMax">Наибольшая поправка масштаба.</param>
/// <param name="MaxShiftV">Наибольшая поправка сдвига по v (baseline), x-height.</param>
/// <param name="MinChange">Поправка меньше этого (по масштабу и сдвигу) не применяется, и выравнивание не повторяется.</param>
/// <param name="Rounds">Наибольшее число раундов «выравнивание — подгонка».</param>
/// <param name="RerunAbove">Если поправка раунда больше этого, выравнивание повторяется ещё раз (большой сдвиг делает первые соответствия неточными).</param>
public sealed record RefineConfig(
    bool Enabled,
    int Iterations,
    double KeepShare,
    double ScaleMin,
    double ScaleMax,
    double MaxShiftV,
    double MinChange,
    int Rounds,
    double RerunAbove);

/// <summary>Веса локальной стоимости DTW (§6.4.4): <c>wPos·‖p−q‖² + wTheta·(1 − cos Δθ)</c>.</summary>
/// <param name="PositionWeight">Вес позиции.</param>
/// <param name="DirectionWeight">Вес направления.</param>
public sealed record DtwConfig(double PositionWeight, double DirectionWeight);

/// <summary>Сопоставление штрихов Венгерским алгоритмом с неназначением (§6.4.10).</summary>
/// <param name="ExtraStrokeCost">Цена неназначенного штриха рукописи.</param>
/// <param name="MissingStrokeCost">Цена неназначенного штриха эталона. Пара назначается, только если её стоимость меньше суммы обеих цен.</param>
/// <param name="MinLengthRatio">Штрих короче этой доли длины штриха эталона с ним не сопоставляется.</param>
/// <param name="MaxLengthRatio">Штрих длиннее этой доли — тоже.</param>
public sealed record MatchingConfig(double ExtraStrokeCost, double MissingStrokeCost, double MinLengthRatio, double MaxLengthRatio);

/// <summary>Параметры профиля допусков.</summary>
/// <param name="ToleranceScale">Множитель допусков формы (больше 1 — мягче).</param>
public sealed record ProfileConfig(double ToleranceScale);

/// <summary>Проверка направления (P2).</summary>
/// <param name="ReverseCostRatioMax">P2, если стоимость обращённого эталона не больше этой доли прямой.</param>
/// <param name="StrongReverseCostRatio">При отношении не больше этого обратное направление очевидно, и угол первого участка не проверяется.</param>
/// <param name="FirstSegmentAngleMaxDeg">... и первый участок рукописи отклонён от допустимого направления старта больше (градусы).</param>
/// <param name="FirstSegmentLength">Длина первого участка, x-height.</param>
/// <param name="MinStrokeLength">Штрих короче этого направление не проверяется (точка, короткая черта), x-height.</param>
/// <param name="BarMinDirectionalShare">Отложенная перекладина <c>ltr</c> написана справа налево, если чистый сдвиг назад больше этой доли длины.</param>
/// <param name="BarReverseIsError">Перекладина справа налево — ошибка P2 (учитывается в направлении и <c>process_ok</c>); иначе только информационная находка P2.</param>
/// <param name="RangeAreaMinShare">Направление обхода овала или петли (checks.closed, checks.loops) судится по знаку ориентированной площади участка, если площадь рукописи не меньше этой доли эталонной.</param>
public sealed record DirectionConfig(
    double ReverseCostRatioMax,
    double StrongReverseCostRatio,
    double FirstSegmentAngleMaxDeg,
    double FirstSegmentLength,
    double MinStrokeLength,
    double BarMinDirectionalShare,
    bool BarReverseIsError,
    double RangeAreaMinShare);

/// <summary>Число основных штрихов (P4).</summary>
/// <param name="ExtraForP4">Лишних штрихов для находки P4.</param>
/// <param name="ExtraForStop">Лишних штрихов для стоп-ошибки (решает Learner; здесь определяет тяжесть).</param>
public sealed record StrokeCountConfig(int ExtraForP4, int ExtraForStop);

/// <summary>Проверка ретрейса (P6).</summary>
/// <param name="Tolerance">Расстояние до написанного, в пределах которого точка считается лежащей по написанному, x-height.</param>
/// <param name="MinShareOfReference">Доля ретрейса по написанному должна быть не меньше этой доли от эталонной.</param>
/// <param name="MinPriorPoints">Если до ретрейса написано меньше точек, проверка неприменима (написано нечего повторять).</param>
/// <param name="MinRangePoints">Если участок ретрейса отображается меньше чем в это число точек рукописи, ретрейса нет (доля 0, P6).</param>
/// <param name="CoveredByExtraShare">Если участок ретрейса не менее чем на эту долю лежит под лишним штрихом (P4: буква кусками), P6 не выдаётся — ретрейс написан другим штрихом.</param>
public sealed record RetraceConfig(double Tolerance, double MinShareOfReference, int MinPriorPoints, int MinRangePoints, double CoveredByExtraShare);

/// <summary>Процессные пороги.</summary>
/// <param name="StartTolerance">Допуск старта (P1), x-height.</param>
/// <param name="StartArcTolerance">Допуск места, где DTW с открытым началом «начинает» рукопись на эталоне: дальше этого вдоль тела от его начала (длина дуги, x-height; 0,5 — около ±50° по овалу) — старт с середины буквы (P1).</param>
/// <param name="StartOnPathTolerance">Старт, лежащий на самом эталоне не дальше этого от выровненной точки и в пределах <c>StartArcTolerance</c> вдоль тела, — малое отклонение от эталонного старта (12 и 3 часа у a), а не P1, x-height.</param>
/// <param name="Approach">Подход к телу буквы как допустимое место старта.</param>
/// <param name="Direction">Направление.</param>
/// <param name="StrokeCount">Число штрихов.</param>
/// <param name="Retrace">Ретрейс.</param>
public sealed record ProcessConfig(double StartTolerance, double StartArcTolerance, double StartOnPathTolerance, ApproachConfig Approach, DirectionConfig Direction, StrokeCountConfig StrokeCount, RetraceConfig Retrace);

/// <summary>
/// Допустимый старт изолированной буквы при <c>animation.startDot = leadIn</c>: вся ломаная входящего штриха плюс её продолжение
/// влево вдоль baseline (подход бывает длиннее или короче эталонного) — при ходе вперёд; либо начало тела.
/// </summary>
/// <param name="ExtendLeft">Продолжать подход влево от его начала лучом вдоль линии v начала.</param>
/// <param name="ExtendRight">Допустимый старт вдоль той же линии и вправо от начала подхода на столько, x-height (короткий подход, начатый ближе к телу).</param>
/// <param name="Tolerance">Допуск расстояния до подхода, x-height; жёстче допуска старта тела, потому что подход тянется вдоль овала и старт снизу овала близок к нему (ошибка выражается в единицах <c>startTolerance</c>).</param>
/// <param name="MaxAngleDeg">Старт на подходе засчитывается, если первый участок рукописи отклонён от хода подхода не больше (градусы).</param>
/// <param name="MinElevationDeg">... и начало не идёт вниз: угол подъёма (от горизонтали) на участке <c>elevationLength</c> не меньше этого, градусы (отрицательный — допустим небольшой спуск).</param>
/// <param name="ElevationLength">Длина участка начала для проверки подъёма, x-height. Длиннее участка направления (<c>process.direction.firstSegmentLength</c>): дрожь рукописи у медленного письма отклоняет первые 0,2 x-height вниз и без этого давала ложный P1 (старт «не на подходе»).</param>
/// <param name="OverlapMax">Старт на подходе не засчитывается, если путь до точки входа в тело перекрыт последующим следом (доля точек в коридоре) не меньше этого ...</param>
/// <param name="OverlapTolerance">Ширина коридора перекрытия, x-height.</param>
/// <param name="OverlapSkipTail">Последние столько (x-height) пути до входа не учитываются: они прилегают к телу по построению.</param>
/// <param name="EntryAngleMaxDeg">... и направление прихода в точку входа отличается от <c>entry.tangent</c> эталона больше этого (градусы). Верный подход: перекрытие небольшое ИЛИ направление близко.</param>
/// <param name="AmbiguousShare">Запас по доле: P1 только при перекрытии больше <c>OverlapMax</c> плюс запас (у порога P1 не ставится).</param>
/// <param name="AmbiguousAngleDeg">Запас по углу, градусы.</param>
public sealed record ApproachConfig(
    bool ExtendLeft,
    double ExtendRight,
    double Tolerance,
    double MaxAngleDeg,
    double MinElevationDeg,
    double ElevationLength,
    double OverlapMax,
    double OverlapTolerance,
    double OverlapSkipTail,
    double EntryAngleMaxDeg,
    double AmbiguousShare,
    double AmbiguousAngleDeg);

/// <summary>Единицы штрафа отложенных штрихов в подбалле <c>deferred</c> (вход функции <c>form.scores.deferred</c>).</summary>
/// <param name="Missing">Слот пуст (D1).</param>
/// <param name="Misplaced">Штрих не на месте (D2).</param>
/// <param name="Extra">Лишняя черта (D3).</param>
public sealed record DeferredPenaltyConfig(double Missing, double Misplaced, double Extra);

/// <summary>Форма (§6.4.9, §6.4.13).</summary>
/// <param name="CorridorTau">Полуширина коридора для <c>corridorShare</c> и <c>coverage</c>, x-height.</param>
/// <param name="MinLoopArea">Наименьшая площадь петли, засчитываемой как петля (F3), x-height².</param>
/// <param name="DeferredPenalty">Единицы штрафа метрики <c>deferred</c>.</param>
/// <param name="ClosureWindow">Полуокно измерения замкнутости овала (участок вокруг старта и вокруг возврата), x-height.</param>
/// <param name="BaselineTouchWindow">Полуокно поиска нижней точки у касаний baseline (keypoints <c>baselineTouch</c>), x-height.</param>
/// <param name="BaselineTouchMaxMismatch">Касание судится, только если соответствующая точка рукописи (нормализованная) лежит не дальше этого от точки касания эталона, x-height: иначе нужной части буквы нет (она в другом штрихе).</param>
/// <param name="Weights">Веса метрик в <c>form</c> (ненормированные); порядок ключей — порядок в файле.</param>
/// <param name="Scores">Функции подбаллов по ключам метрик.</param>
public sealed record FormConfig(
    double CorridorTau,
    double MinLoopArea,
    double ClosureWindow,
    double BaselineTouchWindow,
    double BaselineTouchMaxMismatch,
    DeferredPenaltyConfig DeferredPenalty,
    ImmutableArray<KeyValuePair<string, double>> Weights,
    ImmutableDictionary<string, ScoreFunction> Scores);

/// <summary>Отложенные штрихи (§6.4.8).</summary>
/// <param name="AcceptTiming">Какое время принимается.</param>
/// <param name="NearU">Допуск положения отложенного штриха по горизонтали от ожидаемого (по реальному стволу), x-height; допуск эллиптический вместе с <paramref name="NearV"/>.</param>
/// <param name="NearV">То же по вертикали вниз (точка над i и перекладина чувствительны к высоте).</param>
/// <param name="NearVUpDot">Допуск вверх для точки: дети ставят точку выше ожидаемого, не ниже.</param>
/// <param name="BarMustCrossStem">Перекладина, не пересекающая ствол, — D2.</param>
/// <param name="BarCrossTolerance">Расстояние от перекладины до ствола, до которого она считается пересекающей, x-height.</param>
/// <param name="AssignMaxDistance">Дальше этого штрих не считается этим слотом вообще (D1 и лишний штрих).</param>
/// <param name="DotMaxLength">Штрих короче этого считается точкой.</param>
/// <param name="BarMinLengthRatio">Штрих короче этой доли эталонной перекладины в слот не подходит.</param>
/// <param name="BarMaxLengthRatio">Длиннее этой доли — тоже.</param>
/// <param name="ExtraMarkMaxLength">Лишний штрих короче этого и ...</param>
/// <param name="ExtraMarkHorizontalShare">... с долей горизонтального смещения не меньше этой — лишняя черта (D3), иначе лишний штрих (P4).</param>
/// <param name="StrictTimingSeverity">Тяжесть D4 в режиме <see cref="DeferredAcceptPolicy.AfterWord"/>.</param>
public sealed record DeferredConfig(
    DeferredAcceptPolicy AcceptTiming,
    double NearU,
    double NearV,
    double NearVUpDot,
    bool BarMustCrossStem,
    double BarCrossTolerance,
    double AssignMaxDistance,
    double DotMaxLength,
    double BarMinLengthRatio,
    double BarMaxLengthRatio,
    double ExtraMarkMaxLength,
    double ExtraMarkHorizontalShare,
    double StrictTimingSeverity);

/// <summary>Пороги находок формы и тяжесть.</summary>
/// <param name="F1DtwMean">F1: среднее отклонение больше (x-height) — общее отклонение формы.</param>
/// <param name="F2ClosedGap">F2: превышение зазора замыкания больше (x-height).</param>
/// <param name="F4ReachError">F4: ошибка достижения линии больше (x-height).</param>
/// <param name="F5BaselineError">F5: ошибка посадки на baseline больше (x-height).</param>
/// <param name="F6SlantDeviationDeg">F6: отклонение наклона от стиля больше (градусы, только режим <c>absolute</c>).</param>
/// <param name="SeverityBase">Тяжесть находки на пороге.</param>
/// <param name="SeverityFullAtMultiple">Во сколько раз превышение порога даёт тяжесть 1.</param>
/// <param name="FixedSeverity">Тяжесть находок без порога (P3, P5, D1, D3, P6, F3).</param>
public sealed record FindingsConfig(
    double F1DtwMean,
    double F2ClosedGap,
    double F4ReachError,
    double F5BaselineError,
    double F6SlantDeviationDeg,
    double SeverityBase,
    double SeverityFullAtMultiple,
    ImmutableDictionary<ErrorCode, double> FixedSeverity);

/// <summary>Пороги уверенности и «не распознано» (§6.4.7, §6.4.13).</summary>
/// <param name="UnrecognizedMeanDtw">Среднее отклонение выровненных штрихов больше (x-height) — U0.</param>
/// <param name="UnrecognizedCoverage">Покрытие эталона меньше при сопоставленных штрихах — U0.</param>
/// <param name="UnrecognizedInkShare">Доля чернил (сопоставленных и лишних основных штрихов) в коридоре эталона меньше — U0 (каракули).</param>
/// <param name="MediumMeanDtw">Среднее отклонение больше — уверенность не выше Medium.</param>
public sealed record ConfidenceConfig(double UnrecognizedMeanDtw, double UnrecognizedCoverage, double UnrecognizedInkShare, double MediumMeanDtw);

/// <summary>Выравнивание рукописи по эталону слова (<c>TargetAligner</c>, §6.4.4).</summary>
/// <param name="BandU">Полуширина полосы по x после деслантинга и нормализации (A09), x-height.</param>
/// <param name="OutOfBandCost">Штраф за пару вне полосы («мягкая» полоса: путь существует всегда).</param>
/// <param name="LiftJoinPenalty">Цена отрыва пера в диапазоне соединения (отрыв — альтернатива геометрии; A03): прибавляется к стоимости пути.</param>
/// <param name="LiftInLetterPenalty">Цена отрыва внутри тела буквы (<c>LiftsInsideLetter</c>, не стоп-ошибка).</param>
/// <param name="SkipBodyCostPerPoint">Цена каждой пропущенной точки тела эталона при отрыве (пропуск соединения бесплатен).</param>
/// <param name="ContinuousBreakCostPerPoint">Цена каждой точки рукописи, прошедшей ожидаемый разрыв слитно (break — «отрыв или слитно»).</param>
/// <param name="JoinSlackU">Допуск у границ тел, x-height: отрыв, начатый в пределах допуска перед концом тела эталона или закончившийся в пределах допуска после начала тела следующей буквы, — отрыв на соединении (размер букв ребёнка и эталона различается). Цена отрыва растёт от <c>liftJoinPenalty</c> на границе до <c>liftInLetterPenalty</c> на краю допуска (промежуточный штраф): отрыв глубже в теле буквы — отрыв внутри буквы.</param>
/// <param name="BandMarginU">Запас полосы, x-height: DP считает только клетки с <c>|Δu| ≤ bandU + bandMarginU</c> (вне — недопустимы; если пути нет, расчёт повторяется без ограничения).</param>
/// <param name="BreakCorridorU">Ширина коридора вокруг мостика эталона (разрыв стиля), в котором слитное прохождение стоит плоскую цену <c>continuousBreakCostPerPoint</c>; дальше — обычная стоимость расстояния (предел пробега: слитная линия не может уйти далеко от места разрыва).</param>
public sealed record AlignerConfig(
    double BandU,
    double OutOfBandCost,
    double LiftJoinPenalty,
    double LiftInLetterPenalty,
    double SkipBodyCostPerPoint,
    double ContinuousBreakCostPerPoint,
    double JoinSlackU,
    double BandMarginU,
    double BreakCorridorU);

/// <summary>Границы букв и их уверенность (§6.4.5).</summary>
/// <param name="ShiftU">Сдвиг границы, при котором измеряется рост стоимости пути, x-height (0,3).</param>
/// <param name="MinRiseCost">Наименьший рост полной стоимости пути при сдвиге границы на <paramref name="ShiftU"/> в каждую сторону; меньше — граница неуверенная (пологий минимум).</param>
/// <param name="LiftSnapU">Если перо оторвано не дальше этого (x-height) за границей тела, граница — край штриха: тело кончается там, где оторвано перо (на гладком продолжении тела соединением положение границы по форме неопределённо).</param>
public sealed record BoundaryConfig(double ShiftU, double MinRiseCost, double LiftSnapU);

/// <summary>«Не распознано» для слова (§6.4.7).</summary>
/// <param name="MeanDtw">Среднее отклонение выровненных точек больше (x-height) — U0.</param>
/// <param name="Coverage">Доля тел эталона, покрытых рукописью в коридоре, меньше — U0.</param>
/// <param name="InkShare">Доля точек основного следа в коридоре эталона меньше — U0 (каракули).</param>
/// <param name="MediumMeanDtw">Среднее отклонение больше — уверенность не выше Medium.</param>
/// <param name="CostPerPoint">Стоимость выравнивания на точку рукописи (нормировка гипотез, §6.4.6) больше — U0. Инженерное: верные слова ≤ 0,067, неверные от 0,074 до 0,8 (независимые фикстуры); порог с запасом между ними.</param>
/// <param name="FarDistance">Точка рукописи дальше этого от выровненной точки эталона — «чужая» (лишняя буква, штрих мимо слова), x-height.</param>
/// <param name="FarShare">Доля чужих точек больше — U0 (лишняя буква: каждая точка далека от своей пары, а стоимость на точку мала).</param>
/// <param name="LetterCoverage">Покрытие тела какой-либо буквы рукописью в коридоре меньше — U0 слова (буква без чернил, слово другой длины).</param>
/// <param name="CoverageTauScale">Множитель <c>form.corridorTau</c> для покрытия и доли чернил в коридоре у слова (допуск шире, чем у изолированной буквы).</param>
public sealed record WordUnrecognizedConfig(double MeanDtw, double Coverage, double InkShare, double MediumMeanDtw, double CostPerPoint, double FarDistance, double FarShare, double LetterCoverage, double CoverageTauScale);

/// <summary>Числа оценки слова (ключ <c>word</c> в <c>scoring.json</c>).</summary>
/// <param name="ContextToleranceScale">Множитель допусков формы буквы внутри слова (<c>wordContextTolerance</c>, §6.4.9).</param>
/// <param name="Aligner">Выравнивание.</param>
/// <param name="Boundaries">Границы.</param>
/// <param name="Unrecognized">«Не распознано».</param>
/// <param name="MarkAdjacencyU">Короткая черта, начатая не дальше этого от конца предыдущего основного штриха, — продолжение основного следа (хвост t после перекладины «сразу»), а не отложенный штрих, x-height.</param>
/// <param name="RetraceCoveredShare">Ретрейс по соединению проверяется, только если нужное соединение нарисовано: эталонное окно перед ретрейсом покрыто путём не меньше этой доли.</param>
public sealed record WordConfig(
    double ContextToleranceScale,
    AlignerConfig Aligner,
    BoundaryConfig Boundaries,
    WordUnrecognizedConfig Unrecognized,
    double MarkAdjacencyU,
    double RetraceCoveredShare);

/// <summary>Углы и петли в соединении (J4, §6.4.9).</summary>
/// <param name="ExcessDeg">Угол поворота внутри соединения рукописи больше эталонного на это (градусы) — лишний угол.</param>
/// <param name="WindowU">Окно измерения поворота по длине дуги, x-height.</param>
/// <param name="EdgeMarginU">Концы соединения (стык с телами букв, где углы допустимы) не проверяются на этом отрезке, x-height.</param>
public sealed record JoinCornerConfig(double ExcessDeg, double WindowU, double EdgeMarginU);

/// <summary>Метрики соединений (J1–J4, §6.4.9; ключ <c>joins</c> в <c>scoring.json</c>).</summary>
/// <param name="HighMinHeight">Наименьшая высота v высокого соединения (флаг <c>high</c> правила стиля; единственное место числа, A25), x-height; J2.</param>
/// <param name="DrawnShare">Соединение нарисовано, если рукопись прошла не меньше этой доли точек его эталона.</param>
/// <param name="EntryErr">Вход в тело следующей буквы дальше от эталонного на столько (x-height) — J3. Мерится относительно буквы ребёнка: после подгонки масштаба и сдвига тела буквы к эталону, а не в общей рамке слова (широкий интервал — F8, низкая t — F4, не J3).</param>
/// <param name="EntryAngleDeg">Соединение приходит в начало тела под углом, отличающимся от эталонного входа больше этого (градусы) — J3; проверяется, только если соединение нарисовано.</param>
/// <param name="Corner">Лишние углы.</param>
/// <param name="MinLoopArea">Наименьшая площадь петли в соединении, засчитываемой как лишняя (J4), x-height².</param>
public sealed record JoinsConfig(double HighMinHeight, double DrawnShare, double EntryErr, double EntryAngleDeg, JoinCornerConfig Corner, double MinLoopArea);

/// <summary>
/// Числа оценщика изолированной буквы (<c>content/config/scoring.json</c>, схема <c>pfl.config.scoring/1</c>).
/// Неизменяема; хеш идентифицирует снимок (ARCHITECTURE.md §6.6). Все значения в файле — инженерные стартовые, до калибровки
/// на реальных данных (§6.7).
/// </summary>
/// <param name="Version">Версия файла конфигурации.</param>
/// <param name="ConfigHash">SHA-256 канонического JSON без блока <c>meta</c>, hex в нижнем регистре.</param>
/// <param name="ResampleStep">Шаг ресэмплинга по длине дуги, x-height.</param>
/// <param name="Smoothing">Сглаживание.</param>
/// <param name="Preprocess">Предобработка.</param>
/// <param name="Normalization">Нормализация.</param>
/// <param name="Dtw">Веса DTW.</param>
/// <param name="Matching">Сопоставление штрихов.</param>
/// <param name="Profiles">Профили допусков.</param>
/// <param name="Process">Процессные пороги.</param>
/// <param name="Form">Форма.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
/// <param name="Findings">Находки.</param>
/// <param name="Confidence">Уверенность.</param>
/// <param name="Word">Слово: выравнивание, границы букв.</param>
/// <param name="Joins">Соединения.</param>
public sealed record ScoringConfig(
    string Version,
    string ConfigHash,
    double ResampleStep,
    SmoothingConfig Smoothing,
    PreprocessConfig Preprocess,
    NormalizationConfig Normalization,
    DtwConfig Dtw,
    MatchingConfig Matching,
    ImmutableDictionary<ToleranceProfile, ProfileConfig> Profiles,
    ProcessConfig Process,
    FormConfig Form,
    DeferredConfig Deferred,
    FindingsConfig Findings,
    ConfidenceConfig Confidence,
    WordConfig Word,
    JoinsConfig Joins)
{
    /// <summary>Путь к файлу конфигурации в <see cref="PenForLang.Core.IContentSource"/>.</summary>
    public const string DefaultPath = "config/scoring.json";

    /// <summary>Множитель допуска профиля.</summary>
    /// <param name="profile">Профиль.</param>
    public double ToleranceScaleOf(ToleranceProfile profile) => Profiles[profile].ToleranceScale;
}
