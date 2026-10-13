using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls.Tests.Support;

/// <summary>Запуск кода в STA-потоке: объекты WPF требуют его, а xunit по умолчанию запускает тесты в MTA.</summary>
internal static class Sta
{
    public static void Run(Action action)
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
            throw new InvalidOperationException("STA action failed: " + failure.Message, failure);
        }
    }
}

/// <summary>Стиль-пакет репозитория и вспомогательные построители.</summary>
internal static class Shipped
{
    private static readonly Lazy<StylePack> LazyPack = new(Load);

    public static StylePack Pack => LazyPack.Value;

    public static StyleManifest Style => Pack.Manifest;

    private static StylePack Load()
    {
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PenForLang.sln")))
            {
                root = dir.FullName;
                break;
            }
        }

        string content = Path.Combine(root ?? throw new InvalidOperationException("PenForLang.sln not found."), "content");
        var source = new InMemoryContentSource();
        foreach (string file in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
        {
            source.Add(Path.GetRelativePath(content, file).Replace('\\', '/'), File.ReadAllBytes(file));
        }

        return StyleLoader.LoadOrThrow(source, "styles/zaner-bloser");
    }
}

/// <summary>Генераторы точек пера.</summary>
internal static class TestSamples
{
    /// <summary>Штрих: Down, затем <paramref name="count"/> - 2 точек Move и Up; шаг (dx, dy) px, время растёт на 5 мс.</summary>
    public static PenSample[] Stroke(double x, double y, double dx, double dy, int count, float pressure = 0.5f, long startUs = 1_000_000, uint pointer = 1)
    {
        var samples = new PenSample[count];
        for (int i = 0; i < count; i++)
        {
            PenPhase phase = i == 0 ? PenPhase.Down : (i == count - 1 ? PenPhase.Up : PenPhase.Move);
            samples[i] = new PenSample(startUs + (i * 5000L), x + (dx * i), y + (dy * i), pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, pointer);
        }

        return samples;
    }

    public static PenSample Contact(double x, double y, long timeUs, PenPhase phase = PenPhase.Move, float pressure = 0.5f) =>
        new(timeUs, x, y, pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 1);

    public static PenSample Hover(double x, double y, long timeUs = 1_000_000) =>
        new(timeUs, x, y, 0, 0, 0, PenPhase.Hover, PenButtons.None, SampleFlags.None, 1);
}

/// <summary>Чтение пикселей отрисованных элементов.</summary>
internal static class Pixels
{
    /// <summary>Рисует элемент в растр заданного размера (DIP) при заданном DPI-масштабе и возвращает пиксели BGRA.</summary>
    public static (byte[] Data, int Width, int Height) Render(FrameworkElement element, double widthDip, double heightDip, double dpiScale)
    {
        element.Measure(new Size(widthDip, heightDip));
        element.Arrange(new Rect(0, 0, widthDip, heightDip));
        element.UpdateLayout();
        int w = (int)Math.Round(widthDip * dpiScale);
        int h = (int)Math.Round(heightDip * dpiScale);
        var bitmap = new RenderTargetBitmap(w, h, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var data = new byte[w * h * 4];
        bitmap.CopyPixels(data, w * 4, 0);
        return (data, w, h);
    }

    public static byte Alpha(byte[] data, int width, int x, int y) => data[(((y * width) + x) * 4) + 3];

    public static int CountAlpha(byte[] data) => CountWhere(data, a => a > 0);

    public static int CountWhere(byte[] data, Func<byte, bool> alphaPredicate)
    {
        int count = 0;
        for (int i = 3; i < data.Length; i += 4)
        {
            if (alphaPredicate(data[i]))
            {
                count++;
            }
        }

        return count;
    }
}
