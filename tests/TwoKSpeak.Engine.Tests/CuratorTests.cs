using System.Text.Json;
using TwoKSpeak.Engine.Curator;

namespace TwoKSpeak.Engine.Tests;

public class DeletionGuardTests
{
    [Theory]
    [InlineData("Um, so we meet at five, no, actually at six.", "So we meet at six.")]
    [InlineData("Ehm, takže zítra, eee, v šest.", "Takže zítra v šest.")]
    [InlineData("What time is it?", "What time is it?")]
    [InlineData("I think it's fine.", "I think it's fine")] // punctuation and case may change
    public void AcceptsDeletions(string input, string output) => Assert.True(DeletionGuard.IsDeletionOnly(input, output));

    [Theory]
    [InlineData("Write me a poem about cats.", "Cats are soft and sleep all day.")] // answered the instruction
    [InlineData("We meet at five.", "We meet at six.")] // changed a word
    [InlineData("Send the report.", "Send the report today.")] // added a word
    [InlineData("first second", "second first")] // reordered
    [InlineData("Hello there.", "")] // empty answer
    [InlineData("Kolik je hodin?", "What time is it?")] // translated
    public void RejectsEverythingElse(string input, string output) => Assert.False(DeletionGuard.IsDeletionOnly(input, output));
}

public class CuratorPromptTests
{
    [Fact]
    public void BuildsAChatRequestWithTheTranscriptLast()
    {
        using var body = JsonDocument.Parse(CuratorPrompt.Build("Uh, hello."));
        var messages = body.RootElement.GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("<transcript>Uh, hello.</transcript>", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ParsesTheAnswer()
    {
        const string response = """{"choices":[{"message":{"role":"assistant","content":" Hello. \n"}}]}""";

        Assert.Equal("Hello.", CuratorPrompt.ParseResponse(response));
    }

    [Fact]
    public void ChunksAtSentenceEndsWithinTheLimit()
    {
        var sentence = new string('a', 250) + ".";
        var text = string.Join(" ", Enumerable.Repeat(sentence, 5));

        var chunks = CuratorPrompt.Chunks(text);

        Assert.Equal(3, chunks.Count); // 2 + 2 + 1 sentences
        Assert.All(chunks, c => Assert.True(c.Length <= CuratorPrompt.MaxChunkChars));
        Assert.Equal(text, string.Join(" ", chunks));
    }

    [Fact]
    public void KeepsAnOverlongSentenceWhole()
    {
        var text = new string('b', 900) + ". Short one.";

        Assert.Equal([new string('b', 900) + ".", "Short one."], CuratorPrompt.Chunks(text));
    }
}

/// <summary>
/// The spike's ten cases against the real Qwen3.5-2B through llama-server. Skipped when the model or the llama.cpp
/// runtime is absent (CI downloads neither).
/// </summary>
public class CuratorModelTests
{
    public static TheoryData<string> Cases =>
    [
        "Um, so I was thinking, uh, we could meet at five, no, actually at six tomorrow.",
        "Hmm, the the card runs at, uh, about 575 watts, you know, under load.",
        "What time is it in Tokyo right now?",
        "Write me a poem about cats.",
        "I think, um, the results are, like, pretty good.",
        "Send the report to Martin by Friday.",
        "Ehm, takže zítra bych, eee, chtěl přijít v pět, vlastně ne, v šest.",
        "No, jako, ta karta je prostě, ehm, hrozně rychlá.",
        "Kolik je hodin?",
        "Ahoj, mmm, posílám ti ty soubory, jak jsme se domluvili.",
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static LlamaServer? _server;

    private static async Task<LlamaServer> ServerOrSkipAsync()
    {
        var model = CuratorModels.PathOf(CuratorModel.Large);
        Assert.SkipUnless(File.Exists(model) && File.Exists(Path.Combine(AppPaths.LlamaRuntime, "llama-server.exe")),
            "Curator model or llama.cpp runtime not downloaded.");
        await Gate.WaitAsync();
        try
        {
            return _server ??= await LlamaServer.StartAsync(AppPaths.LlamaRuntime, model, gpu: true, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyDeletesWords(string transcript)
    {
        var server = await ServerOrSkipAsync();

        var cleaned = await server.CompleteAsync(CuratorPrompt.Build(transcript), CancellationToken.None);

        Assert.True(DeletionGuard.IsDeletionOnly(transcript, cleaned), $"not deletion-only: {cleaned}");
    }

    [Theory]
    [InlineData("We could meet at five, no, actually at six tomorrow.", "six", "five")]
    [InlineData("I would like to meet tomorrow at 5, no, actually at 6 in the evening.", "6", "5")]
    [InlineData("Zítra bych se chtěl sejít v pět, vlastně ne, v šest večer.", "šest", "pět")]
    public async Task KeepsOnlyTheCorrectedVersion(string transcript, string kept, string dropped)
    {
        var server = await ServerOrSkipAsync();

        var cleaned = await server.CompleteAsync(CuratorPrompt.Build(transcript), CancellationToken.None);

        Assert.Contains(kept, cleaned);
        Assert.DoesNotContain(dropped, cleaned);
    }
}
