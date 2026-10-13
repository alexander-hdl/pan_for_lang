using System.Diagnostics;
using System.Reflection;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Бюджет производительности: изолированная буква ≤ 5 мс (ARCHITECTURE.md §6.9). Тест с <see cref="Stopwatch"/> допустим
/// только в тестах (в домене часы запрещены). Порог с запасом: 15 мс при оптимизированной сборке (медиана 200 вызовов
/// после прогрева); в отладочной сборке JIT не оптимизирует, порог мягче.
/// <para>
/// Время на общем раннере CI при параллельных тестах плавает на порядок, поэтому эти тесты помечены <c>Category=Perf</c>,
/// не входят в обязательный прогон PR и запускаются отдельным шагом CI (без блокировки) или локально:
/// <c>dotnet test --project tests/PenForLang.Evaluation.Tests --filter-trait "Category=Perf"</c>. Числа пишутся в файл из
/// переменной среды <c>PFL_PERF_REPORT</c> (если задана). В обычном прогоне остаётся грубая проверка
/// <see cref="PerformanceSanityTests"/>.
/// </para>
/// </summary>
[Trait("Category", "Perf")]
[Collection(SerialTests.Name)]
public class PerformanceTests
{
    private static readonly object ReportLock = new();
    private const int Warmup = 100;
    private const int Runs = 200;
    private const double OptimizedBudgetMs = 15.0;
    private const double DebugBudgetMs = 75.0;

    public static TheoryData<string> Letters => ["a", "i", "t"];

    private static bool OptimizedBuild =>
        typeof(LetterEvaluator).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    [Theory]
    [MemberData(nameof(Letters))]
    public void Median_evaluation_time_is_within_budget(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        MeasureMedian(symbol, Tester.Synth.Clean(glyph, 7), "clean");
    }

    /// <summary>
    /// Медленное дрожащее письмо (0,3 x-h/с, дрожь 0,03; до 4300 исходных точек на букву) — худший случай по числу точек: шум
    /// отсчётов раздувал длину пути и число точек ресэмплинга (F-07, F-18). Бюджет тот же.
    /// </summary>
    [Theory]
    [MemberData(nameof(Letters))]
    public void Slow_trembling_handwriting_is_within_the_same_budget(string symbol)
    {
        RulerInk ink = HandFixtures.Hand.SlowTrembling(symbol, 0.3, 0.03);
        Assert.True(ink.Strokes.Sum(s => s.Points.Length) > 1000, "the fixture must have a lot of raw points");
        MeasureMedian(symbol, ink, "slow trembling");
    }

    /// <summary>Крупное письмо (a ×2,5): шаг ресэмплинга растёт с размером буквы, число точек не растёт (R2-07).</summary>
    [Fact]
    public void A_big_letter_is_within_the_same_budget()
    {
        MeasureMedian("a", HandFixtures.HandInkBuilder.Build(HandFixtures.Hand.A(scale: 2.5)), "a x2.5");
    }

    private static void MeasureMedian(string symbol, RulerInk ink, string kind)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        var request = new LetterEvaluationRequest(ink, glyph, Shipped.Style);
        LetterEvaluator evaluator = Shipped.NewEvaluator();

        for (int i = 0; i < Warmup; i++)
        {
            evaluator.Evaluate(request);
        }

        var times = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            evaluator.Evaluate(request);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        double median = times[Runs / 2];
        double p95 = times[(int)(Runs * 0.95)];
        string line = $"perf {symbol} ({kind}): median {median:F2} ms, p95 {p95:F2} ms, max {times[^1]:F2} ms ({(OptimizedBuild ? "optimized" : "debug")} build; budget 5 ms, threshold {OptimizedBudgetMs} ms)";
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("PFL_PERF_REPORT") is { Length: > 0 } report)
        {
            lock (ReportLock)
            {
                File.AppendAllText(report, line + Environment.NewLine);
            }
        }

        Assert.True(median <= (OptimizedBuild ? OptimizedBudgetMs : DebugBudgetMs), $"{symbol} ({kind}): median {median:F2} ms");
    }
}

/// <summary>Коллекция без параллелизма: тесты времени не должны идти одновременно с другими тестами этой сборки.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTests
{
    /// <summary>Имя коллекции.</summary>
    public const string Name = "serial";
}

/// <summary>
/// Грубая проверка скорости в обычном прогоне: ловит катастрофические регрессии (десятки раз), а не шум раннера. Медиана 50 вызовов
/// после прогрева должна быть меньше 100 мс (в Release обычно 1–3 мс). Точные бюджеты — в <see cref="PerformanceTests"/> (Category=Perf).
/// </summary>
[Collection(SerialTests.Name)]
public class PerformanceSanityTests
{
    private const double CatastropheMs = 100.0;

    [Theory]
    [InlineData("a")]
    [InlineData("i")]
    [InlineData("t")]
    public void Evaluation_is_not_catastrophically_slow(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        Assert.True(Median(glyph, Tester.Synth.Clean(glyph, 7)) < CatastropheMs);
    }

