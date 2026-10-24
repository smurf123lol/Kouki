namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Граница транзакции для операций, которые пишут в несколько мест сразу.
/// </summary>
/// <remarks>
/// Приём фразы — это задача, ссылка в журнале распознавания и запись действия.
/// Раньше каждая запись шла своим соединением: сбой в середине оставлял задачу без
/// меты или без записи в журнале действий. Теперь весь набор идёт одной транзакцией.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Выполняет действие в транзакции: успех фиксируется, исключение откатывает всё.
    /// </summary>
    /// <typeparam name="T">Тип результата.</typeparam>
    /// <param name="body">Что выполнить.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат действия.</returns>
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default);
}
