using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Evaluation.Pipeline;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// <see cref="TargetAligner"/> и <see cref="WordReference"/> на искусственных входах: отрыв как альтернатива геометрии
/// (A03), цена отрыва по месту, ожидаемый разрыв, обратный проход и рост стоимости при сдвиге границы.
/// </summary>
public class WordAlignerTests
{
    private static readonly ScoringConfig Config = Shipped.Config;
    private static readonly SavitzkyGolayFilter Filter = new(Config.Smoothing.Window, Config.Smoothing.Order);

    private static WordReference Reference(string text = "at") => WordReference.Build(Tester.Word(text), Config, Filter);

    private static WordReference Reference(StyleManifest style, params string[] symbols)
    {
        WordTemplate word = WordTemplateBuilder.Build(style, [.. symbols.Select(Shipped.Glyph)]);
        return WordReference.Build(word, Config, Filter);
    }

    private static Vec2[] Slice(WordReference r, int from, int to) => r.Pos[from..(to + 1)];

    private static AlignProblem Problem(WordReference r, params Vec2[][] strokes)
    {
        int total = strokes.Sum(s => s.Length);
        var pos = new Vec2[total];
        var dir = new Vec2[total];
        var lift = new bool[total];
        int g = 0;
        for (int s = 0; s < strokes.Length; s++)
        {
            Vec2[] directions = PolylineToolsDirections(strokes[s]);
            for (int k = 0; k < strokes[s].Length; k++)
            {
                pos[g] = strokes[s][k];
                dir[g] = directions[k];
                lift[g] = s > 0 && k == 0;
                g++;
            }
        }

        return new AlignProblem
        {
            InkPos = pos,
            InkDir = dir,
            LiftBefore = lift,
            TplPos = r.Pos,
            TplDir = r.Dir,
            Zone = r.Zone,
            SkipPrefix = r.SkipPrefix,
            Intervals = [.. r.Intervals.Select(i => (i.Lo, i.Hi, i.Expected))],
            OpenBeginMax = r.OpenBeginMax,
            OpenEndMin = r.OpenEndMin,
        };
    }

    private static Vec2[] PolylineToolsDirections(Vec2[] points)
    {
        var result = new Vec2[points.Length];
        DirectionFeatures.FromPolyline(points, result);
        return result;
    }

    // ---------------------------------- эталон слова ----------------------------------

    [Fact]
    public void The_reference_marks_bodies_joins_and_the_open_ends()
    {
        WordReference r = Reference();

        Assert.Equal(r.Pos.Length, r.Zone.Length);
        Assert.Equal(r.Pos.Length + 1, r.SkipPrefix.Length);
        Assert.True(0 < r.BodyStart[0] && r.BodyStart[0] < r.BodyEnd[0] && r.BodyEnd[0] < r.BodyStart[1] && r.BodyStart[1] < r.BodyEnd[1] && r.BodyEnd[1] < r.Count - 1);
        Assert.Equal(r.BodyStart[0], r.OpenBeginMax);
        Assert.Equal(r.BodyEnd[1], r.OpenEndMin);
        JoinInterval join = Assert.Single(r.Intervals);
        Assert.Equal(0, join.Join);
        Assert.Equal(r.BodyEnd[0], join.Lo);
        Assert.Equal(r.BodyStart[1], join.Hi);
        Assert.False(join.Expected);
        Assert.Equal(ZoneKind.LeadIn, r.Zone[0]);
        Assert.Equal(ZoneKind.LeadOut, r.Zone[^1]);
        Assert.All(Enumerable.Range(join.Lo + 1, join.Hi - join.Lo - 1), j => Assert.Equal(ZoneKind.Join, r.Zone[j]));
        Assert.Equal(ZoneKind.Body, r.Zone[join.Lo]);
        Assert.Equal(ZoneKind.Body, r.Zone[join.Hi]);
        Assert.Equal(1.0, r.SkipPrefix[r.BodyEnd[0] + 1] - r.SkipPrefix[r.BodyEnd[0]]);
        Assert.Equal(0.0, r.SkipPrefix[join.Hi] - r.SkipPrefix[join.Lo + 1]);
    }

    [Fact]
    public void The_stem_of_t_retraces_the_join_and_the_slot_is_anchored_to_the_stem()
    {
        WordReference r = Reference();

        Assert.Contains(r.Retrace, x => x.Letter == 0 && !x.StartsBody); // a: ствол вниз по подъёму своего тела
        WordRetrace retrace = Assert.Single(r.Retrace, x => x.Letter == 1);
        Assert.True(retrace.StartsBody);
        Assert.NotNull(retrace.RefShare);
        Assert.True(retrace.RefShare >= 0.8, "the stem top lies on the join");
        WordSlot slot = Assert.Single(r.Slots);
        Assert.Equal(1, slot.Letter);
        Assert.Equal("bar", slot.Slot.Id);
        Assert.InRange(slot.AnchorIndex, r.BodyStart[1], r.BodyEnd[1]);
    }