    [Fact]
    public void Slow_trembling_and_big_letters_are_not_catastrophically_slow()
    {
        Glyph a = Shipped.Glyph("a");

        Assert.True(Median(a, HandFixtures.Hand.SlowTrembling("a", 0.3, 0.03)) < CatastropheMs, "slow trembling");
        Assert.True(Median(a, HandFixtures.HandInkBuilder.Build(HandFixtures.Hand.A(scale: 2.5))) < CatastropheMs, "a x2.5");
    }

    private static double Median(Glyph glyph, RulerInk ink)
    {
        var request = new LetterEvaluationRequest(ink, glyph, Shipped.Style);
        LetterEvaluator evaluator = Shipped.NewEvaluator();
        for (int i = 0; i < 50; i++)
        {
            evaluator.Evaluate(request);
        }

        var times = new double[50];
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            evaluator.Evaluate(request);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        return times[times.Length / 2];
    }
}

/// <summary>
/// Бюджет слова (V0.10): <c>at</c> и <c>it</c> ≤ 50 мс (цель ≤ 20 мс), слово из восьми букв ≤ 100 мс (ARCHITECTURE.md §6.9).
/// Как у буквы: <c>Category=Perf</c>, вне обязательного прогона PR, коллекция без параллелизма. Эталон слова строится один раз
/// (кеш по экземпляру); медиана 100 вызовов после прогрева.
/// </summary>
[Trait("Category", "Perf")]
[Collection(SerialTests.Name)]
public class WordPerformanceTests
{
    private const int Warmup = 30;
    private const int Runs = 100;
    private const double OptimizedBudgetMs = 50.0;
    private const double LongWordBudgetMs = 100.0;
    private const double DebugFactor = 5.0;
    private static readonly object ReportLock = new();

    public static TheoryData<string> Words => ["at", "it"];

    private static bool OptimizedBuild =>
        typeof(WordEvaluator).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    [Theory]
    [MemberData(nameof(Words))]
    public void A_connected_two_letter_word_is_within_budget(string text)
    {
        RulerInk ink = Tester.Synth.Clean(Tester.Word(text), 7);
        Measure(text, ink, "connected", OptimizedBudgetMs);
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void A_two_letter_word_with_a_lift_on_the_join_is_within_budget(string text)
    {
        RulerInk ink = Tester.Synth.Mutate(Tester.Word(text), PenForLang.Testing.SynthInk.SynthWordMutation.LiftAtJoin, 7)!.Ink;
        Measure(text, ink, "lift on the join", OptimizedBudgetMs);
    }

    [Fact]
    public void A_dot_put_immediately_is_within_budget()
    {
        RulerInk ink = Tester.Synth.Mutate(Tester.Word("it"), PenForLang.Testing.SynthInk.SynthWordMutation.DeferredImmediately, 7)!.Ink;
        Measure("it", ink, "dot immediately", OptimizedBudgetMs);
    }

    [Fact]
    public void An_eight_letter_word_is_within_budget()
    {
        const string text = "taitaiti";
        RulerInk ink = Tester.Synth.Clean(Tester.Word(text), 7);
        Measure(text, ink, "eight letters", LongWordBudgetMs);
    }

    private static void Measure(string text, RulerInk ink, string kind, double budget)
    {
        var request = new WordEvaluationRequest(ink, Tester.Word(text));
        WordEvaluator evaluator = new(Shipped.Config);
        for (int i = 0; i < Warmup; i++)
        {
            evaluator.Evaluate(request);
        }

        var times = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            evaluator.Evaluate(request);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        double median = times[Runs / 2];
        double p95 = times[(int)(Runs * 0.95)];
        string line = $"perf word {text} ({kind}): median {median:F2} ms, p95 {p95:F2} ms, max {times[^1]:F2} ms ({(OptimizedBuild ? "optimized" : "debug")} build; budget {budget:F0} ms, target 20 ms for two letters)";
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("PFL_PERF_REPORT") is { Length: > 0 } report)
        {
            lock (ReportLock)
            {
                File.AppendAllText(report, line + Environment.NewLine);
            }
        }

        Assert.True(median <= (OptimizedBuild ? budget : budget * DebugFactor), $"{text} ({kind}): median {median:F2} ms");
    }
}

/// <summary>
/// Грубая проверка скорости слова в обычном прогоне: ловит катастрофические регрессии (медиана 20 вызовов &lt; 500 мс; в Release
/// обычно несколько мс), а не шум раннера. Точные бюджеты — в <see cref="WordPerformanceTests"/> (Category=Perf).
/// </summary>
[Collection(SerialTests.Name)]
public class WordPerformanceSanityTests
{
    private const double CatastropheMs = 500.0;

    [Theory]
    [InlineData("at")]
    [InlineData("it")]
    [InlineData("taitaiti")]
    public void Word_evaluation_is_not_catastrophically_slow(string text)
    {
        var request = new WordEvaluationRequest(Tester.Synth.Clean(Tester.Word(text), 7), Tester.Word(text));
        WordEvaluator evaluator = new(Shipped.Config);
        for (int i = 0; i < 10; i++)
        {
            evaluator.Evaluate(request);
        }

        var times = new double[20];
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            evaluator.Evaluate(request);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        Assert.True(times[times.Length / 2] < CatastropheMs, $"{text}: median {times[times.Length / 2]:F1} ms");
    }
}
