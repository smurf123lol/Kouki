namespace Kouki.Kagedama;

/// <summary>
/// Интенты, которые сессия может назвать сама (станция: «Записать» / «Спросить»).
/// </summary>
/// <remarks>
/// Список закрытый намеренно: <c>intent</c> приходит от клиента и подменяет решение
/// классификатора, поэтому принимать от него произвольную строку нельзя — иначе
/// клиент мог бы назвать любую команду и обойти порог уверенности.
/// Новый режим добавляется здесь и в <c>kouki-station/docs/protocol.md</c>.
/// </remarks>
public static class SessionIntents
{
    /// <summary>Фраза становится задачей целиком, без разбора на слова.</summary>
    public const string AddTask = "add_task";

    /// <summary>Фраза — вопрос; ответ ложится в задачу.</summary>
    public const string Ask = "ask";

    /// <summary>Все допустимые интенты сессии.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { AddTask, Ask };

    /// <summary>Известен ли такой интент сессии.</summary>
    public static bool IsKnown(string? intent)
        => !string.IsNullOrWhiteSpace(intent) && All.Contains(intent.Trim(), StringComparer.Ordinal);
}
