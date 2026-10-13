using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PenForLang.App.Wpf.Lesson;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Testing.SynthInk;
using PenForLang.Wpf.Controls;
using static PenForLang.App.Wpf.Tests.LessonTestSupport;

namespace PenForLang.App.Wpf.Tests;

/// <summary>
/// Настоящее окно урока (WPF, поток STA) с вводом, поданным без сообщений Windows: линейки, слои, кнопки, подсказка, наложение и мягкий
/// сигнал. Окно стоит за краем экрана и снимается только само (<see cref="RenderTargetBitmap"/> окна, не экрана). Снимки пишутся в
/// каталог из переменной среды <c>PFL_UI_SHOTS</c>, если она задана.
/// </summary>
[Trait("Category", "UI")]
public class LessonWindowUiTests
{
    [Fact]
    public void The_scenario_runs_in_the_real_window_from_demo_through_traces_and_copies_to_the_end()
    {
        RunSta(() =>
        {
            LessonWindow window = OpenWindow();
            try
            {
                LessonViewModel vm = window.ViewModel!;
                Assert.NotNull(vm.WritingFrame);
                Assert.Equal(LessonPhase.Demo, vm.Phase);
                Pump(() => window.DemoLayer.IsPlaying || window.DemoLayer.PositionMs > 0, 5000);
                Shot(window, "01-demo");
                Assert.Equal(Text("ui.step.demo"), window.InstructionText.Text);
                Assert.Equal(Visibility.Visible, window.NextButton.Visibility);
                Assert.Equal(Visibility.Collapsed, window.DoneButton.Visibility);
                Assert.True(window.NextButton.ActualHeight >= MmToDip(window, SandboxLayout.TargetMinMm) - 0.5); // цели для пера ≥ 12 мм

                // Ввод во время показа не принимается: письма нет.
                Write(window, Ideal("a"));
                Assert.Equal(0, InkPixels(window));

                Click(window.NextButton);
                Assert.Equal(LessonPhase.Writing, vm.Phase);
                Assert.Equal(Visibility.Visible, window.UnderPenLayer.Visibility); // обводка: эталон бледно под пером
                Assert.Equal(Visibility.Visible, window.DoneButton.Visibility);
                Shot(window, "02-trace-1-empty");

                // Обводка 1: чистая. Подсказки нет, похвала процесса.
                Write(window, Ideal("a"));
                Assert.True(InkPixels(window) > 500);
                Shot(window, "03-trace-1-written");
                FinishAttempt(window);
                Assert.Equal(LessonPhase.Feedback, vm.Phase);
                Assert.Equal(Text("praise.process.path"), window.MessageText.Text);
                Shot(window, "04-trace-1-feedback");
                Click(window.NextButton);
                Assert.Equal(0, InkPixels(window)); // «Дальше» очищает холст

                // Обводка 2: неверный старт. Сигнал по ходу письма — мигает точка старта; после «Готово» — одна подсказка с наложением.
                Write(window, Mutated("a", SynthMutation.WrongStart), stopAfterPoints: 12);
                Assert.Equal(1, vm.LiveCueSerial);
                Assert.True(window.CueLayer.IsPulsing);
                Shot(window, "05-trace-2-live-cue");
                Write(window, Mutated("a", SynthMutation.WrongStart), skipStrokeStart: 12);
                FinishAttempt(window);
                Assert.Equal(FeedbackKeys.Start, vm.Plan!.HintKey);
                Assert.Equal(Text("fb.start"), window.MessageText.Text);
                Assert.True(window.HighlightLayer.HasContent);
                Assert.True(window.HighlightLayer.IsPulsing);
                Assert.Equal(HighlightColor.Warm, window.HighlightLayer.Tone);
                Shot(window, "06-trace-2-feedback-start");

                // Язык переключается на лету, состояние не меняется.
                Click(window.LanguageButton);
                Assert.Equal(Text("fb.start", "en"), window.MessageText.Text);
                Shot(window, "07-trace-2-feedback-en");
                Click(window.LanguageButton);

                Click(window.NextButton);
                Assert.False(window.HighlightLayer.HasContent);

                // Обводка 3 и две копии: чисто.
                for (int i = 0; i < 3; i++)
                {
                    Write(window, Ideal("a"));
                    FinishAttempt(window);
                    if (i == 0)
                    {
                        Shot(window, "08-trace-3-feedback");
                    }

                    Click(window.NextButton);
                    if (i == 0)
                    {
                        Assert.Equal(StepKind.Copy, vm.Step.Kind);
                        Assert.Equal(Visibility.Collapsed, window.UnderPenLayer.Visibility);
                        Assert.Equal(Visibility.Visible, window.DemoLayer.Visibility); // копия: эталон рядом
                        Shot(window, "09-copy-1-empty");
                    }
                }

                Assert.Equal(LessonPhase.Finished, vm.Phase);
                Assert.Equal(5, vm.CompletedAttempts);
                Assert.Equal(Text("ui.finished"), window.InstructionText.Text);
                Assert.Equal(Visibility.Visible, window.RestartLessonButton.Visibility);
                Shot(window, "10-finished");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_missing_bar_of_t_is_highlighted_on_the_model_beside_in_the_copy_step()
    {
        RunSta(() =>
        {
            LessonWindow window = OpenWindow("--letter", "t");
            try
            {
                LessonViewModel vm = window.ViewModel!;
                Click(window.NextButton);
                for (int i = 0; i < 3; i++)
                {
                    Write(window, Ideal("t"));
                    FinishAttempt(window);
                    Click(window.NextButton);
                }

                Assert.Equal(StepKind.Copy, vm.Step.Kind);
                Write(window, Mutated("t", SynthMutation.MissingDeferred));
                FinishAttempt(window);

                Assert.Equal(FeedbackKeys.DeferredMissing, vm.Plan!.HintKey);
                Assert.Contains(Text("part.bar"), window.MessageText.Text, StringComparison.Ordinal);
                Assert.True(window.HighlightLayer.HasContent);
                Assert.Same(vm.DemoFrame, window.HighlightLayer.Guides); // подсветка на эталоне рядом (линейка показа)
                Shot(window, "11-copy-missing-bar");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static LessonWindow OpenWindow(params string[] extra)
    {
        var options = AppOptions.Parse(["--mouse", "--windowed", "--content-dir", ContentRoot, .. extra]);
        var window = new LessonWindow(options)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = 0,
            ShowActivated = false,
            PlaceOnPenMonitor = false,
        };
        window.Show();
        Pump(() => window.ViewModel?.WritingFrame is not null && window.InkLayer.Surface is not null, 10000);
        return window;
    }

    private static string Text(string key, string language = "ru") => Content.Locale(language).Get(key);

    private static double MmToDip(LessonWindow window, double mm) =>
        mm * window.ViewModel!.WritingFrame!.PxPerMm / VisualTreeHelper.GetDpi(window).DpiScaleX;

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled && button.IsVisible, "button must be enabled and visible");
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        Pump(() => true, 100);
    }

    /// <summary>«Готово» и ожидание оценки в фоне: пока она идёт, интерфейс отзывчив (насос сообщений крутится).</summary>
    private static void FinishAttempt(LessonWindow window)
    {
        LessonViewModel vm = window.ViewModel!;
        Click(window.DoneButton);
        Pump(() => vm.Phase != LessonPhase.Evaluating, 10000);
        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        Pump(() => true, 50);
    }

    /// <summary>Подаёт попытку как точки пера: пачками по 5 точек, как у источника 200 Гц.</summary>
    private static void Write(LessonWindow window, RulerInk ink, int? stopAfterPoints = null, int skipStrokeStart = 0)
    {
        GuideFrame frame = window.ViewModel!.WritingFrame!;
        long baseUs = MonotonicTime.ToMicroseconds(new QpcMonotonicClock().Ticks, new QpcMonotonicClock().Frequency) + 50_000;
        var batch = new List<PenSample>();
        int sent = 0;
        foreach (RulerStroke stroke in ink.Strokes)
        {
            for (int i = 0; i < stroke.Points.Length; i++)
            {
                if (stopAfterPoints is { } stop && sent >= stop)
                {
                    Flush(window, batch);
                    window.InkLayer.FrameTick();
                    return;
                }

                sent++;
                if (sent <= skipStrokeStart)
                {
                    continue;
                }

                RulerPoint p = stroke.Points[i];
                (double x, double y) = RulerProjection.ToSurface(frame, p.U, p.V);
                PenPhase phase = i == 0 ? PenPhase.Down : (i == stroke.Points.Length - 1 ? PenPhase.Up : PenPhase.Move);
                batch.Add(new PenSample(baseUs + p.TimeUs, x, y, p.Pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 1));
                if (batch.Count == 5)
                {
                    Flush(window, batch);
                }
            }
        }

        Flush(window, batch);
        window.InkLayer.FrameTick();
    }

    private static void Flush(LessonWindow window, List<PenSample> batch)
    {
        if (batch.Count > 0)
        {
            window.FeedSamples(batch.ToArray());
            batch.Clear();
        }
    }

    private static int InkPixels(LessonWindow window)
    {
        window.InkLayer.FrameTick();
        int count = 0;
        foreach (uint pixel in window.InkLayer.Surface!.Pixels)
        {
            if ((pixel >> 24) != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void Shot(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PFL_UI_SHOTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        Pump(() => true, 100);
        window.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    /// <summary>Крутит очередь сообщений потока UI, пока не выполнено условие (или не вышло время).</summary>
    private static void Pump(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        do
        {
            dispatcher.Invoke(DispatcherPriority.Background, () => { });
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }
        while (clock.ElapsedMilliseconds < timeoutMs);

        if (!condition())
        {
            throw new TimeoutException("The condition was not met within " + timeoutMs + " ms.");
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("UI scenario failed: " + failure.Message, failure);
        }
    }
}
