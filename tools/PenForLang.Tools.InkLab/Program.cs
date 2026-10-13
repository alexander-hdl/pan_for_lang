namespace PenForLang.Tools.InkLab;

/// <summary>Точка входа: разбор параметров и настройка переключателя стилуса до первого обращения WPF к нему.</summary>
public static class Program
{
    /// <summary>Имя переключателя WPF, отключающего WISP/стилус (ARCHITECTURE.md §7.1, ADR-003).</summary>
    public const string DisableStylusSwitch = "Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport";

    /// <summary>Запуск приложения.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        LabOptions options = LabOptions.Parse(args);

        // В runtimeconfig переключатель уже включён (csproj); здесь он повторяется для запуска вне publish-папки.
        AppContext.SetSwitch(DisableStylusSwitch, true);
        App.Options = options;
        var app = new App();
        return app.Run();
    }
}
