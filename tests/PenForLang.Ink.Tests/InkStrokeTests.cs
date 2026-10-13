using System.Collections.Immutable;
using PenForLang.Core;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class InkStrokeTests
{
    [Fact]
    public void Duration_is_difference_between_last_and_first_sample()
    {
        var stroke = new InkStroke(0, [Sample(1_000, 0, 0), Sample(6_000, 1, 1), Sample(11_000, 2, 2)]);

        Assert.Equal(3, stroke.Samples.Length);
        Assert.Equal(10_000, stroke.DurationUs);
    }

    [Fact]
    public void Empty_default_and_single_sample_strokes_have_zero_duration()
    {
        Assert.Equal(0, new InkStroke(0, []).DurationUs);
        Assert.Equal(0, new InkStroke(1, default).DurationUs);
        Assert.True(new InkStroke(1, default).Samples.IsEmpty);
        Assert.Equal(0, new InkStroke(0, [Sample(5, 0, 0)]).DurationUs);
    }

    [Fact]
    public void Samples_out_of_time_order_are_rejected_but_equal_times_are_fine()
    {
        Assert.Throws<ArgumentException>(() => new InkStroke(0, [Sample(10, 0, 0), Sample(9, 1, 1)]));
        _ = new InkStroke(0, [Sample(10, 0, 0), Sample(10, 1, 1)]);
    }

    [Fact]
    public void Negative_index_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InkStroke(-1, ImmutableArray<PenSample>.Empty));
    }

    [Fact]
    public void Sample_flag_helpers_reflect_phase_buttons_and_flags()
    {
        Assert.False(Sample(0, 0, 0, PenPhase.Hover).IsContact);
        Assert.True(Sample(0, 0, 0, PenPhase.Up).IsContact);
        Assert.True(Sample(0, 0, 0, buttons: PenButtons.Barrel).IsAuxiliary);
        Assert.True(Sample(0, 0, 0, buttons: PenButtons.Eraser).IsAuxiliary);
        Assert.False(Sample(0, 0, 0).IsAuxiliary);
        Assert.True(Sample(0, 0, 0, flags: SampleFlags.Clipped | SampleFlags.Coalesced).IsClipped);
    }

    [Fact]
    public void GuideFrame_validates_its_numbers()
    {
        Assert.Throws<ArgumentException>(() => new GuideFrame(0, 0, 0, 7.5, 3, null, new StyleId("s")));
        Assert.Throws<ArgumentException>(() => new GuideFrame(0, 0, 10, -1, 3, null, new StyleId("s")));
        Assert.Throws<ArgumentException>(() => new GuideFrame(double.NaN, 0, 10, 7.5, 3, null, new StyleId("s")));
        Assert.Throws<ArgumentException>(() => new GuideFrame(0, 0, 10, 7.5, 3, double.PositiveInfinity, new StyleId("s")));
        Assert.Equal(15, new GuideFrame(0, 0, 112.5, 7.5, 3, null, new StyleId("s")).XHeightMm);
    }

    [Fact]
    public void InkEvent_equality_ignores_dictionary_order_and_treats_null_and_empty_data_as_equal()
    {
        InkEvent a = Event(5, "x", ("a", "1"), ("b", "2"));
        InkEvent b = Event(5, "x", ("b", "2"), ("a", "1"));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Event(5, "x", ("a", "1")));
        Assert.NotEqual(a, Event(5, "x", ("a", "1"), ("b", "3")));
        Assert.Equal(new InkEvent(1, "n"), new InkEvent(1, "n", new Dictionary<string, string>()));
        Assert.NotEqual(new InkEvent(1, "n"), new InkEvent(2, "n"));
    }
}
