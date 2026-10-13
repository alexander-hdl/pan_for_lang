using System.Diagnostics;
using System.Reflection;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Бюджет производительности: изолированная буква ≤ 5 мс (ARCHITECTURE.md §6.9). Тест с <see cref="Stopwatch"/> допустим
/// только в тестах (в домене часы запрещены). Порог с запасом для CI: 15 мс при оптимизированной сборке (медиана 200 вызовов
/// после прогрева); реальное число пишется в вывод теста. В отладочной сборке JIT не оптимизирует, порог мягче.
/// </summary>
public class PerformanceTests
{
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
        RulerInk ink = Tester.Synth.Clean(glyph, 7);
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
        TestContext.Current.SendDiagnosticMessage($"perf {symbol}: median {median:F2} ms, p95 {p95:F2} ms, max {times[^1]:F2} ms ({(OptimizedBuild ? "optimized" : "debug")} build; budget 5 ms, CI threshold {OptimizedBudgetMs} ms)");

        Assert.True(median <= (OptimizedBuild ? OptimizedBudgetMs : DebugBudgetMs), $"{symbol}: median {median:F2} ms");
    }
}
