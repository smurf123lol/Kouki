namespace Kouki.Kagedama;

/// <summary>
/// Генератор чистого заголовка задачи: отделяет заголовок от рамок императива и instruct-слов.
/// </summary>
public interface ITaskTitleGenerator
{
    /// <summary>
    /// Извлекает лаконичный заголовок задачи из фразы пользователя.
    /// </summary>
    /// <param name="rawText">Исходная фраза пользователя.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Чистый заголовок с заглавной буквы без мусорных instruct-слов.</returns>
    Task<string> GenerateTitleAsync(string rawText, CancellationToken cancellationToken = default);
}
