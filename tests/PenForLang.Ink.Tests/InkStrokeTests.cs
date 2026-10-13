namespace PenForLang.Ink.Tests;

public class InkStrokeTests
{
    [Fact]
    public void Duration_is_difference_between_last_and_first_sample()
    {
        var stroke = new InkStroke([
            new PenSample(0, 0, 0.5f, 1_000),
            new PenSample(1, 1, 0.6f, 6_000),
            new PenSample(2, 2, 0.4f, 11_000),
        ]);

        Assert.Equal(3, stroke.Samples.Count);
        Assert.Equal(10_000, stroke.DurationMicros);
    }

    [Fact]
    public void Empty_and_single_sample_strokes_have_zero_duration()
    {
        Assert.Equal(0, new InkStroke([]).DurationMicros);
        Assert.Equal(0, new InkStroke([new PenSample(0, 0, 1f, 5)]).DurationMicros);
    }

    [Fact]
    public void Samples_out_of_time_order_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new InkStroke([
            new PenSample(0, 0, 0.5f, 10),
            new PenSample(1, 1, 0.5f, 9),
        ]));
    }
}
