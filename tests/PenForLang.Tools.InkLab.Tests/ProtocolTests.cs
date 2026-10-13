using PenForLang.Tools.InkLab.Protocol;

namespace PenForLang.Tools.InkLab.Tests;

public class ProtocolTests
{
    [Fact]
    public void Default_protocol_has_30_a_30_i_30_t_and_20_at_in_that_order()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault();

        Assert.Equal(110, protocol.Prompts.Length);
        Assert.Equal(30, protocol.Prompts.Count(p => p.Target == "glyph:a"));
        Assert.Equal(30, protocol.Prompts.Count(p => p.Target == "glyph:i"));
        Assert.Equal(30, protocol.Prompts.Count(p => p.Target == "glyph:t"));
        Assert.Equal(20, protocol.Prompts.Count(p => p.Target == "word:at"));
        Assert.Equal("a-001", protocol.Prompts[0].Id);
        Assert.Equal("i-001", protocol.Prompts[30].Id);
        Assert.Equal("t-001", protocol.Prompts[60].Id);
        Assert.Equal("at-lift-001", protocol.Prompts[90].Id);
        Assert.Equal(protocol.Prompts.Length, protocol.Prompts.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Word_prompts_are_split_evenly_between_lift_and_crossbar_variants()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault();
        var words = protocol.Prompts.Where(p => p.IsWord).ToList();

        Assert.Equal(10, words.Count(p => p.Variant == PromptVariants.Lift));
        Assert.Equal(10, words.Count(p => p.Variant == PromptVariants.Crossbar));
        Assert.Equal("at-crossbar-001", words[1].Id);
        Assert.Contains("ОТРЫВОМ", words[0].Instruction, StringComparison.Ordinal);
        Assert.Equal("word/at", words[0].StimulusKey);
        Assert.All(protocol.Prompts.Where(p => !p.IsWord), p => Assert.Null(p.Variant));
    }

    [Fact]
    public void Create_rejects_a_target_without_kind_and_text()
    {
        Assert.Throws<ArgumentException>(() => RecordingProtocol.Create("p", [new ProtocolStep("a", 1)]));
        Assert.Throws<ArgumentException>(() => RecordingProtocol.Create("p", [new ProtocolStep("glyph:", 1)]));
    }

    [Fact]
    public void Session_starts_at_the_first_prompt_and_advances_on_done_and_skip()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault(letterRepeats: 2, wordRepeats: 2);
        var progress = new ProtocolProgress { ProtocolId = protocol.Id, Subject = new SubjectInfo("s1") };
        var session = new ProtocolSession(protocol, progress);

        Assert.Equal("a-001", session.Current!.Id);
        session.MarkDone("a-001.pfl.jsonl.gz", restarts: 1);
        Assert.Equal("a-002", session.Current!.Id);
        session.Skip();
        Assert.Equal("i-001", session.Current!.Id);

