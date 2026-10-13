namespace PenForLang.App.Wpf;

/// <summary>Точка входа: разбор параметров и настройка переключателя стилуса до первого обращения WPF к нему.</summary>
public static class Program
{
    /// <summary>Запуск приложения.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);

        // По умолчанию переключатель включён через runtimeconfig (PenForLang.App.Wpf.csproj); здесь он переопределяется флагом.
        // WPF читает его лениво при первом использовании стилуса, поэтому значение надо задать до создания приложения.
        AppContext.SetSwitch(AppOptions.DisableStylusSwitch, !options.KeepWpfStylus);
        if (options.WpfPointer)
        {
            AppContext.SetSwitch(AppOptions.EnablePointerSupportSwitch, true);
        }

        App.Options = options;
        var app = new App();
        return app.Run();
    }
}
