namespace Kouki.Un;

/// <summary>
/// Проверка, что каталог моделей распознавания действительно содержит то, что
/// нужно загрузчику sherpa-onnx.
/// </summary>
/// <remarks>
/// Существует отдельно от <c>Sherpa\</c>, чтобы проверяться в обычной сборке
/// (без <c>-p:KoukiEnableSherpa=true</c>): решение «поднимать распознавание или
/// нет» принимается хостом до того, как создаётся распознаватель, и цена ошибки —
/// сервер, который падает при старте из-за отсутствующего каталога.
/// </remarks>
public static class SpeechModelCheck
{
    /// <summary>
    /// Проверяет каталог моделей: он должен существовать и содержать
    /// <c>*tokens.txt</c>, <c>*encoder*.onnx</c> и <c>*decoder*.onnx</c>.
    /// </summary>
    /// <param name="modelDirectory">Абсолютный путь к каталогу модели.</param>
    /// <param name="reason">Человеческое объяснение, если проверка не прошла.</param>
    /// <returns><c>true</c>, если распознавание можно поднимать.</returns>
    public static bool TryValidate(string? modelDirectory, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            reason = "не задан каталог моделей (Kouki:SpeechModelDirectory)";
            return false;
        }

        if (!Directory.Exists(modelDirectory))
        {
            reason = $"нет каталога моделей: {modelDirectory}";
            return false;
        }

        var missing = new List<string>();
        RequireContains(modelDirectory, "*tokens.txt", "tokens.txt", missing);
        RequireContains(modelDirectory, "*encoder*.onnx", "encoder *.onnx", missing);
        RequireContains(modelDirectory, "*decoder*.onnx", "decoder *.onnx", missing);

        if (missing.Count > 0)
        {
            reason = $"в {modelDirectory} нет: {string.Join(", ", missing)}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Проверяет каталог голоса для синтеза: <c>tokens.txt</c> и хотя бы один
    /// <c>*.onnx</c>. Каталог <c>espeak-ng-data</c> необязателен — он нужен только
    /// части голосов piper.
    /// </summary>
    /// <param name="voiceDirectory">Абсолютный путь к каталогу голоса.</param>
    /// <param name="reason">Человеческое объяснение, если проверка не прошла.</param>
    /// <returns><c>true</c>, если синтез можно поднимать.</returns>
    public static bool TryValidateVoice(string? voiceDirectory, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(voiceDirectory))
        {
            reason = "не задан каталог голоса (Kouki:SpeechVoiceDirectory)";
            return false;
        }

        if (!Directory.Exists(voiceDirectory))
        {
            reason = $"нет каталога голоса: {voiceDirectory}";
            return false;
        }

        var missing = new List<string>();
        RequireContains(voiceDirectory, "tokens.txt", "tokens.txt", missing);
        RequireContains(voiceDirectory, "*.onnx", "модель *.onnx", missing);

        if (missing.Count > 0)
        {
            reason = $"в {voiceDirectory} нет: {string.Join(", ", missing)}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Находит файл модели голоса в каталоге.
    /// </summary>
    /// <param name="voiceDirectory">Каталог голоса.</param>
    /// <returns>Путь к <c>*.onnx</c> или <c>null</c>, если файла нет.</returns>
    public static string? FindVoiceModel(string? voiceDirectory)
    {
        if (string.IsNullOrWhiteSpace(voiceDirectory) || !Directory.Exists(voiceDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(voiceDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }
    private static void RequireContains(string directory, string pattern, string what, List<string> missing)
    {
        if (Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any())
        {
            return;
        }

        missing.Add(what);
    }
}
