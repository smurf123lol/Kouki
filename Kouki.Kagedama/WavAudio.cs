namespace Kouki.Kagedama;

/// <summary>
/// Разобранный WAV-файл: байты и параметры формата.
/// </summary>
/// <param name="Bytes">Исходные байты файла.</param>
/// <param name="SampleRate">Частота дискретизации, Гц.</param>
/// <param name="Channels">Число каналов.</param>
/// <param name="BitsPerSample">Бит на сэмпл.</param>
/// <param name="DataLength">Размер звуковых данных в байтах.</param>
public sealed record WavAudio(byte[] Bytes, int SampleRate, int Channels, int BitsPerSample, int DataLength)
{
    /// <summary>Длительность записи.</summary>
    public TimeSpan Duration
    {
        get
        {
            int bytesPerSecond = SampleRate * Channels * (BitsPerSample / 8);
            return bytesPerSecond <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)DataLength / bytesPerSecond);
        }
    }
}

/// <summary>
/// Чтение WAV: сервер принимает только тот формат, который отдаёт станция.
/// </summary>
/// <remarks>
/// Конвертировать аудио на сервере нечем (нет ffmpeg), поэтому формат фиксирован:
/// PCM, 16 бит, моно, 16 кГц. Всё прочее — ошибка с понятным текстом, а не шум в модели.
/// </remarks>
public static class WavReader
{
    /// <summary>Ожидаемая частота дискретизации.</summary>
    public const int RequiredSampleRate = 16000;

    private const ushort PcmFormat = 1;
    private const int HeaderSize = 12;

    /// <summary>
    /// Разбирает WAV и проверяет, что он годится для распознавания.
    /// </summary>
    /// <param name="bytes">Байты файла.</param>
    /// <param name="audio">Разобранный файл, если разбор удался.</param>
    /// <param name="error">Текст ошибки, если не удался.</param>
    public static bool TryParse(byte[] bytes, out WavAudio? audio, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        audio = null;
        error = null;

        if (bytes.Length < HeaderSize
            || ReadAscii(bytes, 0, 4) != "RIFF"
            || ReadAscii(bytes, 8, 4) != "WAVE")
        {
            error = "Не WAV-файл: ожидался заголовок RIFF/WAVE.";
            return false;
        }

        ushort format = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataLength = -1;

        int offset = HeaderSize;
        while (offset + 8 <= bytes.Length)
        {
            string chunkId = ReadAscii(bytes, offset, 4);
            int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            int payload = offset + 8;

            if (chunkSize < 0 || payload + chunkSize > bytes.Length)
            {
                error = $"Битый WAV: чанк '{chunkId.Trim()}' выходит за границы файла.";
                return false;
            }

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                format = BitConverter.ToUInt16(bytes, payload);
                channels = BitConverter.ToUInt16(bytes, payload + 2);
                sampleRate = BitConverter.ToInt32(bytes, payload + 4);
                bitsPerSample = BitConverter.ToUInt16(bytes, payload + 14);
            }
            else if (chunkId == "data")
            {
                dataLength = chunkSize;
            }

            // Чанки выровнены по чётной границе.
            offset = payload + chunkSize + (chunkSize % 2);
        }

        if (channels == 0 || sampleRate == 0)
        {
            error = "В WAV нет описания формата (чанк 'fmt ').";
            return false;
        }

        if (format != PcmFormat)
        {
            error = $"Поддерживается только PCM (формат 1), а в файле формат {format}.";
            return false;
        }

        if (bitsPerSample != 16)
        {
            error = $"Поддерживается только 16 бит на сэмпл, а в файле {bitsPerSample}.";
            return false;
        }

        if (channels != 1)
        {
            error = $"Поддерживается только моно, а в файле каналов: {channels}.";
            return false;
        }

        if (sampleRate != RequiredSampleRate)
        {
            error = $"Нужна частота {RequiredSampleRate} Гц, а в файле {sampleRate} Гц.";
            return false;
        }

        if (dataLength <= 0)
        {
            error = "В WAV нет звуковых данных (чанк 'data' пуст).";
            return false;
        }

        audio = new WavAudio(bytes, sampleRate, channels, bitsPerSample, dataLength);
        return true;
    }

    private static string ReadAscii(byte[] bytes, int offset, int length)
        => System.Text.Encoding.ASCII.GetString(bytes, offset, length);
}
