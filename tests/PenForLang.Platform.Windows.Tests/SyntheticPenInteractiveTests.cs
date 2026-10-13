using System.Collections.Concurrent;
using System.Windows.Interop;
using System.Windows.Threading;
using PenForLang.Ink;

namespace PenForLang.Platform.Windows.Tests;

/// <summary>
/// Тесты категории Interactive (ARCHITECTURE.md §10.1): синтетический указатель-перо
/// (<c>CreateSyntheticPointerDevice(PT_PEN)</c> + <c>InjectSyntheticPointerInput</c>) доходит до <see cref="Win32PointerPenSource"/>
/// через настоящее окно и хук. Исключены из обычного CI (<c>--filter-not-trait "Category=Interactive"</c>); запуск:
/// <c>dotnet test --filter-trait "Category=Interactive"</c> на машине с рабочим столом. Если инъекция недоступна
/// (нет прав, сеанс без ввода, Windows старше 1809) — тест пропускается, а не падает.
/// </summary>
[Trait("Category", "Interactive")]
public class SyntheticPenInteractiveTests
{
    private const int WindowX = 240;
    private const int WindowY = 240;
    private const int WindowWidth = 500;
    private const int WindowHeight = 400;

    static SyntheticPenInteractiveTests()
    {
        // Как в приложении (ADR-003): без этого переключателя WPF (WISP/RealTimeStylus) забирает контакт пера себе и окно получает
        // его мышиными сообщениями без WM_POINTER (наблюдалось: WM_POINTER приходит только на наведение). Задаётся до первого окна WPF.
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);
    }

    [Fact]
    public async Task Injected_pen_stroke_reaches_the_win32_source_with_pressure_and_order()
    {
        using SyntheticPenInjector? injector = SyntheticPenInjector.TryCreate(out int error);
        if (injector is null)
        {
            Assert.Skip($"Synthetic pointer device is unavailable (Win32 error {error}).");
        }

        using var window = new TestPenWindow(WindowX, WindowY, WindowWidth, WindowHeight);
        var received = new ConcurrentQueue<PenSample>();
        Win32PointerPenSource source = window.Invoke(() =>
        {
            var s = new Win32PointerPenSource(new QpcMonotonicClock());
            s.Attach(window.Source);
            s.SetSurface(new PenSurface(WindowX, WindowY, WindowWidth, WindowHeight));
            s.SamplesReceived += batch =>
            {
                foreach (PenSample sample in batch.Span)
                {
                    received.Enqueue(sample);
                }
            };
            return s;
        });
        await source.StartAsync(CancellationToken.None);

        // Штрих по диагонали внутри окна; давление растёт 100 → 900.
        const int Moves = 30;
        bool injected = injector.Hover(WindowX + 100, WindowY + 100);
        injected &= injector.Hover(WindowX + 101, WindowY + 100);
        Thread.Sleep(20);
        injected &= injector.Down(WindowX + 100, WindowY + 100, pressure: 100);
        for (int i = 1; i <= Moves; i++)
        {
            Thread.Sleep(8);
            injected &= injector.Move(WindowX + 100 + (i * 5), WindowY + 100 + (i * 3), pressure: (uint)(100 + (i * 800 / Moves)));
        }

        Thread.Sleep(8);
        injected &= injector.Up(WindowX + 100 + (Moves * 5), WindowY + 100 + (Moves * 3));

        if (!injected)
        {
            Assert.Skip($"Pointer injection was refused (Win32 error {injector.LastError}).");
        }

        PenSample[] contact = await WaitForAsync(received, s => s.Any(p => p.Phase == PenPhase.Up), TimeSpan.FromSeconds(5))
            ?? throw new Xunit.Sdk.XunitException(
                $"Injection succeeded but no stroke reached the source. Diagnostics: {source.Diagnostics}; messages: {string.Join(",", window.SeenMessages)}; samples: "
                + string.Join(" | ", received.Select(p => $"{p.Phase}@{p.X:0},{p.Y:0} p={p.Pressure:0.00}")));

        PenSample[] stroke = contact.Where(p => p.IsContact).ToArray();
        Assert.Equal(PenPhase.Down, stroke[0].Phase);
        Assert.Equal(PenPhase.Up, stroke[^1].Phase);
        Assert.True(stroke.Length >= Moves, $"Expected at least {Moves} contact points, got {stroke.Length}. {source.Diagnostics}");
        Assert.All(stroke, p => Assert.Equal(stroke[0].PointerId, p.PointerId));

        // Хронологический порядок и неубывающее время (история развёрнута правильно).
        for (int i = 1; i < stroke.Length; i++)
        {
            Assert.True(stroke[i].TimeUs >= stroke[i - 1].TimeUs, $"time went backwards at {i}");
        }

        // Давление дошло и не вырожденное; координаты — в системе холста, без DPI-масштаба (±2 px от заданных).
        Assert.Contains(stroke, p => p.Pressure > 0.5f);
        Assert.InRange(stroke[0].X, 98, 102);
        Assert.InRange(stroke[0].Y, 98, 102);
        Assert.InRange(stroke[^1].X, 100 + (Moves * 5) - 2, 100 + (Moves * 5) + 2);

        await source.DisposeAsync();
    }

    [Fact]
    public void Ink_suppression_applies_to_a_real_window()
    {
        using var window = new TestPenWindow(WindowX, WindowY, WindowWidth, WindowHeight);

        InkSuppressionResult result = window.Invoke(() => WindowInkSuppressor.Apply(window.Source.Handle));

        Assert.True(result.TabletPropertySet, string.Join("; ", result.Errors));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static async Task<PenSample[]?> WaitForAsync(
        ConcurrentQueue<PenSample> queue,
        Func<PenSample[], bool> condition,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            PenSample[] snapshot = queue.ToArray();
            if (condition(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        return null;
    }

    /// <summary>Настоящее окно (WS_POPUP, topmost, без активации) с собственным потоком сообщений.</summary>
    private sealed class TestPenWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Dispatcher? _dispatcher;
        private HwndSource? _source;

        public TestPenWindow(int x, int y, int width, int height)
        {
            _thread = new Thread(() =>
            {
                const int WsPopup = unchecked((int)0x80000000);
                const int WsVisible = 0x10000000;
                const int WsExTopmost = 0x00000008;
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;

                var parameters = new HwndSourceParameters("PenForLangSyntheticPenTest", width, height)
                {
                    PositionX = x,
                    PositionY = y,
                    WindowStyle = WsPopup | WsVisible,
                    ExtendedWindowStyle = WsExTopmost | WsExToolWindow | WsExNoActivate,
                };
                _source = new HwndSource(parameters);
                _source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (msg is >= 0x0238 and <= 0x0253 or >= 0x0200 and <= 0x020E)
                    {
                        SeenMessages.Enqueue($"0x{msg:X3}");
                    }

                    return 0;
                });
                _dispatcher = Dispatcher.CurrentDispatcher;
                _ready.Set();
                Dispatcher.Run();
                _source.Dispose();
            })
            {
                IsBackground = true,
                Name = "PenForLang.TestPenWindow",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
        }

        public ConcurrentQueue<string> SeenMessages { get; } = new();

        public HwndSource Source => _source!;

        public T Invoke<T>(Func<T> action) => _dispatcher!.Invoke(action);

        public void Dispose()
        {
            _dispatcher?.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }
    }
}
