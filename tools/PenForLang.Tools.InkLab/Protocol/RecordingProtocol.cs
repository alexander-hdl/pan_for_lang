using System.Collections.Immutable;
using System.Globalization;

namespace PenForLang.Tools.InkLab.Protocol;

/// <summary>Варианты написания слова в протоколе записи (ARCHITECTURE.md §12.2, V0.9).</summary>
public static class PromptVariants
{
    /// <summary>Слово с отрывом пера на соединении: перо поднимают между буквами, соединительного штриха нет.</summary>
    public const string Lift = "lift";

    /// <summary>Слово слитно, перекладину t ставят сразу после ствола.</summary>
    public const string Crossbar = "crossbar";
}

/// <summary>Одна подсказка протокола: что именно написать (одна запись penlog = одна подсказка).</summary>
/// <param name="Id">Стабильный идентификатор в пределах протокола: <c>a-007</c>, <c>at-lift-003</c>.</param>
/// <param name="Target">Цель в формате penlog (<c>task.target</c>): <c>glyph:a</c> или <c>word:at</c>.</param>
/// <param name="Text">Что надо написать: <c>a</c>, <c>at</c>.</param>
/// <param name="Variant">Вариант написания (<see cref="PromptVariants"/>) или <c>null</c>.</param>
/// <param name="Number">Номер повтора внутри группы, с 1.</param>
/// <param name="Total">Сколько повторов в группе.</param>
/// <param name="Instruction">Краткое указание взрослому (по-русски).</param>
public sealed record RecordingPrompt(string Id, string Target, string Text, string? Variant, int Number, int Total, string Instruction)
{
    /// <summary>Подсказка про слово (а не про отдельную букву).</summary>
    public bool IsWord => Target.StartsWith("word:", StringComparison.Ordinal);

    /// <summary>Ключ стимула для события <c>stimulus-start</c> (<c>glyph/a</c>, <c>word/at</c>).</summary>
    public string StimulusKey => Target.Replace(':', '/');

    /// <summary>Группа повторов: подсказки с одинаковой целью и вариантом (для сводки и заголовка).</summary>
    public string Group => Variant is null ? Target : Target + "#" + Variant;
}

/// <summary>Шаг описания протокола: сколько раз писать цель.</summary>
/// <param name="Target">Цель (<c>glyph:a</c>, <c>word:at</c>).</param>
/// <param name="Count">Сколько раз.</param>
/// <param name="Variants">Варианты написания по кругу (пусто — без варианта).</param>
public sealed record ProtocolStep(string Target, int Count, ImmutableArray<string> Variants = default);

/// <summary>
/// Протокол записи эталонов: упорядоченный список подсказок. Взрослый проходит их по очереди, копируя экранный эталон;
/// каждая подсказка даёт отдельный penlog.
/// </summary>
public sealed class RecordingProtocol
{
    /// <summary>Идентификатор протокола V0.9 по умолчанию (попадает в <c>task.taskId</c> каждого penlog).</summary>
    public const string DefaultId = "inklab-v0.9";

    /// <summary>Сколько раз писать каждую букву в протоколе по умолчанию.</summary>
    public const int DefaultLetterRepeats = 30;

    /// <summary>Сколько раз писать слово в протоколе по умолчанию (половина — «с отрывом», половина — «перекладина сразу»).</summary>
    public const int DefaultWordRepeats = 20;

    private RecordingProtocol(string id, ImmutableArray<RecordingPrompt> prompts)
    {
        Id = id;
        Prompts = prompts;
    }

    /// <summary>Идентификатор протокола.</summary>
    public string Id { get; }

    /// <summary>Подсказки по порядку.</summary>
    public ImmutableArray<RecordingPrompt> Prompts { get; }

