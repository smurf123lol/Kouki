using Microsoft.Data.Sqlite;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Одна точка открытия соединений с базой: включает <c>busy_timeout</c> и умеет
/// выполнить несколько записей в одной транзакции.
/// </summary>
/// <remarks>
/// Файл базы делят три процесса (API, MCP, бот), поэтому без <c>busy_timeout</c>
/// второй писатель получает «database is locked» вместо короткого ожидания.
/// Транзакция нужна приёму фразы: задача, ссылка в журнале и запись действия должны
/// появляться вместе, иначе после сбоя остаётся половина.
/// </remarks>
public sealed class SqliteSession
{
    /// <summary>Сколько миллисекунд SQLite ждёт освобождения блокировки, прежде чем ответить ошибкой.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    private readonly string _connectionString;
    private readonly AsyncLocal<SqliteTransaction?> _ambient = new();

    /// <summary>
    /// Создаёт сессию для файла базы.
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteSession(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = DatabaseInitializer.BuildConnectionString(DatabasePath);
    }

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Открывает соединение или отдаёт уже открытое в текущей транзакции.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Соединение и (если есть) транзакция. Освобождать — через <c>await using</c>.</returns>
    public async Task<SqliteLease> OpenAsync(CancellationToken cancellationToken = default)
    {
        SqliteTransaction? ambient = _ambient.Value;
        if (ambient is not null && ambient.Connection is { } shared)
        {
            // Внутри транзакции соединение общее: освобождает его тот, кто транзакцию открыл.
            return new SqliteLease(shared, ambient, ownsConnection: false);
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyBusyTimeoutAsync(connection, cancellationToken).ConfigureAwait(false);

        return new SqliteLease(connection, null, ownsConnection: true);
    }

    /// <summary>
    /// Выполняет действие в одной транзакции. Вложенный вызов использует внешнюю транзакцию.
    /// </summary>
    /// <typeparam name="T">Тип результата.</typeparam>
    /// <param name="body">Что выполнить.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат действия.</returns>
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (_ambient.Value is not null)
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplyBusyTimeoutAsync(connection, cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ambient.Value = transaction;
        try
        {
            T result = await body(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _ambient.Value = null;
        }
    }

    private static async Task ApplyBusyTimeoutAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Соединение (и, возможно, транзакция), выданное сессией.
/// </summary>
public sealed class SqliteLease : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    internal SqliteLease(SqliteConnection connection, SqliteTransaction? transaction, bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    /// <summary>Соединение для запросов.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Активная транзакция, если запрос выполняется внутри <see cref="SqliteSession.InTransactionAsync{T}"/>.</summary>
    public SqliteTransaction? Transaction { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => _ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
