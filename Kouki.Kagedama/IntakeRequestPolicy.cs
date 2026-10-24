using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama;

/// <summary>
/// Проверенный запрос на приём фразы: всё, что нужно <see cref="TaskIntakeService"/>.
/// </summary>
/// <param name="Text">Текст без крайних пробелов.</param>
/// <param name="Source">Источник из <see cref="TaskSources"/>.</param>
/// <param name="IntentHint">Интент, который сообщила сессия, или <c>null</c>.</param>
public sealed record IntakeSpec(string Text, string Source, string? IntentHint);

/// <summary>
/// Причина отказа в приёме. <see cref="Message"/> уже годится для ответа клиенту.
/// </summary>
/// <param name="Code">Стабильный код, по которому хост решает, какой HTTP-статус вернуть.</param>
/// <param name="Message">Текст для человека.</param>
public sealed record IntakeRejection(string Code, string Message);

/// <summary>
/// Граница приёма фразы: что считается корректным запросом.
/// </summary>
/// <remarks>
/// Раньше эти правила жили в HTTP-эндпоинте, и MCP повторял их своими руками —
/// «источник по умолчанию», закрытый список интентов, лимит длины текста.
/// Теперь правило одно, а хост только переводит отказ в свой статус.
/// </remarks>
public static class IntakeRequestPolicy
{
    /// <summary>Длина текста, которую сервер принимает по умолчанию.</summary>
    public const int DefaultMaxTextLength = 2000;

    /// <summary>Пустой текст.</summary>
    public const string MissingText = "missing-text";

    /// <summary>Источник не из <see cref="TaskSources.All"/>.</summary>
    public const string UnknownSource = "unknown-source";

    /// <summary>Интент не из <see cref="SessionIntents.All"/>.</summary>
    public const string UnknownIntent = "unknown-intent";

    /// <summary>Текст длиннее лимита.</summary>
    public const string TextTooLong = "text-too-long";

    /// <summary>
    /// Проверяет запрос и собирает <see cref="IntakeSpec"/>.
    /// </summary>
    /// <param name="text">Текст фразы из запроса.</param>
    /// <param name="requestedSource">Источник из запроса; пусто — берётся <paramref name="defaultSource"/>.</param>
    /// <param name="intentHint">Интент сессии из запроса.</param>
    /// <param name="defaultSource">Источник по умолчанию для этого хоста.</param>
    /// <param name="maxTextLength">Лимит длины текста; <c>0</c> или меньше — без лимита.</param>
    /// <param name="spec">Готовый запрос, если проверка прошла.</param>
    /// <param name="rejection">Причина отказа, если проверка не прошла.</param>
    /// <returns><c>true</c>, если запрос можно передавать в приём.</returns>
    public static bool TryPrepare(
        string? text,
        string? requestedSource,
        string? intentHint,
        string defaultSource,
        int maxTextLength,
        out IntakeSpec? spec,
        out IntakeRejection? rejection)
    {
        spec = null;
        rejection = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            rejection = new IntakeRejection(MissingText, "Поле 'text' обязательно: пустая фраза не несёт смысла.");
            return false;
        }

        string trimmed = text.Trim();

        if (maxTextLength > 0 && trimmed.Length > maxTextLength)
        {
            rejection = new IntakeRejection(TextTooLong, $"Текст длиннее {maxTextLength} символов.");
            return false;
        }

        if (!TryResolveSource(requestedSource, defaultSource, out string source, out rejection))
        {
            return false;
        }

        if (!TryResolveIntentHint(intentHint, out string? hint, out rejection))
        {
            return false;
        }

        spec = new IntakeSpec(trimmed, source, hint);
        return true;
    }

    /// <summary>
    /// Разрешает источник: пустой запрос означает источник по умолчанию хоста.
    /// </summary>
    /// <param name="requested">Источник из запроса.</param>
    /// <param name="defaultSource">Источник по умолчанию.</param>
    /// <param name="source">Разрешённый источник.</param>
    /// <param name="rejection">Причина отказа, если источник неизвестен.</param>
    /// <returns><c>true</c>, если источник допустим.</returns>
    public static bool TryResolveSource(
        string? requested,
        string defaultSource,
        out string source,
        out IntakeRejection? rejection)
    {
        rejection = null;
        source = string.IsNullOrWhiteSpace(requested) ? defaultSource : requested.Trim();

        if (TaskSources.IsKnown(source))
        {
            return true;
        }

        rejection = new IntakeRejection(
            UnknownSource,
            $"Неизвестный источник '{source}'. Допустимые: {string.Join(", ", TaskSources.All)}.");
        return false;
    }

    /// <summary>
    /// Разрешает подсказку интента от сессии.
    /// </summary>
    /// <param name="requested">Интент из запроса.</param>
    /// <param name="intentHint">Интент или <c>null</c>, если сессия его не сообщала.</param>
    /// <param name="rejection">Причина отказа, если интент вне закрытого списка.</param>
    /// <returns><c>true</c>, если подсказка допустима.</returns>
    /// <remarks>
    /// Строка принимается только из закрытого списка: иначе клиент назвал бы любую команду
    /// в обход классификатора.
    /// </remarks>
    public static bool TryResolveIntentHint(
        string? requested,
        out string? intentHint,
        out IntakeRejection? rejection)
    {
        rejection = null;
        intentHint = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();

        if (intentHint is null || SessionIntents.IsKnown(intentHint))
        {
            return true;
        }

        rejection = new IntakeRejection(
            UnknownIntent,
            $"Неизвестный intent '{intentHint}'. Допустимые: {string.Join(", ", SessionIntents.All)}.");
        return false;
    }
}
