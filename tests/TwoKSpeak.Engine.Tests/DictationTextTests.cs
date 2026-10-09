using TwoKSpeak.Engine.Dictation;

namespace TwoKSpeak.Engine.Tests;

public class FillerFilterTests
{
    [Theory]
    [InlineData("Um, so I was thinking, uh, we could meet.", "So I was thinking, we could meet.")]
    [InlineData("The card, hmm, runs at 575 watts.", "The card, runs at 575 watts.")]
    [InlineData("Ehm, takže zítra, eee, přijdu.", "Takže zítra, přijdu.")]
    [InlineData("Mmm.", "")]
    [InlineData("I said umm.", "I said.")]
    public void RemovesHesitationSounds(string input, string expected)
    {
        Assert.Equal(expected, FillerFilter.Apply(input));
    }

    [Theory]
    [InlineData("Umbrella and hummus are fine.")]
    [InlineData("Jako bych to prostě věděl.")]
    [InlineData("Herman met Emma.")]
    public void LeavesRealWordsAlone(string input)
    {
        Assert.Equal(input, FillerFilter.Apply(input));
    }
}

public class PhraseAssemblerTests
{
    [Fact]
    public void HoldsTrailingPunctuationUntilTheNextPhraseDecides()
    {
        var assembler = new PhraseAssembler();

        Assert.Equal("I was thinking", assembler.Add("I was thinking.", seamPunctuation: null));
        Assert.Equal(" that we could meet", assembler.Add("that we could meet.", seamPunctuation: ""));
        Assert.Equal(".", assembler.Finish());
    }

    [Fact]
    public void UsesSeamPunctuationFromTheRecognizer()
    {
        var assembler = new PhraseAssembler();

        assembler.Add("Ahoj.", null);
        Assert.Equal(", jak se máš", assembler.Add("jak se máš?", ","));
        Assert.Equal("?", assembler.Finish());
    }

    [Fact]
    public void WithoutSeamVerdictKeepsSentenceBreakOnlyBeforeCapital()
    {
        var assembler = new PhraseAssembler();

        assembler.Add("First sentence.", null);
        Assert.Equal(". Second one", assembler.Add("Second one.", null));
        Assert.Equal(" and more", assembler.Add("and more", null));
        Assert.Equal("", assembler.Finish());
    }

    [Fact]
    public void KeepsCommasWithoutSeamVerdict()
    {
        var assembler = new PhraseAssembler();

        assembler.Add("Well,", null);
        Assert.Equal(", maybe", assembler.Add("maybe", null));
    }

    [Fact]
    public void EmptyAndPunctuationOnlyPhrasesTypeNothing()
    {
        var assembler = new PhraseAssembler();

        Assert.Equal("", assembler.Add("  ", null));
        Assert.Equal("", assembler.Add(".", null));
        Assert.Equal("", assembler.Finish());
        Assert.Equal("Hello", assembler.Add("Hello.", null));
    }
}
