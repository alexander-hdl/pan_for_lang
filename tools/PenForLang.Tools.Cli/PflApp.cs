using System.CommandLine;

namespace PenForLang.Tools.Cli;

/// <summary>
/// Точка входа утилиты <c>pfl</c>. Вынесена из <c>Program</c>, чтобы тесты вызывали её с подменёнными потоками вывода.
/// Команды (<c>validate</c>, <c>render</c>, ...) добавляются в <see cref="CreateRootCommand"/> по мере появления задач.
/// </summary>
public static class PflApp
{
    /// <summary>Описание утилиты в справке.</summary>
    public const string Description = "pfl - developer tool of PenForLang (content validation, rendering, evaluation, simulation).";

    /// <summary>Создаёт корневую команду. Стандартные <c>--version</c>, <c>--help</c> добавляет System.CommandLine.</summary>
    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand(Description);
        root.Subcommands.Add(StyleCommands.CreateValidateCommand());
        root.Subcommands.Add(StyleCommands.CreateRenderCommand());
        root.Subcommands.Add(EvalCommands.CreateEvalCommand());
        return root;
    }

    /// <summary>Выполняет утилиту; возвращает код завершения процесса.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var configuration = new InvocationConfiguration { Output = output, Error = error };
        return CreateRootCommand().Parse(args).Invoke(configuration);
    }
}
