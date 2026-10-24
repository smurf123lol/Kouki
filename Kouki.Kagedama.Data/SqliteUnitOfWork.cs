using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Транзакция поверх <see cref="SqliteSession"/>: один набор записей — одна транзакция.
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт единицу работы.
    /// </summary>
    /// <param name="session">Сессия базы: через неё ходят все хранилища, поэтому транзакция общая.</param>
    public SqliteUnitOfWork(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
        => _session.InTransactionAsync(body, cancellationToken);
}