    [Fact]
    public void Letter_glyphs_keep_the_approach_only_at_the_start_and_the_exit_only_at_the_end_and_have_no_deferred_strokes()
    {
        WordReference r = Reference("tat");

        Assert.NotNull(r.LetterGlyphs[0].LeadIn);
        Assert.Null(r.LetterGlyphs[0].LeadOut);
        Assert.Null(r.LetterGlyphs[1].LeadIn);
        Assert.Null(r.LetterGlyphs[1].LeadOut);
        Assert.Null(r.LetterGlyphs[2].LeadIn);
        Assert.NotNull(r.LetterGlyphs[2].LeadOut);
        Assert.All(r.LetterGlyphs, g => Assert.Empty(g.Deferred));
    }

    [Fact]
    public void A_break_between_letters_becomes_an_expected_connector()
    {
        StyleManifest style = Shipped.Style with
        {
            Joins = new JoinPolicy([new JoinRule(null, ExitClass.Base, [], true, JoinShape.Break, false, new Dictionary<string, double>())], EntryClass.UpTall),
        };

        WordReference r = Reference(style, "i", "t");

        JoinInterval join = Assert.Single(r.Intervals);
        Assert.True(join.Expected);
        Assert.Equal(0, join.Join);
        Assert.All(Enumerable.Range(join.Lo + 1, join.Hi - join.Lo - 1), j => Assert.Equal(ZoneKind.Connector, r.Zone[j]));
    }

    // ---------------------------------- выравнивание ----------------------------------

    [Fact]
    public void The_template_aligned_with_itself_costs_nothing_and_walks_the_diagonal()
    {
        WordReference r = Reference();

        Alignment alignment = TargetAligner.Align(Problem(r, r.Pos), Config);

        Assert.True(alignment.Forward.BestCost < 1e-9);
        Assert.Empty(alignment.Lifts);
        Assert.All(alignment.Path, c => Assert.Equal(c.I, c.J));
        Assert.Equal(r.Count - 1, alignment.Forward.BestEnd);
    }

    [Fact]
    public void A_lift_on_the_join_is_an_alternative_to_its_geometry_priced_by_place()
    {
        WordReference r = Reference();
        JoinInterval join = r.Intervals[0];

        // Тело a, отрыв, тело t сверху: геометрии соединения в рукописи нет.
        Alignment alignment = TargetAligner.Align(Problem(r, Slice(r, 0, join.Lo), Slice(r, join.Hi, r.Count - 1)), Config);

        LiftEvent lift = Assert.Single(alignment.Lifts);
        Assert.Equal(0, lift.Interval);
        Assert.Equal(join.Lo, lift.FromJ);
        Assert.Equal(join.Hi, lift.ToJ);
        Assert.InRange(alignment.Forward.BestCost, Config.Word.Aligner.LiftJoinPenalty, Config.Word.Aligner.LiftJoinPenalty + 0.2); // + направления у начала нового штриха
        Assert.Equal(join.Lo, alignment.InkOfTemplate[join.Lo] is var i && i >= 0 ? alignment.Path.First(c => c.I == i).J : -1);
    }

    [Fact]
    public void A_lift_inside_a_body_is_priced_higher_than_a_lift_on_the_join()
    {
        WordReference r = Reference();
        JoinInterval join = r.Intervals[0];
        int cut = (r.BodyStart[0] + r.BodyEnd[0]) / 2;

        Alignment onJoin = TargetAligner.Align(Problem(r, Slice(r, 0, join.Lo), Slice(r, join.Hi, r.Count - 1)), Config);
        Alignment inLetter = TargetAligner.Align(Problem(r, Slice(r, 0, cut), Slice(r, cut, r.Count - 1)), Config);

        LiftEvent lift = Assert.Single(inLetter.Lifts);
        Assert.Equal(-1, lift.Interval);
        Assert.InRange(inLetter.Forward.BestCost, Config.Word.Aligner.LiftInLetterPenalty, Config.Word.Aligner.LiftInLetterPenalty + 0.2);
        Assert.True(inLetter.Forward.BestCost > 2 * onJoin.Forward.BestCost);
    }

    [Fact]
    public void Skipping_body_points_with_a_lift_is_not_free()
    {
        WordReference r = Reference();
        int from = r.BodyStart[0] + 20;
        int to = from + 30;

        // Пропущен кусок тела a: отрыв внутри тела с ценой за каждую пропущенную точку.
        Alignment alignment = TargetAligner.Align(Problem(r, Slice(r, 0, from), Slice(r, to, r.Count - 1)), Config);

        LiftEvent lift = Assert.Single(alignment.Lifts);
        Assert.Equal(-1, lift.Interval);
        Assert.True(alignment.Forward.BestCost >= Config.Word.Aligner.LiftInLetterPenalty, "a lift inside a body always pays its price");
    }

    [Fact]
    public void An_expected_break_is_a_free_lift()
    {
        StyleManifest style = Shipped.Style with
        {
            Joins = new JoinPolicy([new JoinRule(null, ExitClass.Base, [], true, JoinShape.Break, false, new Dictionary<string, double>())], EntryClass.UpTall),
        };
        WordReference r = Reference(style, "i", "t");
        JoinInterval join = r.Intervals[0];

        Alignment alignment = TargetAligner.Align(Problem(r, Slice(r, 0, join.Lo), Slice(r, join.Hi, r.Count - 1)), Config);

        Assert.Single(alignment.Lifts);
        Assert.True(alignment.Forward.BestCost < 1e-9, "the lift is expected: no penalty");
    }

