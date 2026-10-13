namespace PenForLang.Evaluation.Tests;

public class EvaluatorVersionTests
{
    [Fact]
    public void Current_version_is_a_non_empty_tag()
    {
        Assert.False(string.IsNullOrWhiteSpace(EvaluatorVersion.Current));
        Assert.StartsWith("v", EvaluatorVersion.Current, StringComparison.Ordinal);
    }
}
