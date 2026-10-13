using System.Collections.Immutable;
using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.Tools.InkLab.Recording;

/// <summary>
/// Накопитель одной попытки в памяти: точки контакта (время пересчитано в «мкс от начала задания» через
/// <see cref="AttemptTimeRebaser"/>) и события. По «Готово» превращается в <see cref="PenLog"/>, который пишет
/// <c>PenLogWriter</c>. Ввод-вывода здесь нет; для лабораторного инструмента этого достаточно (попытка — несколько тысяч точек).
/// </summary>
/// <remarks>Не потокобезопасен: точки приходят в потоке UI (хук окна).</remarks>
public sealed class AttemptCapture
{
    private readonly IMonotonicClock _clock;
    private readonly List<PenLogEntry> _entries = [];
    private AttemptTimeRebaser? _rebaser;
    private long _lastTimeUs;
    private int _contactSamples;
    private int _restarts;
    private bool _penDown;

    /// <summary>Создаёт накопитель.</summary>
    /// <param name="clock">Монотонные часы источника пера (QPC): от них отсчитывается начало задания.</param>
    public AttemptCapture(IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Попытка начата и не завершена.</summary>
    public bool IsActive => _rebaser is not null;

    /// <summary>Сколько точек контакта принято.</summary>
    public int ContactSamples => _contactSamples;

    /// <summary>Сколько раз нажато «Заново».</summary>
    public int Restarts => _restarts;

    /// <summary>Перо сейчас касается поверхности (по последней принятой точке).</summary>
    public bool IsPenDown => _penDown;

    /// <summary>Начинает попытку: время задания отсчитывается от этого момента. Предыдущая незавершённая попытка отбрасывается.</summary>
    public void Begin()
    {
        _entries.Clear();
        _rebaser = AttemptTimeRebaser.Start(_clock);
        _lastTimeUs = 0;
        _contactSamples = 0;
        _restarts = 0;
        _penDown = false;
    }

    /// <summary>Отбрасывает попытку без записи.</summary>
    public void Discard()
    {
        _entries.Clear();
        _rebaser = null;
        _contactSamples = 0;
        _penDown = false;
    }

    /// <summary>Принимает пачку точек источника (абсолютное время). Hover пропускается; без начатой попытки ничего не делает.</summary>
    public void AddSamples(ReadOnlySpan<PenSample> samples)
    {
        if (_rebaser is null)
        {
            return;
        }

        foreach (PenSample sample in samples)
        {
            if (!sample.IsContact || !_rebaser.TryRebase(in sample, out PenSample rebased))
            {
                if (sample.Phase == PenPhase.Hover)
                {
                    _penDown = false;
                }

                continue;
            }

            // Метки устройства могут слегка отставать от момента события: писатель требует неубывающего времени.
            PenSample toWrite = rebased.TimeUs < _lastTimeUs ? rebased with { TimeUs = _lastTimeUs } : rebased;
            _lastTimeUs = toWrite.TimeUs;
            _entries.Add(new PenLogSampleEntry(toWrite));
            _contactSamples++;
            _penDown = toWrite.Phase != PenPhase.Up;
        }
    }

    /// <summary>Записывает событие в текущий момент задания.</summary>
    public void RecordEvent(string name, IReadOnlyDictionary<string, string>? data = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_rebaser is null)
        {
            return;
        }

        long now = _rebaser.ToTaskTime(MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency));
        long time = Math.Max(now, _lastTimeUs);
        _lastTimeUs = time;
        _entries.Add(new PenLogEventEntry(new InkEvent(time, name, data)));
    }

    /// <summary>«Заново» (A10): событие <c>restart-button</c>; штрихи до него в penlog остаются, но в <see cref="PenLog.ToInkAttempt"/> не попадают.</summary>
    public void Restart()
    {
        _restarts++;
        _penDown = false;
        RecordEvent(InkEvent.Names.RestartButton);
    }

    /// <summary>
    /// Завершает попытку: событие <c>completed</c> и сборка лога.
    /// </summary>
    /// <param name="header">Заголовок penlog.</param>
    /// <param name="completedBy">Причина (<c>done-button</c>, <c>cancelled</c>).</param>
    /// <returns>Лог или <c>null</c>, если контакта не было (писать нечего).</returns>
    public PenLog? Complete(PenLogHeader header, string completedBy = "done-button")
    {
        ArgumentNullException.ThrowIfNull(header);
        if (_rebaser is null)
        {
            return null;
        }

        RecordEvent(InkEvent.Names.Completed, new Dictionary<string, string> { ["by"] = completedBy });
        PenLog? log = _contactSamples == 0 ? null : new PenLog(header, [.. _entries]);
        Discard();
        return log;
    }
}
