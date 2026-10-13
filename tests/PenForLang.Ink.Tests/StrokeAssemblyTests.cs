using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class StrokeAssemblyTests
{
    [Fact]
    public void Down_move_up_form_a_stroke_and_hover_is_ignored()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(1, 0, 0, PenPhase.Hover),
            Sample(2, 1, 1, PenPhase.Down),
            Sample(3, 2, 2),
            Sample(4, 3, 3, PenPhase.Up),
            Sample(5, 4, 4, PenPhase.Hover),
        ]);

        var strokes = assembler.ToStrokes();

        Assert.Single(strokes);
        Assert.Equal(0, strokes[0].Index);
        Assert.Equal([2L, 3L, 4L], strokes[0].Samples.Select(s => s.TimeUs));
        Assert.Equal(0, assembler.OpenStrokeCount);
    }

    [Fact]
    public void Move_without_down_is_dropped_and_unterminated_stroke_stays_open()
    {
        var assembler = new StrokeAssembler();
        assembler.Add(Sample(1, 0, 0));
        assembler.Add(Sample(2, 0, 0, PenPhase.Up));
        Assert.Equal(0, assembler.StrokeCount);

        assembler.Add(Sample(3, 0, 0, PenPhase.Down));
        assembler.Add(Sample(4, 1, 1));

        Assert.Equal(1, assembler.OpenStrokeCount);
        Assert.Equal(2, assembler.ToStrokes()[0].Samples.Length);

        assembler.Release(3);
        assembler.Add(Sample(5, 2, 2));
        Assert.Equal(2, assembler.ToStrokes()[0].Samples.Length);
    }

    [Fact]
    public void Two_pointers_interleave_into_separate_strokes_numbered_by_touch_order()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(1, 0, 0, PenPhase.Down, pid: 1),
            Sample(2, 5, 5, PenPhase.Down, pid: 2),
            Sample(3, 1, 1, pid: 1),
            Sample(4, 6, 6, PenPhase.Up, pid: 2),
            Sample(5, 2, 2, PenPhase.Up, pid: 1),
        ]);

        var strokes = assembler.ToStrokes();

        Assert.Equal([1u, 2u], strokes.Select(s => s.Samples[0].PointerId));
        Assert.Equal(3, strokes[0].Samples.Length);
        Assert.Equal(2, strokes[1].Samples.Length);
    }

    [Fact]
    public void Second_down_without_up_starts_a_new_stroke()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([Sample(1, 0, 0, PenPhase.Down), Sample(2, 1, 1), Sample(3, 5, 5, PenPhase.Down), Sample(4, 6, 6, PenPhase.Up)]);

        Assert.Equal([2, 2], assembler.ToStrokes().Select(s => s.Samples.Length));
    }

    [Fact]
    public void Down_and_up_only_gives_a_two_point_stroke_and_no_input_gives_no_strokes()
    {
        var assembler = new StrokeAssembler();
        Assert.Empty(assembler.ToStrokes());
        assembler.Add([Sample(1, 0, 0, PenPhase.Down), Sample(1, 0, 0, PenPhase.Up)]);
        Assert.Equal(2, assembler.ToStrokes()[0].Samples.Length);
    }

    [Fact]
    public void Ownership_Down_inside_makes_the_pointer_owned_and_marks_points_outside_as_clipped_until_Up()
    {
        var ownership = new PointerOwnership(100, 50);
        var output = new List<PenSample>();
        PenSample[] input =
        [
            Sample(1, 10, 10, PenPhase.Down),
            Sample(2, -3.5, 20),
            Sample(3, 120, 20),
            Sample(4, 50, 60),
            Sample(5, 50, 25, PenPhase.Up),
            Sample(6, 50, 25), // после Up указатель не принадлежит холсту
        ];

        foreach (PenSample sample in input)
        {
            if (ownership.TryProcess(sample, out PenSample result))
            {
                output.Add(result);
            }
        }

        Assert.Equal([1L, 2L, 3L, 4L, 5L], output.Select(s => s.TimeUs));
        Assert.Equal([false, true, true, true, false], output.Select(s => s.IsClipped));
        Assert.Equal(0, ownership.OwnedCount);
    }

    [Fact]
    public void Ownership_Down_outside_is_ignored_with_all_following_points_even_when_they_enter_the_canvas()
    {
        var ownership = new PointerOwnership(100, 50);

        Assert.False(ownership.TryProcess(Sample(1, -5, 10, PenPhase.Down), out _));
        Assert.False(ownership.TryProcess(Sample(2, 10, 10), out _));
        Assert.False(ownership.TryProcess(Sample(3, 10, 10, PenPhase.Up), out _));
        Assert.False(ownership.IsOwned(3));
    }

    [Fact]
    public void Ownership_is_per_pointer_id_and_release_drops_ownership()
    {
        var ownership = new PointerOwnership(100, 50);

        Assert.True(ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down, pid: 1), out _));
        Assert.False(ownership.TryProcess(Sample(2, 10, 10, pid: 2), out _));
        Assert.True(ownership.TryProcess(Sample(3, 10, 10, pid: 1), out _));

        ownership.Release(1);
        Assert.False(ownership.TryProcess(Sample(4, 10, 10, pid: 1), out _));

        ownership.TryProcess(Sample(5, 10, 10, PenPhase.Down, pid: 7), out _);
        ownership.Reset();
        Assert.Equal(0, ownership.OwnedCount);
    }

    [Fact]
    public void Ownership_hover_passes_only_inside_and_border_is_inclusive()
    {
        var ownership = new PointerOwnership(100, 50);

        Assert.True(ownership.TryProcess(Sample(1, 100, 50, PenPhase.Hover), out PenSample inside));
        Assert.False(inside.IsClipped);
        Assert.False(ownership.TryProcess(Sample(2, 100.01, 50, PenPhase.Hover), out _));
    }

    [Fact]
    public void Ownership_keeps_existing_flags_when_adding_clipped()
    {
        var ownership = new PointerOwnership(100, 50);
        ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down), out _);

        ownership.TryProcess(Sample(2, 500, 10, flags: SampleFlags.Coalesced), out PenSample result);

        Assert.Equal(SampleFlags.Coalesced | SampleFlags.Clipped, result.Flags);
    }

    [Fact]
    public void Ownership_rejects_a_degenerate_canvas()
    {
        Assert.Throws<ArgumentException>(() => new PointerOwnership(0, 10));
        Assert.Throws<ArgumentException>(() => new PointerOwnership(10, double.NaN));
    }

    [Fact]
    public void Non_monotonic_points_are_dropped_and_counted_and_assembly_never_throws()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(100, 0, 0, PenPhase.Down),
            Sample(200, 1, 1),
            Sample(150, 99, 99), // назад во времени
            Sample(200, 2, 2), // равное время допустимо
            Sample(300, 3, 3, PenPhase.Up),
        ]);

        var strokes = assembler.ToStrokes();

        Assert.Equal(1, assembler.DroppedNonMonotonicCount);
        Assert.Equal([100L, 200L, 200L, 300L], strokes[0].Samples.Select(s => s.TimeUs));
        Assert.DoesNotContain(strokes[0].Samples, s => s.X == 99);
    }

    [Fact]
    public void Non_monotonic_time_is_tracked_per_pointer()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(500, 0, 0, PenPhase.Down, pid: 1),
            Sample(100, 5, 5, PenPhase.Down, pid: 2), // другой указатель: свои часы не сравниваются с чужими
            Sample(200, 6, 6, pid: 2),
            Sample(400, 1, 1, pid: 1), // назад для указателя 1
        ]);

        Assert.Equal(1, assembler.DroppedNonMonotonicCount);
        Assert.Equal([1, 2], assembler.ToStrokes().Select(s => s.Samples.Length));
    }

    [Fact]
    public void A_non_monotonic_Up_is_dropped_and_leaves_the_stroke_open()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([Sample(100, 0, 0, PenPhase.Down), Sample(50, 1, 1, PenPhase.Up)]);

        Assert.Equal(1, assembler.OpenStrokeCount);
        Assert.Equal(1, assembler.DroppedNonMonotonicCount);
    }

    [Fact]
    public void Barrel_button_in_the_middle_of_a_stroke_splits_it()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(1, 0, 0, PenPhase.Down),
            Sample(2, 1, 0),
            Sample(3, 2, 0, buttons: PenButtons.Barrel),
            Sample(4, 3, 0, buttons: PenButtons.Barrel),
            Sample(5, 4, 0),
            Sample(6, 5, 0, PenPhase.Up),
        ]);

        var strokes = assembler.ToStrokes();

        Assert.Equal([2, 2, 2], strokes.Select(s => s.Samples.Length));
        Assert.Equal([0, 1, 2], strokes.Select(s => s.Index));
        Assert.Equal([false, true, false], strokes.Select(s => s.Samples[0].IsAuxiliary));
        Assert.Equal(0, assembler.OpenStrokeCount);

        // В линейке вспомогательный штрих исчезает, а обычные остаются раздельными (нет рывка через разрыв).
        var attempt = new InkAttempt(strokes, Guides(), Device(), null, InkCompletion.Pause, 0, []);
        Assert.Equal([0, 2], RulerProjection.Project(attempt).Strokes.Select(s => s.Index));
    }

    [Fact]
    public void Eraser_stroke_started_by_Down_is_a_separate_auxiliary_stroke_and_Up_with_a_changed_state_ends_cleanly()
    {
        var assembler = new StrokeAssembler();
        assembler.Add([
            Sample(1, 0, 0, PenPhase.Down, buttons: PenButtons.Eraser),
            Sample(2, 1, 0, buttons: PenButtons.Eraser),
            Sample(3, 2, 0, PenPhase.Up),
        ]);

        var strokes = assembler.ToStrokes();

        Assert.Equal([2, 1], strokes.Select(s => s.Samples.Length));
        Assert.Equal(0, assembler.OpenStrokeCount);
    }

    [Fact]
    public void Assembler_scales_to_many_strokes_with_a_single_pass_materialisation()
    {
        var assembler = new StrokeAssembler();
        for (int i = 0; i < 2000; i++)
        {
            assembler.Add([Sample(i * 10L, i, 0, PenPhase.Down), Sample((i * 10L) + 5, i, 1, PenPhase.Up)]);
        }

        var strokes = assembler.ToStrokes();

        Assert.Equal(2000, strokes.Length);
        Assert.All(strokes, s => Assert.Equal(2, s.Samples.Length));
        Assert.Equal(1999, strokes[^1].Index);
    }

    [Fact]
    public void Ownership_hover_on_an_owned_pointer_is_an_implicit_Up()
    {
        var ownership = new PointerOwnership(100, 50);
        ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down), out _);
        ownership.TryProcess(Sample(2, 20, 10), out _);

        // Up потерян: приходит hover.
        Assert.True(ownership.TryProcess(Sample(3, 30, 10, PenPhase.Hover), out PenSample implicitUp));

        Assert.Equal(PenPhase.Up, implicitUp.Phase);
        Assert.Equal(30, implicitUp.X);
        Assert.False(ownership.IsOwned(3));

        // Дальше указатель в hover — обычный hover внутри холста; Move без Down не принимается.
        Assert.True(ownership.TryProcess(Sample(4, 31, 10, PenPhase.Hover), out PenSample hover));
        Assert.Equal(PenPhase.Hover, hover.Phase);
        Assert.False(ownership.TryProcess(Sample(5, 32, 10), out _));

        // В сборщике потерянный Up закрывает штрих.
        var assembler = new StrokeAssembler();
        assembler.Add(Sample(1, 10, 10, PenPhase.Down));
        assembler.Add(Sample(3, 30, 10, PenPhase.Up));
        Assert.Equal(0, assembler.OpenStrokeCount);
    }

    [Fact]
    public void Ownership_implicit_Up_outside_the_canvas_is_marked_clipped()
    {
        var ownership = new PointerOwnership(100, 50);
        ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down), out _);

        Assert.True(ownership.TryProcess(Sample(2, 500, 10, PenPhase.Hover), out PenSample implicitUp));

        Assert.Equal(PenPhase.Up, implicitUp.Phase);
        Assert.True(implicitUp.IsClipped);
    }

    [Fact]
    public void Ownership_double_Down_keeps_ownership_inside_and_drops_it_outside()
    {
        var ownership = new PointerOwnership(100, 50);
        Assert.True(ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down), out _));

        Assert.True(ownership.TryProcess(Sample(2, 20, 10, PenPhase.Down), out _));
        Assert.True(ownership.IsOwned(3));
        Assert.Equal(1, ownership.OwnedCount);

        Assert.False(ownership.TryProcess(Sample(3, 200, 10, PenPhase.Down), out _));
        Assert.False(ownership.IsOwned(3));
        Assert.False(ownership.TryProcess(Sample(4, 20, 10), out _));
    }

    [Fact]
    public void Ownership_SetSize_changes_clipping_and_keeps_owned_pointers()
    {
        var ownership = new PointerOwnership(100, 50);
        ownership.TryProcess(Sample(1, 10, 10, PenPhase.Down), out _);

        ownership.TryProcess(Sample(2, 150, 10), out PenSample before);
        ownership.SetSize(200, 100);
        ownership.TryProcess(Sample(3, 150, 10), out PenSample after);
        ownership.SetSurface(new PenSurface(5000, 5000, 120, 60));

        Assert.True(before.IsClipped);
        Assert.False(after.IsClipped);
        Assert.True(ownership.IsOwned(3));
        Assert.Equal(120, ownership.WidthPx);
        Assert.Equal(60, ownership.HeightPx);
        Assert.Throws<ArgumentException>(() => ownership.SetSize(0, 10));
        Assert.Equal(120, ownership.WidthPx);
    }
}
