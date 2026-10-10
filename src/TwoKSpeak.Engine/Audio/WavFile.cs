namespace TwoKSpeak.Engine.Audio;

/// <summary>Minimal reader for 16-bit PCM mono WAV files (fixtures and benchmarks only).</summary>
public static class WavFile
{
    public static float[] ReadMono16(string path, out int sampleRate)
    {
        using var stream = File.OpenRead(path);
        return ReadMono16(stream, path, out sampleRate);
    }

    /// <param name="path">Names the source in error messages.</param>
    public static float[] ReadMono16(Stream stream, string path, out int sampleRate)
    {
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException($"'{path}' is not a RIFF file.");
        }
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException($"'{path}' is not a WAVE file.");
        }

        sampleRate = 0;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                var channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                if (format != 1 || channels != 1 || bits != 16)
                {
                    throw new InvalidDataException($"'{path}' must be 16-bit PCM mono.");
                }
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = reader.ReadInt16() / 32768f;
                }
                return samples;
            }
            else
            {
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException($"'{path}' has no data chunk.");
    }
}
