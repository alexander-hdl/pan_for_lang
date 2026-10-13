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
}