    [Fact]
    public void A_break_written_continuously_costs_a_flat_price_per_point()
    {
        StyleManifest style = Shipped.Style with
        {
            Joins = new JoinPolicy([new JoinRule(null, ExitClass.Base, [], true, JoinShape.Break, false, new Dictionary<string, double>())], EntryClass.UpTall),
        };
        WordReference r = Reference(style, "i", "t");
        JoinInterval join = r.Intervals[0];
        Vec2 from = r.Pos[join.Lo];
        Vec2 to = r.Pos[join.Hi];
        int bridge = join.Hi - join.Lo - 1;

        // Слитно, но не по прямой (изгиб в сторону): геометрия мостика не судится, цена одинаковая на каждую точку.
        Vec2[] drawn = [.. Enumerable.Range(1, bridge).Select(k => Vec2.Lerp(from, to, (double)k / (bridge + 1)) + new Vec2(0.0, 0.3 * Math.Sin(Math.PI * k / (bridge + 1))))];
        Vec2[] ink = [.. Slice(r, 0, join.Lo), .. drawn, .. Slice(r, join.Hi, r.Count - 1)];
        Alignment alignment = TargetAligner.Align(Problem(r, ink), Config);

        Assert.Empty(alignment.Lifts);
        Assert.InRange(alignment.Forward.BestCost, bridge * Config.Word.Aligner.ContinuousBreakCostPerPoint, (bridge * Config.Word.Aligner.ContinuousBreakCostPerPoint) + 0.05);
    }

    // ---------------------------------- обратный проход и уверенность границ ----------------------------------

    [Fact]
    public void The_backward_pass_gives_the_same_best_cost_and_no_rise_on_the_best_path()
    {
        WordReference r = Reference();
        JoinInterval join = r.Intervals[0];
        Vec2[][] inks =
        [
            [.. r.Pos],
            [.. Slice(r, 0, join.Lo), .. Slice(r, join.Hi, r.Count - 1)],
        ];
        foreach (Vec2[] ink in inks)
        {
            Vec2[] noisy = [.. ink.Select((p, i) => p + new Vec2(0.01 * Math.Sin(i), 0.01 * Math.Cos(2 * i)))];
            AlignProblem problem = inks[1] == ink ? Problem(r, Slice(r, 0, join.Lo), Slice(r, join.Hi, r.Count - 1)) : Problem(r, noisy);
            Alignment alignment = TargetAligner.Align(problem, Config);
            var backward = new DpTable(problem.Reversed(), Config.Word.Aligner, Config.Dtw);

            Assert.Equal(alignment.Forward.BestCost, backward.BestCost, 9);
            foreach (PathCell cell in alignment.Path)
            {
                Assert.Equal(0.0, TargetAligner.Rise(alignment, backward, cell.I, cell.J), 8);
            }
        }
    }

    [Fact]
    public void Moving_a_boundary_off_the_path_raises_the_cost()
    {
        WordReference r = Reference();
        AlignProblem problem = Problem(r, r.Pos);
        Alignment alignment = TargetAligner.Align(problem, Config);
        var backward = new DpTable(problem.Reversed(), Config.Word.Aligner, Config.Dtw);
        int j = r.BodyStart[1];
        int shift = (int)Math.Round(Config.Word.Boundaries.ShiftU / Config.ResampleStep);

        double before = TargetAligner.Rise(alignment, backward, j - shift, j);
        double after = TargetAligner.Rise(alignment, backward, j + shift, j);

        // У излома (верх t) обе стороны дороги: рост стоимости заметно выше порога уверенности.
        Assert.True(before >= Config.Word.Boundaries.MinRiseCost);
        Assert.True(after >= Config.Word.Boundaries.MinRiseCost);
        Assert.Equal(double.PositiveInfinity, TargetAligner.Rise(alignment, backward, -1, j));
    }

    [Fact]
    public void The_reversed_problem_swaps_open_ends_and_maps_intervals()
    {
        WordReference r = Reference();
        AlignProblem problem = Problem(r, Slice(r, 0, r.Intervals[0].Lo), Slice(r, r.Intervals[0].Hi, r.Count - 1));

        AlignProblem back = problem.Reversed();

        Assert.Equal(r.Count - 1 - r.OpenEndMin, back.OpenBeginMax);
        Assert.Equal(r.Count - 1 - r.OpenBeginMax, back.OpenEndMin);
        Assert.Equal((r.Count - 1 - r.Intervals[0].Hi, r.Count - 1 - r.Intervals[0].Lo, false), back.Intervals[0]);
        Assert.Equal(problem.LiftBefore.Count(l => l), back.LiftBefore.Count(l => l));
        Assert.Equal(problem.InkPos[0], back.InkPos[^1]);
        Assert.Equal(problem.SkipPrefix[^1], back.SkipPrefix[^1]);
    }
}
