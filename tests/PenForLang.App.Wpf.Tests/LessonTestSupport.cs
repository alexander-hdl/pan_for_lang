using System.Collections.Immutable;
using System.IO;
using PenForLang.App.Wpf.Lesson;
using PenForLang.Core;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Testing.SynthInk;

namespace PenForLang.App.Wpf.Tests;

/// <summary>Общие данные тестов урока: контент репозитория, линейки, синтетический ввод и воспроизведение через <see cref="ReplayPenSource"/>.</summary>
internal static class LessonTestSupport
{
    private static readonly Lazy<LessonContent> LazyContent = new(LoadContent);

    /// <summary>Контент урока из каталога <c>content/</c> репозитория (через <see cref="DirectoryContentSource"/>).</summary>
    public static LessonContent Content => LazyContent.Value;

    /// <summary>Каталог <c>content/</c> репозитория (в нём конфигурация, строки и стиль).</summary>
    public static string ContentRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PenForLang.sln")))
                {
                    return Path.Combine(dir.FullName, "content");
                }
            }

            throw new InvalidOperationException("PenForLang.sln not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Тот же холст и раскладка, что у экрана пера: 1920×900 px, 7,5 px/мм, x-height 15 мм.</summary>
    public static (GuideFrame Demo, GuideFrame Writing) Frames()
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 1920, 900), 7.5, 1.0);
        return SandboxLayout.Frames(mapper, Content.Style, SandboxLayout.DefaultXHeightMm);
    }

    /// <summary>Урок на воспроизведении: оценка синхронно, раскладка задана.</summary>
    public static LessonViewModel NewLesson(string letter = "a", string language = "ru", LessonRuntime? runtime = null)
    {
        var vm = new LessonViewModel(Content, runtime ?? LessonRuntime.Inline(), language, letter);
        (GuideFrame demo, GuideFrame writing) = Frames();
        vm.SetFrames(demo, writing);
        return vm;
    }

    /// <summary>Генератор без случайных вариаций «детской руки»: попытка лежит на эталоне (кроме внесённой мутации).</summary>
    public static SynthInkGenerator Synth { get; } = new(
        Content.Style,
        new SynthOptions
        {
            ScaleMin = 1.0,
            ScaleMax = 1.0,
            SlantRangeDeg = 0,
            RotationRangeDeg = 0,
            OffsetU = 0,
            BaselineDrift = 0,
            StrokeOffsetSigma = 0,
            WobbleAmplitude = 0,
            TremorSigma = 0,
            SpeedMin = 1.0,
            SpeedMax = 1.0,
            TimeJitterUs = 0,
        });

    /// <summary>Идеальная попытка: траектория эталона.</summary>
    public static RulerInk Ideal(string letter) => Synth.Ideal(Content.GetGlyph(letter));

    /// <summary>Попытка с внесённой мутацией, сдвинутая на эталон (генератор ставит букву на u = 1: сдвиг убирает его).</summary>
    public static RulerInk Mutated(string letter, SynthMutation mutation, ulong seed = 3)
    {
        SynthSample sample = Synth.Mutate(Content.GetGlyph(letter), mutation, seed)
            ?? throw new InvalidOperationException($"Mutation {mutation} is not applicable to '{letter}'.");
        return sample.Mutation == SynthMutation.Scribble ? sample.Ink : Shift(sample.Ink, -1.0, 0.0);
    }

    /// <summary>Лог пера (точки в px поверхности) из попытки в координатах линейки <paramref name="frame"/>.</summary>
    public static PenLog ToPenLog(RulerInk ink, GuideFrame frame)
    {
        var entries = ImmutableArray.CreateBuilder<PenLogEntry>();
        foreach (RulerStroke stroke in ink.Strokes)
        {
            for (int i = 0; i < stroke.Points.Length; i++)
            {
                RulerPoint p = stroke.Points[i];
                (double x, double y) = RulerProjection.ToSurface(frame, p.U, p.V);
                PenPhase phase = i == 0 ? PenPhase.Down : (i == stroke.Points.Length - 1 ? PenPhase.Up : PenPhase.Move);
                entries.Add(new PenLogSampleEntry(new PenSample(p.TimeUs, x, y, p.Pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 1)));
            }
        }

        var header = new PenLogHeader(
            "test",
            "10.0.19045",
            new PenDeviceInfo("Replay", PenSourceKind.Replay, 200, null, 1024, false, PenTimestampKind.Qpc),
            new PenScreenInfo(1920, 1080, 256, 144, 1.0, 7.5, PxPerMmSource.Default),
            frame,
            new PenLogTask("lesson-001", "E03", "glyph:a", "L4", "trace", "right"),
            new PenLogSubject("test", null, PenSubjectKind.Adult),
            null,
            10_000_000);
        return new PenLog(header, entries.ToImmutable());
    }

    /// <summary>Воспроизводит попытку в урок через <see cref="ReplayPenSource"/> на виртуальных часах (без ожидания).</summary>
    public static async Task WriteAsync(LessonViewModel vm, RulerInk ink)
    {
        GuideFrame frame = vm.WritingFrame ?? throw new InvalidOperationException("Frames are not set.");
        PenLog log = ToPenLog(ink, frame);
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        source.SamplesReceived += batch => vm.OnSamples(batch.Span);
        await source.StartAsync(CancellationToken.None);
        await source.RunToEndAsync(
            (wait, _) =>
            {
                clock.Advance(wait);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>Одна попытка до подсказки: письмо, «Готово», результат.</summary>
    public static async Task<FeedbackPlan> AttemptAsync(LessonViewModel vm, RulerInk ink)
    {
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        await WriteAsync(vm, ink);
        await vm.DoneAsync();
        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        return vm.Plan!;
    }

    /// <summary>Сдвиг попытки по u и v (линейка).</summary>
    public static RulerInk Shift(RulerInk ink, double du, double dv) =>
        ink with
        {
            Strokes =
            [
                .. ink.Strokes.Select(s => s with
                {
                    Points = [.. s.Points.Select(p => p with { U = p.U + du, V = p.V + dv })],
                }),
            ],
        };

    /// <summary>Сводка оценки с заданными находками (для таблиц планировщика, без оценщика).</summary>
    public static EvaluationSummary Summary(
        IEnumerable<Finding> findings,
        Decision decision = Decision.Target,
        Confidence confidence = Confidence.High,
        double form = 0.9,
        bool processOk = true,
        IEnumerable<DeferredSummary>? deferred = null) =>
        new(
            new EvaluatorInfo("test", "test"),
            TargetKind.Letter,
            "a",
            decision,
            "a",
            confidence,
            [new LetterSummary(0, "a", "a", LetterContext.Isolated, null, form, processOk, true, new ProcessSummary(true, true, true, 0, true))],
            [.. deferred ?? []],
            [.. findings]);

    /// <summary>Находка с участком эталона по умолчанию.</summary>
    public static Finding Find(
        ErrorCode code,
        FindingKind kind = FindingKind.Error,
        double severity = 0.5,
        string? strokeId = "s1",
        TemplatePart part = TemplatePart.Body,
        double from = 0.0,
        double to = 1.0) =>
        new(code, new FindingLocation(FindingPlace.Stroke, 0, strokeId, null), severity, new TemplateSpan(strokeId, part, from, to), kind);

    /// <summary>Контекст планировщика для таблиц.</summary>
    public static FeedbackContext Context(
        HintHistory? history = null,
        int attemptNo = 1,
        ElementStatus status = ElementStatus.Learning,
        string step = "E03") =>
        new("a", step, SupportLevel.L4, status, attemptNo, history ?? HintHistory.Empty, Content.Config.Feedback);

    private static LessonContent LoadContent() => LessonContent.Load(new DirectoryContentSource(ContentRoot));
}