        Assert.Equal(1, session.DoneCount);
        Assert.Equal(1, session.SkippedCount);
        Assert.Equal(PromptStatus.Done, progress.StatusOf("a-001"));
        Assert.Equal(PromptStatus.Skipped, progress.StatusOf("a-002"));
        Assert.Equal(1, progress.Prompts["a-001"].Restarts);
        Assert.Equal("i-001", progress.Current);
    }

    [Fact]
    public void Back_returns_to_the_previous_prompt_and_stops_at_the_first()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault(letterRepeats: 2, wordRepeats: 0);
        var session = new ProtocolSession(protocol, new ProtocolProgress { ProtocolId = protocol.Id });

        Assert.False(session.Back());
        session.MarkDone("a.gz", 0);
        Assert.True(session.Back());
        Assert.Equal("a-001", session.Current!.Id);
        Assert.False(session.Back());
    }

    [Fact]
    public void Redoing_a_done_prompt_keeps_it_done_and_moves_on_to_the_next_pending()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault(letterRepeats: 3, wordRepeats: 0);
        var progress = new ProtocolProgress { ProtocolId = protocol.Id };
        var session = new ProtocolSession(protocol, progress);
        session.MarkDone("a1.gz", 0);
        session.MarkDone("a2.gz", 0);

        Assert.True(session.Back());
        session.MarkDone("a2-new.gz", 2);

        Assert.Equal("a-003", session.Current!.Id);
        Assert.Equal("a2-new.gz", progress.Prompts["a-002"].File);
        Assert.Equal(2, progress.Prompts["a-002"].Takes);
    }

    [Fact]
    public void Resume_continues_from_the_saved_position_even_when_it_is_a_pending_prompt_after_skips()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault(letterRepeats: 3, wordRepeats: 0);
        var progress = new ProtocolProgress { ProtocolId = protocol.Id };
        var first = new ProtocolSession(protocol, progress);
        first.MarkDone("a1.gz", 0);
        first.Skip();

        // Новый сеанс по тому же прогрессу (после сохранения в JSON и чтения обратно).
        ProtocolProgress reloaded = ProtocolProgress.FromJson(progress.ToJson());
        var resumed = new ProtocolSession(protocol, reloaded);

        Assert.Equal("a-003", resumed.Current!.Id);
        Assert.Equal(1, resumed.DoneCount);
        Assert.Equal(1, resumed.SkippedCount);
    }

    [Fact]
    public void Protocol_completes_when_nothing_is_pending_and_skipped_prompts_are_offered_once()
    {
        RecordingProtocol protocol = RecordingProtocol.Create("p", [new ProtocolStep("glyph:a", 2)]);
        var progress = new ProtocolProgress { ProtocolId = protocol.Id };
        var session = new ProtocolSession(protocol, progress);

        session.Skip(); // a-001 пропущена
        session.MarkDone("a2.gz", 0);
        Assert.Equal("a-001", session.Current!.Id); // пропущенная предлагается ещё раз

        session.MarkDone("a1.gz", 0);
        Assert.True(session.IsComplete);
        Assert.Null(session.Current);
        Assert.Equal(2, session.DoneCount);
        Assert.Throws<InvalidOperationException>(() => session.MarkDone("x.gz", 0));
    }

    [Fact]
    public void Skipping_the_only_remaining_prompt_completes_instead_of_looping()
    {
        RecordingProtocol protocol = RecordingProtocol.Create("p", [new ProtocolStep("glyph:a", 1)]);
        var session = new ProtocolSession(protocol, new ProtocolProgress { ProtocolId = protocol.Id });

        session.Skip();

        Assert.True(session.IsComplete);
        Assert.Equal(1, session.SkippedCount);
    }

    [Fact]
    public void Progress_json_round_trips_subject_and_prompts()
    {
        var progress = new ProtocolProgress
        {
            ProtocolId = "inklab-v0.9",
            Subject = new SubjectInfo("adult-1", SubjectInfo.Left, 41, 7),
            Current = "a-003",
        };
        progress.Entry("a-001").Status = PromptStatus.Done;
        progress.Entry("a-001").File = "a-001-x.pfl.jsonl.gz";
        progress.Entry("a-002").Status = PromptStatus.Skipped;

        ProtocolProgress copy = ProtocolProgress.FromJson(progress.ToJson());

        Assert.Equal(progress.Subject, copy.Subject);
        Assert.Equal("a-003", copy.Current);
        Assert.Equal(PromptStatus.Done, copy.StatusOf("a-001"));
        Assert.Equal(PromptStatus.Skipped, copy.StatusOf("a-002"));
        Assert.Equal(PromptStatus.Pending, copy.StatusOf("a-999"));
        Assert.Throws<FormatException>(() => ProtocolProgress.FromJson("{\"schema\":\"other/1\"}"));
        Assert.Throws<FormatException>(() => ProtocolProgress.FromJson("not json"));
    }

    [Theory]
    [InlineData("adult-1", "adult-1")]
    [InlineData("A B/C", "A_B_C")]
    [InlineData("  x  ", "x")]
    public void Subject_ids_become_safe_folder_names(string id, string expected)
    {
        Assert.Equal(expected, new SubjectInfo(id).FolderName);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("ok", true)]
    public void Subject_id_validity(string? id, bool valid)
    {
        Assert.Equal(valid, SubjectInfo.IsValidId(id));
    }
}
