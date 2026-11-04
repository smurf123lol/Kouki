using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Kouki.Un;

/// <summary>
/// Проверка имён и разрешение каталогов моделей голосов синтеза речи.
/// </summary>
public static class SpeechVoiceResolver
{
    public const int MaxVoiceNameLength = 64;

    private static readonly Regex VoiceNamePattern = new(
        @"^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Проверяет допустимость имени голоса.
    /// </summary>
    public static bool TryValidateVoiceName(
        string? voice,
        [NotNullWhen(false)] out string? error)
    {
        if (voice is null || voice.Trim().Length == 0)
        {
            error = "Поле 'voice' пустое: нечего выбирать.";
            return false;
        }

        string trimmed = voice.Trim();
        if (trimmed.Length > MaxVoiceNameLength
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || !VoiceNamePattern.IsMatch(trimmed))
        {
            error = $"Недопустимое имя голоса '{voice}'. Разрешены буквы, цифры, точка, дефис и подчёркивание.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Разрешает каталог модели голоса:
    /// 1. <paramref name="baseDirectory"/>/vits-piper-&lt;voice&gt;
    /// 2. <paramref name="baseDirectory"/>/&lt;voice&gt;
    /// 3. сам <paramref name="baseDirectory"/> (если совпадает с <paramref name="defaultVoice"/> и в нём лежат файлы).
    /// </summary>
    public static string? ResolveVoiceDirectory(
        string? baseDirectory,
        string voice,
        string? defaultVoice = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return null;
        }

        string trimmedVoice = voice.Trim();

        // 1. vits-piper-<voice>
        string candidate1 = Path.Combine(baseDirectory, "vits-piper-" + trimmedVoice);
        if (SpeechModelCheck.TryValidateVoice(candidate1, out _))
        {
            return candidate1;
        }

        // 2. <voice>
        string candidate2 = Path.Combine(baseDirectory, trimmedVoice);
        if (SpeechModelCheck.TryValidateVoice(candidate2, out _))
        {
            return candidate2;
        }

        // 3. Сам baseDirectory (только для голоса по умолчанию)
        if (!string.IsNullOrWhiteSpace(defaultVoice) &&
            string.Equals(trimmedVoice, defaultVoice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            if (SpeechModelCheck.TryValidateVoice(baseDirectory, out _))
            {
                return baseDirectory;
            }
        }

        return null;
    }

    /// <summary>
    /// Перечисляет все установленные и валидные голоса в каталоге.
    /// </summary>
    public static IReadOnlyList<string> EnumerateAvailableVoices(
        string? baseDirectory,
        string? defaultVoice = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
        {
            return Array.Empty<string>();
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(baseDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string dirName = Path.GetFileName(dir);
                string voiceName = dirName.StartsWith("vits-piper-", StringComparison.OrdinalIgnoreCase)
                    ? dirName["vits-piper-".Length..]
                    : dirName;

                if (TryValidateVoiceName(voiceName, out _) && SpeechModelCheck.TryValidateVoice(dir, out _))
                {
                    result.Add(voiceName);
                }
            }
        }
        catch
        {
            // Не удалось перечислить подкаталоги — игнорируем
        }

        // Если в самом baseDirectory лежит одиночный голос
        if (!string.IsNullOrWhiteSpace(defaultVoice) &&
            SpeechModelCheck.TryValidateVoice(baseDirectory, out _))
        {
            result.Add(defaultVoice.Trim());
        }

        return result.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Форматирует текст ошибки «Голос '...' не установлен».
    /// </summary>
    public static string FormatVoiceNotFoundMessage(string voice, IReadOnlyList<string> availableVoices)
    {
        string listStr = availableVoices.Count > 0
            ? string.Join(", ", availableVoices)
            : "(нет)";

        return $"Голос '{voice}' не установлен. Доступны: {listStr}.";
    }
}
