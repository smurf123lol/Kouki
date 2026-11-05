using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama;

/// <summary>
/// Проверенный запрос на приём фразы: всё, что нужно <see cref="TaskIntakeService"/>.
/// </summary>
/// <param name="Text">Текст без крайних пробелов.</param>
/// <param name="Source">Источник из <see cref="TaskSources"/>.</param>
/// <param name="IntentHint">Интент, который сообщила сессия, или <c>null</c>.</param>
public sealed record IntakeSpec(string Text, string Source, string? IntentHint)
{
    /// <summary>
    /// Теги, вырезанные из фразы в режиме <see cref="TagModes.Extract"/> (в режиме keep — пусто).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Запрос продолжения предыдущего хода диалога.
    /// </summary>
    public bool ContinueDialogue { get; init; } = false;
}

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

    /// <summary>Параметр продолжения диалога содержит неизвестное значение.</summary>
    public const string UnknownDialogue = "unknown-dialogue";

    /// <summary>Единственное допустимое значение параметра продолжения диалога.</summary>
    public const string DialogueContinueValue = "continue";

    /// <summary>После вырезания тегов во фразе не осталось самой формулировки.</summary>
    public const string TagsOnlyText = "tags-only";

    /// <summary>Текст отказа для <see cref="TagsOnlyText"/>: он же у защитного исключения приёма.</summary>
    public const string TagsOnlyMessage = "После вырезания тегов во фразе ничего не осталось. Добавьте саму формулировку задачи.";

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
    /// <param name="tagMode">Режим тегов для источника, см. <see cref="IntakeOptions.ResolveTagMode"/>.</param>
    /// <param name="dialogue">Параметр продолжения диалога из запроса.</param>
    /// <returns><c>true</c>, если запрос можно передавать в приём.</returns>
    /// <exception cref="ArgumentException">Режим тегов неизвестен.</exception>
    public static bool TryPrepare(
        string? text,
        string? requestedSource,
        string? intentHint,
        string defaultSource,
        int maxTextLength,
        out IntakeSpec? spec,
        out IntakeRejection? rejection,
        string tagMode = TagModes.Extract,
        string? dialogue = null)
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

        if (!TryResolveDialogueContinue(dialogue, out bool continueDialogue, out rejection))
        {
            return false;
        }

        if (!TryResolveTags(trimmed, tagMode, out IReadOnlyList<string> tags, out rejection))
        {
            return false;
        }

        spec = new IntakeSpec(trimmed, source, hint)
        {
            Tags = tags,
            ContinueDialogue = continueDialogue,
        };
        return true;
    }

    /// <summary>
    /// Проверяет, что во фразе остаётся формулировка, а не одни теги, и достаёт сами теги.
    /// </summary>
    /// <remarks>
    /// В режиме <see cref="TagModes.Extract"/> теги вырезаются — значит, фраза «#работа #дом»
    /// задачи не содержит, и приём должен отказать до записи, а не бросить исключение внутри.
    /// В режиме <see cref="TagModes.Keep"/> вырезания нет: текст остаётся как есть, теги — пусто.
    /// </remarks>
    /// <param name="trimmed">Фраза без крайних пробелов.</param>
    /// <param name="tagMode">Режим тегов источника.</param>
    /// <param name="tags">Теги, вырезанные из фразы.</param>
    /// <param name="rejection">Причина отказа, если остались одни теги.</param>
    /// <returns><c>true</c>, если фразу можно принимать.</returns>
    private static bool TryResolveTags(
        string trimmed,
        string tagMode,
        out IReadOnlyList<string> tags,
        out IntakeRejection? rejection)
    {
        rejection = null;
        tags = Array.Empty<string>();

        if (string.Equals(tagMode, TagModes.Keep, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(tagMode, TagModes.Extract, StringComparison.OrdinalIgnoreCase))
        {
            // Тот же fail-closed, что и в TagParser.Split: опечатка в конфиге — ошибка, а не keep.
            throw new ArgumentException($"Неизвестный режим тегов '{tagMode}'.", nameof(tagMode));
        }

        (string cleaned, IReadOnlyList<string> extracted) = TagParser.Split(trimmed, tagMode);
        if (cleaned.Length == 0)
        {
            rejection = new IntakeRejection(TagsOnlyText, TagsOnlyMessage);
            return false;
        }

        tags = extracted;
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

    /// <summary>
    /// Разрешает признак продолжения предыдущего хода диалога.
    /// </summary>
    /// <param name="requested">Значение параметра dialogue из запроса.</param>
    /// <param name="continueDialogue">Установлен ли признак продолжения.</param>
    /// <param name="rejection">Причина отказа, если значение неизвестно.</param>
    /// <returns><c>true</c>, если параметр допустим.</returns>
    public static bool TryResolveDialogueContinue(
        string? requested,
        out bool continueDialogue,
        out IntakeRejection? rejection)
    {
        rejection = null;
        continueDialogue = false;

        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        string trimmed = requested.Trim();
        if (string.Equals(trimmed, DialogueContinueValue, StringComparison.OrdinalIgnoreCase))
        {
            continueDialogue = true;
            return true;
        }

        rejection = new IntakeRejection(
            UnknownDialogue,
            $"Неизвестный dialogue '{trimmed}'. Допустимое значение: {DialogueContinueValue}.");
        return false;
    }
}
