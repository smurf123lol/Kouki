using System.Text;

namespace Kouki.SpeechLab;

/// <summary>
/// Пишет WAV PCM16 моно 16 кГц — ровно тот формат, который принимает Kagedama.api.
/// </summary>
internal static class WavWriter
{
    /// <summary>Частота, которую ждёт сервер.</summary>
    public const int SampleRate = 16000;

    /// <summary>Сохраняет отсчёты в WAV-файл.</summary>
    public static void Write(string path, IReadOnlyList<float> samples)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        int dataLength = samples.Count * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // моно
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);

        foreach (float sample in samples)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            short value = (short)Math.Round(clamped * short.MaxValue);
            writer.Write(value);
        }
    }

    /// <summary>
    /// Пересчитывает частоту линейной интерполяцией: модели синтеза отдают 22 кГц,
    /// а приём ждёт 16 кГц.
    /// </summary>
    public static IReadOnlyList<float> Resample(IReadOnlyList<float> samples, int fromRate, int toRate = SampleRate)
    {
        if (fromRate == toRate || samples.Count == 0)
        {
            return samples;
        }

        double ratio = (double)fromRate / toRate;
        int length = (int)Math.Round(samples.Count / ratio);
        var result = new float[length];

        for (int i = 0; i < length; i++)
        {
            double position = i * ratio;
            int left = (int)position;
            int right = Math.Min(left + 1, samples.Count - 1);
            double fraction = position - left;
            result[i] = (float)((samples[left] * (1 - fraction)) + (samples[right] * fraction));
        }

        return result;
    }
}
