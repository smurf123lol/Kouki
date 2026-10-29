namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Отвечает на вопрос пользователя (команда <c>ask</c>).
/// </summary>
/// <remarks>
/// Интерфейс отдельный от классификатора интентов: классификатор решает,
/// что человек имел в виду, а ответчик — что на это ответить. Пока ответчика
/// нет (нет ключа или провайдер выключен), команда остаётся заглушкой, но маршрут
/// «вопрос → команда → ответ в задаче» не меняется.
/// </remarks>
public interface IAnswerer
{
    /// <summary>Готов ли ответчик принимать вопросы (есть ключ, провайдер не выключен).</summary>
    bool IsConfigured { get; }

    /// <summary>Почему ответчик недоступен; <c>null</c>, если он готов.</summary>
    string? UnavailableReason { get; }

    /// <summary>Провайдер и модель — для журнала и диагностики («gemini:gemini-2.5-flash»).</summary>
    string Signature { get; }

    /// <summary>
    /// Отвечает на вопрос.
    /// </summary>
    /// <exception cref="AnswererException">Провайдер вернул отказ или не ответил вовремя.</exception>
    Task<Answer> AnswerAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Ответ модели.</summary>
/// <param name="Text">Текст ответа.</param>
/// <param name="Signature">Провайдер и модель, которые ответили.</param>
/// <param name="ElapsedMs">Сколько заняло.</param>
public sealed record Answer(string Text, string Signature, long ElapsedMs);

/// <summary>Ответчик недоступен или отказал: текст исключения можно показывать человеку.</summary>
public sealed class AnswererException : Exception
{
    /// <summary>Создаёт исключение с человекочитаемым объяснением.</summary>
    public AnswererException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
