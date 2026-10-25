namespace Kouki.Kagedama.Bot;

/// <summary>
/// Настройки бота заданы, но разобрать их не удалось.
/// </summary>
/// <remarks>
/// Отдельный тип нужен, чтобы <c>Program</c> мог отличить «конфиг сломан, показать текст»
/// от прочих исключений, которые печатаются как стек вызовов.
/// </remarks>
public sealed class BotConfigurationException : Exception
{
    /// <summary>
    /// Создаёт исключение с готовым текстом для человека.
    /// </summary>
    /// <param name="message">Что именно не так и как это исправить.</param>
    /// <param name="innerException">Исходная ошибка разбора.</param>
    public BotConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