    /// <summary>
    /// Протокол V0.9: буквы <c>a</c>, <c>i</c>, <c>t</c> по <paramref name="letterRepeats"/> раз, затем слово <c>at</c>
    /// <paramref name="wordRepeats"/> раз (варианты <see cref="PromptVariants.Lift"/> и <see cref="PromptVariants.Crossbar"/> по очереди).
    /// </summary>
    public static RecordingProtocol CreateDefault(int letterRepeats = DefaultLetterRepeats, int wordRepeats = DefaultWordRepeats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(letterRepeats);
        ArgumentOutOfRangeException.ThrowIfNegative(wordRepeats);
        return Create(
            DefaultId,
            [
                new ProtocolStep("glyph:a", letterRepeats),
                new ProtocolStep("glyph:i", letterRepeats),
                new ProtocolStep("glyph:t", letterRepeats),
                new ProtocolStep("word:at", wordRepeats, [PromptVariants.Lift, PromptVariants.Crossbar]),
            ]);
    }

    /// <summary>Строит протокол из шагов; идентификаторы подсказок уникальны.</summary>
    public static RecordingProtocol Create(string id, IEnumerable<ProtocolStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(steps);
        var prompts = ImmutableArray.CreateBuilder<RecordingPrompt>();
        foreach (ProtocolStep step in steps)
        {
            int separator = step.Target.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == step.Target.Length - 1)
            {
                throw new ArgumentException($"Target '{step.Target}' must look like 'glyph:a' or 'word:at'.", nameof(steps));
            }

            string text = step.Target[(separator + 1)..];
            ImmutableArray<string> variants = step.Variants.IsDefaultOrEmpty ? [] : step.Variants;
            if (variants.Length == 0)
            {
                for (int i = 1; i <= step.Count; i++)
                {
                    prompts.Add(Make(step.Target, text, null, i, step.Count, i));
                }
            }
            else
            {
                // Варианты чередуются; повторы считаются внутри каждого варианта (at-lift-001, at-crossbar-001, at-lift-002 ...).
                var counters = new int[variants.Length];
                int[] totals = new int[variants.Length];
                for (int i = 0; i < step.Count; i++)
                {
                    totals[i % variants.Length]++;
                }

                for (int i = 0; i < step.Count; i++)
                {
                    int v = i % variants.Length;
                    counters[v]++;
                    prompts.Add(Make(step.Target, text, variants[v], counters[v], totals[v], counters[v]));
                }
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RecordingPrompt prompt in prompts)
        {
            if (!ids.Add(prompt.Id))
            {
                throw new ArgumentException($"Duplicate prompt id '{prompt.Id}'.", nameof(steps));
            }
        }

        return new RecordingProtocol(id, prompts.ToImmutable());
    }

    /// <summary>Индекс подсказки по идентификатору или -1.</summary>
    public int IndexOf(string promptId)
    {
        for (int i = 0; i < Prompts.Length; i++)
        {
            if (string.Equals(Prompts[i].Id, promptId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static RecordingPrompt Make(string target, string text, string? variant, int number, int total, int idNumber)
    {
        bool word = target.StartsWith("word:", StringComparison.Ordinal);
        string id = text + (variant is null ? string.Empty : "-" + variant) + "-" + idNumber.ToString("000", CultureInfo.InvariantCulture);
        return new RecordingPrompt(id, target, text, variant, number, total, Describe(word, text, variant));
    }

    private static string Describe(bool word, string text, string? variant)
    {
        if (!word)
        {
            return $"Напишите букву «{text}» так же, как показывает эталон. Пишите в своём обычном темпе, аккуратно.";
        }

        return variant switch
        {
            PromptVariants.Lift =>
                $"Слово «{text}» С ОТРЫВОМ ПЕРА НА СОЕДИНЕНИИ: напишите первую букву, ОТОРВИТЕ перо, затем вторую букву (перекладина t — после неё).",
            PromptVariants.Crossbar =>
                $"Слово «{text}» СЛИТНО, перекладину t ставьте СРАЗУ после ствола, не возвращаясь к ней позже.",
            _ => $"Напишите слово «{text}» так же, как показывает эталон.",
        };
    }
}
