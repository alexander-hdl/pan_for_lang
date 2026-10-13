using System.Text;

namespace PenForLang.Core.Tests;

public class InMemoryContentSourceTests
{
    private static InMemoryContentSource Sample() => new InMemoryContentSource()
        .Add("styles/zb/style.json", "{}")
        .Add("styles/zb/glyphs/a.json", "A")
        .Add("styles/zb-print/style.json", "P")
        .Add("config/main.json", [1, 2, 3]);

    [Fact]
    public void Open_returns_the_content_and_each_call_gives_an_independent_stream()
    {
        InMemoryContentSource source = Sample();

        using Stream first = source.Open("styles/zb/glyphs/a.json");
        using Stream second = source.Open("styles/zb/glyphs/a.json");
        Assert.Equal((byte)'A', first.ReadByte());
        Assert.Equal(-1, first.ReadByte());
        Assert.Equal((byte)'A', second.ReadByte());
    }

    [Fact]
    public void Text_is_stored_as_utf8_without_bom()
    {
        var source = new InMemoryContentSource().Add("t.txt", "Привет");

        using Stream stream = source.Open("t.txt");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal(new UTF8Encoding(false).GetBytes("Привет"), buffer.ToArray());
    }

    [Fact]
    public void Added_content_is_copied_so_later_mutation_of_the_source_array_does_not_leak()
    {
        byte[] bytes = [1, 2, 3];
        var source = new InMemoryContentSource().Add("a.bin", bytes);
        bytes[0] = 99;

        using Stream stream = source.Open("a.bin");
        Assert.Equal(1, stream.ReadByte());
    }

    [Fact]
    public void Streams_are_read_only()
    {
        using Stream stream = Sample().Open("config/main.json");
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void Open_of_a_missing_file_throws_FileNotFoundException_and_Exists_is_false()
    {
        InMemoryContentSource source = Sample();

        Assert.False(source.Exists("styles/zb/missing.json"));
        Assert.True(source.Exists("styles/zb/style.json"));
        Assert.Throws<FileNotFoundException>(() => source.Open("styles/zb/missing.json"));
    }

    [Fact]
    public void Paths_are_case_sensitive()
    {
        Assert.False(Sample().Exists("Styles/zb/style.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/abs/file.json")]
    [InlineData("C:/file.json")]
    [InlineData("a\\b.json")]
    [InlineData("a/../b.json")]
    [InlineData("a/./b.json")]
    [InlineData("a//b.json")]
    [InlineData("a/b/")]
    [InlineData("a/\nb")]
    [InlineData("a:b.json")]
    [InlineData("a/b.json:stream")]
    [InlineData("a/b:c/d.json")]
    [InlineData("a./b.json")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData("a /b.json")]
    public void Invalid_paths_are_rejected_everywhere(string path)
    {
        InMemoryContentSource source = Sample();

        Assert.Throws<ArgumentException>(() => source.Exists(path));
        Assert.Throws<ArgumentException>(() => source.Open(path));
        Assert.Throws<ArgumentException>(() => source.Add(path, "x"));
    }

    [Fact]
    public void Null_path_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Sample().Exists(null!));
    }

    [Fact]
    public void List_is_recursive_sorted_ordinal_and_respects_directory_boundaries()
    {
        InMemoryContentSource source = Sample();

        Assert.Equal(["styles/zb/glyphs/a.json", "styles/zb/style.json"], source.List("styles/zb"));

        // «styles/zb» не должен захватывать «styles/zb-print».
        Assert.Equal(["styles/zb-print/style.json"], source.List("styles/zb-print"));
        Assert.Equal(
            ["config/main.json", "styles/zb-print/style.json", "styles/zb/glyphs/a.json", "styles/zb/style.json"],
            source.List(string.Empty));
        Assert.Empty(source.List("nothing"));
    }

    [Fact]
    public void Replacing_a_file_overwrites_it()
    {
        var source = new InMemoryContentSource().Add("a.txt", "one").Add("a.txt", "two");

        using Stream stream = source.Open("a.txt");
        using var reader = new StreamReader(stream);
        Assert.Equal("two", reader.ReadToEnd());
        Assert.Single(source.List(string.Empty));
    }
}
