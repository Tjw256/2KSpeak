using System.Text;

namespace TwoKSpeak.Engine.Asr;

/// <summary>SentencePiece vocabulary in the "token id" per-line format of the ONNX export.</summary>
public sealed class Vocabulary
{
    private const char WordBoundary = '▁';
    private readonly string[] _tokens;

    public Vocabulary(string[] tokens)
    {
        _tokens = tokens;
        BlankId = Array.IndexOf(tokens, "<blk>");
        if (BlankId < 0)
        {
            throw new InvalidDataException("Vocabulary has no <blk> token.");
        }
    }

    public int Count => _tokens.Length;
    public int BlankId { get; }

    public static Vocabulary Load(string path)
    {
        var entries = new List<(string Token, int Id)>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0)
            {
                continue;
            }
            var split = line.LastIndexOf(' ');
            if (split <= 0 || !int.TryParse(line.AsSpan(split + 1), out var id))
            {
                throw new InvalidDataException($"Malformed vocabulary line: '{line}'.");
            }
            entries.Add((line[..split], id));
        }

        var tokens = new string[entries.Count];
        foreach (var (token, id) in entries)
        {
            if (id < 0 || id >= tokens.Length || tokens[id] is not null)
            {
                throw new InvalidDataException($"Vocabulary id {id} is out of range or duplicated.");
            }
            tokens[id] = token;
        }
        return new Vocabulary(tokens);
    }

    /// <summary>True for tokens that begin a new word (SentencePiece boundary marker).</summary>
    public bool IsWordStart(int id) => _tokens[id].StartsWith(WordBoundary);

    /// <summary>True for tokens that are only punctuation, such as "," or "." (with or without a word boundary).</summary>
    public bool IsPunctuation(int id)
    {
        var piece = _tokens[id].AsSpan().Trim(WordBoundary);
        if (piece.IsEmpty)
        {
            return false;
        }
        foreach (var c in piece)
        {
            if (!char.IsPunctuation(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Joins token pieces into text, dropping control tokens such as &lt;unk&gt; or &lt;|nospeech|&gt;.</summary>
    public string Detokenize(IEnumerable<int> ids)
    {
        var text = new StringBuilder();
        foreach (var id in ids)
        {
            var token = _tokens[id];
            if (token.StartsWith('<') && token.EndsWith('>'))
            {
                continue;
            }
            text.Append(token);
        }
        return text.Replace(WordBoundary, ' ').ToString().Trim();
    }
}
