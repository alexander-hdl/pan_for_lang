using System.Collections.Immutable;
using PenForLang.Evaluation;
using PenForLang.Ink;

namespace PenForLang.Testing.SynthInk;

/// <summary>Мутации синтетической попытки с разметкой ожидаемой находки (ARCHITECTURE.md §10.3, шаг 3).</summary>
public enum SynthMutation
{
    /// <summary>Без мутации: корректный зашумлённый образец.</summary>
    None,

    /// <summary>Штрих начат в другом месте, затем перо приходит к настоящему старту (P1).</summary>
    WrongStart,

    /// <summary>Штрих написан в обратном направлении (P2).</summary>
    ReversedStroke,

    /// <summary>Лишний штрих основного следа (P4).</summary>
    ExtraStroke,

    /// <summary>Пропущен основной штрих (P5).</summary>
    MissingStroke,

    /// <summary>Основные штрихи написаны в другом порядке (P3); нужно не меньше двух основных штрихов.</summary>
    SwappedStrokeOrder,

    /// <summary>Нет точки или перекладины (D1).</summary>
    MissingDeferred,

    /// <summary>Отложенный штрих поставлен сразу, раньше последнего основного штриха (D4, информационно; не ошибка).</summary>
    DeferredImmediately,

    /// <summary>Отложенный штрих поставлен далеко от ствола (D2).</summary>
    MisplacedDeferred,

    /// <summary>Лишний отложенный штрих — вторая точка или перекладина (D3).</summary>
    ExtraDeferred,

    /// <summary>Ретрейс выполнен параллельной линией, а не по написанному (P6).</summary>
    ParallelRetrace,

    /// <summary>Овал не замкнут: штрих после участка замыкания смещён (F2).</summary>
    OpenOval,

    /// <summary>Каракули, не похожие на букву (U0).</summary>
    Scribble,
}

/// <summary>Находка, которую оценщик обязан выдать на мутированный образец.</summary>
/// <param name="Code">Код.</param>
/// <param name="StrokeId">Штрих эталона (слот), к которому привязана находка, или <c>null</c>, если привязки к штриху эталона нет.</param>
public sealed record ExpectedFinding(ErrorCode Code, string? StrokeId);

/// <summary>Синтетическая попытка с разметкой.</summary>
/// <param name="Ink">Попытка в координатах линейки.</param>
/// <param name="Mutation">Внесённая мутация.</param>
/// <param name="Expected">Ожидаемые находки (все должны присутствовать); для <see cref="SynthMutation.Scribble"/> — пусто, ожидается U0.</param>
/// <param name="Forbidden">Коды, которых быть не должно (например, J1 при D4 или P1 при обратном штрихе, ложные стоп-ошибки).</param>
public sealed record SynthSample(
    RulerInk Ink,
    SynthMutation Mutation,
    ImmutableArray<ExpectedFinding> Expected,
    ImmutableArray<ErrorCode> Forbidden);
