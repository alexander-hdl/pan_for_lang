namespace PenForLang.Core.Tests;

public class Pcg32Tests
{
    [Fact]
    public void Matches_reference_sequence_for_seed_42_stream_54()
    {
        // Эталонный вывод pcg32-demo (pcg-c-basic): seed 42, sequence 54.
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        var rng = new Pcg32(42, 54);

        foreach (uint value in expected)
        {
            Assert.Equal(value, rng.NextUInt32());
        }
    }

    [Fact]
    public void Same_seed_gives_same_sequence_and_different_seed_differs()
    {
        var a = new Pcg32(7);
        var b = new Pcg32(7);
        var c = new Pcg32(8);

        uint[] first = Enumerable.Range(0, 20).Select(_ => a.NextUInt32()).ToArray();
        uint[] second = Enumerable.Range(0, 20).Select(_ => b.NextUInt32()).ToArray();
        uint[] third = Enumerable.Range(0, 20).Select(_ => c.NextUInt32()).ToArray();

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void NextInt32_stays_in_range_and_NextDouble_in_unit_interval()
    {
        var rng = new Pcg32(123);

        for (int i = 0; i < 1000; i++)
        {
            int n = rng.NextInt32(7);
            Assert.InRange(n, 0, 6);

            double d = rng.NextDouble();
            Assert.InRange(d, 0.0, 1.0);
            Assert.NotEqual(1.0, d);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NextInt32_rejects_non_positive_bound(int bound)
    {
        var rng = new Pcg32(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt32(bound));
    }
}
