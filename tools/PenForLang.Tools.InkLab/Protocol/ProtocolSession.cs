namespace PenForLang.Tools.InkLab.Protocol;

/// <summary>
/// Ход прохождения протокола: какая подсказка текущая, как работают «Готово», «Пропустить» и «Назад». Чистая логика над
/// <see cref="ProtocolProgress"/>, без файлов и окна; сохранение прогресса — забота вызывающего.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Старт (и возобновление): текущая — сохранённая <see cref="ProtocolProgress.Current"/>, если она ещё не записана,
/// иначе первая незаписанная и непропущенная; если таких нет — первая пропущенная; если и их нет — протокол пройден.</item>
/// <item>«Готово» и «Пропустить» двигают к следующей незаписанной подсказке (сначала дальше по списку, затем с начала);
/// когда незаписанных нет — к первой пропущенной; иначе <see cref="IsComplete"/>.</item>
/// <item>«Назад» возвращает к предыдущей по списку (в том числе уже записанной: её можно записать заново).</item>
/// </list>
/// </remarks>
public sealed class ProtocolSession
{
    private int _index;

    /// <summary>Создаёт ход по протоколу и прогрессу (прогресс изменяется на месте).</summary>
    public ProtocolSession(RecordingProtocol protocol, ProtocolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(progress);
        Protocol = protocol;
        Progress = progress;
        if (protocol.Prompts.Length == 0)
        {
            _index = -1;
            return;
        }

        int saved = progress.Current is null ? -1 : protocol.IndexOf(progress.Current);
        if (saved >= 0 && progress.StatusOf(protocol.Prompts[saved].Id) != PromptStatus.Done)
        {
            _index = saved;
        }
        else
        {
            _index = FindNext(-1);
        }

        progress.Current = Current?.Id;
    }

    /// <summary>Протокол.</summary>
    public RecordingProtocol Protocol { get; }

    /// <summary>Прогресс.</summary>
    public ProtocolProgress Progress { get; }

    /// <summary>Индекс текущей подсказки или -1, если протокол пройден.</summary>
    public int CurrentIndex => _index;

    /// <summary>Текущая подсказка или <c>null</c>, если всё записано или пропущено.</summary>
    public RecordingPrompt? Current => _index >= 0 && _index < Protocol.Prompts.Length ? Protocol.Prompts[_index] : null;

    /// <summary>Незаписанных и непропущенных подсказок не осталось.</summary>
    public bool IsComplete => Current is null;

    /// <summary>Сколько подсказок записано.</summary>
    public int DoneCount => Count(PromptStatus.Done);

    /// <summary>Сколько подсказок пропущено.</summary>
    public int SkippedCount => Count(PromptStatus.Skipped);

    /// <summary>Сколько подсказок ещё не записано и не пропущено.</summary>
    public int PendingCount => Count(PromptStatus.Pending);

    /// <summary>Состояние подсказки по индексу.</summary>
    public PromptStatus StatusAt(int index) => Progress.StatusOf(Protocol.Prompts[index].Id);

    /// <summary>Записать результат для текущей подсказки и перейти к следующей.</summary>
    /// <param name="file">Файл penlog относительно папки субъекта.</param>
    /// <param name="restarts">Сколько раз нажато «Заново» в принятой попытке.</param>
    /// <exception cref="InvalidOperationException">Протокол пройден, текущей подсказки нет.</exception>
    public void MarkDone(string file, int restarts)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        RecordingPrompt prompt = Current ?? throw new InvalidOperationException("The protocol is complete: there is no current prompt.");
        PromptProgress entry = Progress.Entry(prompt.Id);
        entry.Status = PromptStatus.Done;
        entry.File = file;
        entry.Restarts = restarts;
        entry.Takes++;
        MoveNext();
    }

    /// <summary>Пропустить текущую подсказку (если она уже была записана, запись остаётся: статус не меняется) и перейти дальше.</summary>
    public void Skip()
    {
        RecordingPrompt prompt = Current ?? throw new InvalidOperationException("The protocol is complete: there is no current prompt.");
        if (Progress.StatusOf(prompt.Id) != PromptStatus.Done)
        {
            Progress.Entry(prompt.Id).Status = PromptStatus.Skipped;
        }

        MoveNext(excludeCurrentFromSkipped: true);
    }

    /// <summary>Вернуться к предыдущей подсказке по списку. На первой подсказке остаётся на месте.</summary>
    /// <returns><c>true</c>, если текущая подсказка изменилась.</returns>
    public bool Back()
    {
        int from = _index < 0 ? Protocol.Prompts.Length : _index;
        if (from <= 0)
        {
            return false;
        }

        _index = from - 1;
        Progress.Current = Current?.Id;
        return true;
    }

    /// <summary>Перейти к подсказке по индексу (выбор в списке).</summary>
    /// <returns><c>true</c>, если индекс допустим.</returns>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= Protocol.Prompts.Length)
        {
            return false;
        }

        _index = index;
        Progress.Current = Current?.Id;
        return true;
    }

    private void MoveNext(bool excludeCurrentFromSkipped = false)
    {
        _index = FindNext(_index, excludeCurrentFromSkipped ? _index : -2);
        Progress.Current = Current?.Id;
    }

    /// <summary>Следующая незаписанная после <paramref name="after"/>, затем с начала; затем первая пропущенная; иначе -1.</summary>
    private int FindNext(int after, int excludeSkipped = -2)
    {
        int n = Protocol.Prompts.Length;
        for (int step = 1; step <= n; step++)
        {
            int i = (after + step + n) % n;
            if (StatusAt(i) == PromptStatus.Pending)
            {
                return i;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (i != excludeSkipped && StatusAt(i) == PromptStatus.Skipped)
            {
                return i;
            }
        }

        return -1;
    }

    private int Count(PromptStatus status)
    {
        int count = 0;
        foreach (RecordingPrompt prompt in Protocol.Prompts)
        {
            if (Progress.StatusOf(prompt.Id) == status)
            {
                count++;
            }
        }

        return count;
    }
}
