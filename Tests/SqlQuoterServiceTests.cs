using SqlVersionControl.Services;

namespace SqlVersionControl.Tests;

public class SqlQuoterServiceTests
{
    [Fact]
    public void ParseValues_NewlineDelimited_PreservesNamesWithSpaces()
    {
        var result = SqlQuoterService.ParseValues("John Smith\nJane Doe");
        Assert.Equal(new[] { "John Smith", "Jane Doe" }, result);
    }

    [Fact]
    public void ParseValues_WhitespaceDelimited_SingleLine_SplitsOnSpace()
    {
        var result = SqlQuoterService.ParseValues("123 456 789");
        Assert.Equal(new[] { "123", "456", "789" }, result);
    }

    [Fact]
    public void ParseValues_WhitespaceDelimited_MultiLine_SplitsOnSpace()
    {
        var result = SqlQuoterService.ParseValues("123 456 789\n1011 1213");
        Assert.Equal(new[] { "123", "456", "789", "1011", "1213" }, result);
    }

    [Fact]
    public void ParseValues_CommaDelimited_SplitsAndTrims()
    {
        var result = SqlQuoterService.ParseValues("1, 2, 3");
        Assert.Equal(new[] { "1", "2", "3" }, result);
    }

    [Fact]
    public void ParseValues_WordsWithSpaces_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("abc def ghi");
        Assert.Equal(new[] { "abc def ghi" }, result);
    }

    [Fact]
    public void ParseValues_IsoTimestampsPerLine_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("2026-04-24 10:30:00\n2026-04-25 11:30:00");
        Assert.Equal(new[] { "2026-04-24 10:30:00", "2026-04-25 11:30:00" }, result);
    }

    [Fact]
    public void ParseValues_IpsPerLine_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("192.168.1.1\n10.0.0.1");
        Assert.Equal(new[] { "192.168.1.1", "10.0.0.1" }, result);
    }

    [Fact]
    public void ParseValues_SlashDatesPerLine_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("2026/04/24\n2026/05/01");
        Assert.Equal(new[] { "2026/04/24", "2026/05/01" }, result);
    }

    [Fact]
    public void ParseValues_SingleDecimal_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("1.5");
        Assert.Equal(new[] { "1.5" }, result);
    }

    [Fact]
    public void ParseValues_VersionNumbersPerLine_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("1.2.3\n1.2.4");
        Assert.Equal(new[] { "1.2.3", "1.2.4" }, result);
    }

    [Fact]
    public void ParseValues_MultipleStructuredOnOneLine_KeptWhole()
    {
        var result = SqlQuoterService.ParseValues("192.168.1.1 10.0.0.1");
        Assert.Equal(new[] { "192.168.1.1 10.0.0.1" }, result);
    }

    [Fact]
    public void ParseValues_ParenWrapped_StripsWrapping()
    {
        var result = SqlQuoterService.ParseValues("(1, 2, 3)");
        Assert.Equal(new[] { "1", "2", "3" }, result);
    }

    [Fact]
    public void ParseValues_BracketWrapped_StripsWrapping()
    {
        var result = SqlQuoterService.ParseValues("[1, 2, 3]");
        Assert.Equal(new[] { "1", "2", "3" }, result);
    }

    [Fact]
    public void ParseValues_TrailingParenInValue_Preserved()
    {
        var result = SqlQuoterService.ParseValues("abc)");
        Assert.Equal(new[] { "abc)" }, result);
    }

    [Fact]
    public void ParseValues_DoubleWrapped_StripsOnceOnly()
    {
        var result = SqlQuoterService.ParseValues("((1, 2, 3))");
        Assert.Equal(new[] { "(1", "2", "3)" }, result);
    }

    [Fact]
    public void ParseValues_AlreadySingleQuoted_Idempotent()
    {
        var result = SqlQuoterService.ParseValues("'a', 'b', 'c'");
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void ParseValues_AlreadyDoubleQuoted_Idempotent()
    {
        var result = SqlQuoterService.ParseValues("\"a\", \"b\", \"c\"");
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void ParseValues_MixedDelimiters_PerLineClassification()
    {
        var result = SqlQuoterService.ParseValues("1, 2\n3 4");
        Assert.Equal(new[] { "1", "2", "3", "4" }, result);
    }

    [Fact]
    public void ParseValues_MixedNamesAndNumbers_ClassifiedPerLine()
    {
        var result = SqlQuoterService.ParseValues("John Smith\n123 456");
        Assert.Equal(new[] { "John Smith", "123", "456" }, result);
    }

    [Fact]
    public void ParseValues_CrlfLineEndings_Handled()
    {
        var result = SqlQuoterService.ParseValues("a\r\nb\r\nc");
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void ParseValues_EmptyInput_ReturnsEmptyList()
    {
        var result = SqlQuoterService.ParseValues("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseValues_WhitespaceOnlyInput_ReturnsEmptyList()
    {
        var result = SqlQuoterService.ParseValues("   \n  \t  ");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseValues_SingleValueNoDelimiter_ReturnsOneValue()
    {
        var result = SqlQuoterService.ParseValues("hello");
        Assert.Equal(new[] { "hello" }, result);
    }

    [Fact]
    public void ParseValues_TrailingComma_Ignored()
    {
        var result = SqlQuoterService.ParseValues("1, 2, 3,");
        Assert.Equal(new[] { "1", "2", "3" }, result);
    }

    [Fact]
    public void ParseValues_LeadingTrailingWhitespacePerValue_Trimmed()
    {
        var result = SqlQuoterService.ParseValues("  a  ,  b  ");
        Assert.Equal(new[] { "a", "b" }, result);
    }
}
