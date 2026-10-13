using System.IO;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Scene;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Вкладка «Повтор» без окна: воспроизведение через <see cref="ReplayPenSource"/> на виртуальных часах.</summary>
public sealed class ReplayTabViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    private ReplayTabViewModel NewVm() => new(_clock);

    private static int PlayedPoints(InkScene scene) =>
        scene.Polylines.Where(p => p.Style != SceneStyle.Ghost).Sum(p => p.Points.Length);

    private static int PlayedStrokes(InkScene scene) => scene.Polylines.Count(p => p.Style != SceneStyle.Ghost);

    private string Write(PenLog log, string name = "replay") => TestData.WriteLog(_folder.Path, name, log);

    [Fact]
    public void Loading_shows_the_whole_attempt_as_a_ghost_and_fills_the_metadata()
    {
        using ReplayTabViewModel vm = NewVm();

        Assert.True(vm.Load(Write(TestData.AtWordLifted())));

        Assert.Equal(3, vm.Scene.Polylines.Count(p => p.Style == SceneStyle.Ghost));
        Assert.Equal(0, PlayedPoints(vm.Scene));
        Assert.Contains("Субъект: tester (взрослый)", vm.Metadata);
        Assert.Contains(vm.Metadata, line => line.StartsWith("Задание: word:at", StringComparison.Ordinal));
        Assert.Contains(vm.Metadata, line => line.StartsWith("Штрихов: 3, точек: 53", StringComparison.Ordinal));
        Assert.Contains(vm.Metadata, line => line.Contains("7.50 px/мм", StringComparison.Ordinal));
        Assert.Contains(vm.Metadata, line => line.Contains("x-height 13.3 мм", StringComparison.Ordinal));
        Assert.True(vm.PlayCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public void Playing_in_real_time_reveals_points_as_the_clock_advances_and_finishes_at_the_end()
    {
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(TestData.AtWordLifted()));

        vm.Play();
        vm.Tick();
        Assert.True(vm.IsPlaying);
        Assert.Equal(1, PlayedPoints(vm.Scene)); // только первая точка в момент 0

        _clock.Advance(TimeSpan.FromMilliseconds(250));
        vm.Tick();

        // Штрих 0 целиком (21 точка) и штрих 1 до 250 мс: точки 200…250 мс — 11 штук.
        Assert.Equal(2, PlayedStrokes(vm.Scene));
        Assert.Equal(21 + 11, PlayedPoints(vm.Scene));
        Assert.InRange(vm.Progress, 0.4, 0.5);

        _clock.Advance(TimeSpan.FromSeconds(1));
        vm.Tick();

        Assert.False(vm.IsPlaying);
        Assert.Equal(1.0, vm.Progress);
        Assert.Equal(3, PlayedStrokes(vm.Scene));
        Assert.Equal(53, PlayedPoints(vm.Scene));
        Assert.Contains("закончено", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Accelerated_playback_finishes_proportionally_sooner()
    {
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(TestData.AtWordLifted()));
        vm.Speed = 4;

        vm.Play();
        _clock.Advance(TimeSpan.FromMilliseconds(70)); // 280 мс записи
        vm.Tick();
        Assert.Equal(21 + 17, PlayedPoints(vm.Scene)); // штрих 1: точки 200…280 мс
        Assert.True(vm.IsPlaying);

        _clock.Advance(TimeSpan.FromMilliseconds(70)); // 560 мс записи ≥ 550
        vm.Tick();
        Assert.False(vm.IsPlaying);
        Assert.Equal(53, PlayedPoints(vm.Scene));
    }

    [Fact]
    public void Pause_freezes_the_replay_clock_and_resume_continues_from_the_same_place()
    {
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(TestData.AtWordLifted()));
        vm.Play();
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        vm.Tick();
        int before = PlayedPoints(vm.Scene);

        vm.Pause();
        _clock.Advance(TimeSpan.FromSeconds(5));
        vm.Tick();

        Assert.False(vm.IsPlaying);
        Assert.Equal(before, PlayedPoints(vm.Scene));

        vm.Play();
        _clock.Advance(TimeSpan.FromMilliseconds(100)); // запись: 220 мс → штрих 1 до точки 4
        vm.Tick();
        Assert.Equal(21 + 5, PlayedPoints(vm.Scene));
    }

    [Fact]
    public void Restart_clears_the_ink_and_plays_again_from_the_beginning()
    {
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(TestData.AtWordLifted()));
        vm.Play();
        _clock.Advance(TimeSpan.FromSeconds(2));
        vm.Tick();
        Assert.Equal(53, PlayedPoints(vm.Scene));

        vm.Restart();

        Assert.True(vm.IsPlaying);
        Assert.Equal(0, PlayedPoints(vm.Scene)); // чернила стёрты, призрак на месте
        Assert.Equal(0.0, vm.Progress);
        Assert.Equal(3, vm.Scene.Polylines.Count(p => p.Style == SceneStyle.Ghost));
    }

    [Fact]
    public void Show_all_displays_the_whole_attempt_without_playing()
    {
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(TestData.AtWordLifted()));

        vm.ShowAll();

        Assert.False(vm.IsPlaying);
        Assert.Equal(53, PlayedPoints(vm.Scene));
        Assert.Equal(1.0, vm.Progress);
    }

    [Fact]
    public void A_restart_event_in_the_log_clears_the_replayed_canvas_like_the_writer_saw_it()
    {
        PenLog log = TestData.Log(
            "inklab-v0.9/a-001",
            "glyph:a",
            [
                TestData.Stroke(0, 1, TestData.Line(0, 0, 0.3, 0.3, 6)),
                TestData.Stroke(300_000, 1, TestData.Line(0, 1, 0, 0, 11)),
            ],
            [new InkEvent(150_000, InkEvent.Names.RestartButton)]);
        using ReplayTabViewModel vm = NewVm();
        vm.Load(Write(log));

        // Призрак — только штрихи после «Заново».
        Assert.Single(vm.Scene.Polylines, p => p.Style == SceneStyle.Ghost);

        vm.Play();
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        vm.Tick();
        Assert.Equal(6, PlayedPoints(vm.Scene));

        _clock.Advance(TimeSpan.FromMilliseconds(100)); // 220 мс: «Заново» на 150 мс стёрло первый штрих
        vm.Tick();
        Assert.Equal(0, PlayedPoints(vm.Scene));

        _clock.Advance(TimeSpan.FromSeconds(1));
        vm.Tick();
        Assert.Equal(11, PlayedPoints(vm.Scene));
        Assert.Equal(1, PlayedStrokes(vm.Scene));
        Assert.Contains(vm.Metadata, line => line.Contains("«Заново»: 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Opening_a_folder_lists_recordings_and_selecting_one_loads_it()
    {
        Write(TestData.AtWordLifted(), "one");
        Write(TestData.LetterI(), "two");
        using ReplayTabViewModel vm = NewVm();

        vm.OpenFolder(_folder.Path);

        Assert.Equal(2, vm.Files.Count);
        vm.Selected = vm.Files.Single(f => f.Target == "glyph:i");
        Assert.NotNull(vm.Log);
        Assert.Equal("glyph:i", vm.Log!.Header.Task!.Target);
        Assert.Equal(2, vm.Attempt!.Strokes.Length);
    }

    [Fact]
    public void An_unreadable_recording_is_reported_without_an_exception()
    {
        string bad = Path.Combine(_folder.Path, "bad" + CorpusStore.PenLogExtension);
        File.WriteAllText(bad, "garbage");
        using ReplayTabViewModel vm = NewVm();

        Assert.False(vm.Load(bad));

        Assert.Contains("не читается", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.PlayCommand.CanExecute(null));
        vm.Play();
        vm.Tick();
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void The_scaled_clock_runs_faster_stops_on_zero_and_does_not_jump_when_the_speed_changes()
    {
        var scaled = new ScaledClock(_clock);
        DateTimeOffset start = scaled.UtcNow;

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), scaled.UtcNow - start);

        scaled.Speed = 4;
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(5), scaled.UtcNow - start);

        scaled.Speed = 0;
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(5), scaled.UtcNow - start);

        scaled.Speed = 1;
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(7), scaled.UtcNow - start);
        Assert.Throws<ArgumentOutOfRangeException>(() => scaled.Speed = -1);
    }
}
