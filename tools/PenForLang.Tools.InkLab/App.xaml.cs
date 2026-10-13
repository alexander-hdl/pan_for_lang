using System.IO;
using System.Windows;

namespace PenForLang.Tools.InkLab;

/// <summary>Приложение InkLab.</summary>
public partial class App : Application
{
    /// <summary>Параметры командной строки; задаются в <see cref="Program.Main"/> до создания приложения.</summary>
    internal static LabOptions Options { get; set; } = LabOptions.Parse([]);

    /// <summary>Файл журнала сбоев: <c>%LOCALAPPDATA%\PenForLang\inklab-crash.log</c>.</summary>
    public static string CrashLogPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenForLang", "inklab-crash.log");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Запись нельзя терять из-за сбоя интерфейса: сообщение со всей цепочкой уходит в журнал, а окно остаётся жить.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                "Ошибка: " + args.Exception.Message + Environment.NewLine + "Подробности: " + CrashLogPath,
                "InkLab",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow(Options);
        MainWindow.Show();
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал недоступен — сообщение в окне всё равно будет показано.
        }
    }
}
