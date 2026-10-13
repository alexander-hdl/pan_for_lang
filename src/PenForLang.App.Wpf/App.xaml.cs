using System.Windows;

namespace PenForLang.App.Wpf;

/// <summary>Приложение. Корень композиции: параметры командной строки и окно урока (V0.8) либо окно-песочница (<c>--sandbox</c>).</summary>
public partial class App : Application
{
    /// <summary>Параметры командной строки; задаются в <see cref="Program.Main"/> до создания приложения.</summary>
    internal static AppOptions Options { get; set; } = AppOptions.Parse([]);

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            // Родитель не должен остаться без отчёта: при сбое пишем его до падения.
            if (MainWindow is MainWindow window)
            {
                window.WriteCrashReport(args.Exception);
            }
        };
        // Основной экран — фиксированный урок (V0.8); песочница V0.6 с протоколом железа и отчётом — по --sandbox.
        MainWindow = Options.Sandbox ? new MainWindow(Options) : new LessonWindow(Options);
        MainWindow.Show();
    }
}
