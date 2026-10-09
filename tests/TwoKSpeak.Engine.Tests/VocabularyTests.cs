using TwoKSpeak.Engine.Asr;

namespace TwoKSpeak.Engine.Tests;

public class VocabularyTests
{
    private static Vocabulary LoadFrom(params string[] lines)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, lines);
            return Vocabulary.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetokenizeJoinsPiecesAndDropsControlTokens()
    {
        var vocab = LoadFrom("<unk> 0", "<|nospeech|> 1", "▁Ahoj 2", ", 3", "▁sv 4", "ět 5", "<blk> 6");

        Assert.Equal(6, vocab.BlankId);
        Assert.Equal("Ahoj, svět", vocab.Detokenize([1, 2, 3, 0, 4, 5]));
    }

    [Fact]
    public void LoadRejectsDuplicateIds()
    {
        Assert.Throws<InvalidDataException>(() => LoadFrom("a 0", "b 0", "<blk> 1"));
    }

    [Fact]
    public void LoadRequiresBlankToken()
    {
        Assert.Throws<InvalidDataException>(() => LoadFrom("a 0", "b 1"));
    }
}
