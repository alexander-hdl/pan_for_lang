using System.Windows;

namespace PenForLang.App.Wpf;

/// <summary>Приложение. Корень композиции V0.5: параметры командной строки и окно-холст.</summary>
public partial class App : Application
{
    /// <summary>Параметры командной строки; задаются в <see cref="Program.Main"/> до создания приложения.</summary>
    internal static AppOptions Options { get; set; } = AppOptions.Parse([]);

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow(Options);
        MainWindow.Show();
    }
}
