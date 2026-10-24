using System.Text.Json;
using Dapper;
using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Data.Sqlite;

namespace Kouki.Kagedama.Data;

/// <summary>
/// <see cref="IStateStore"/> поверх SQLite: состояние контекста переживает перезапуск процесса.
/// Значения хранятся как JSON, поэтому подходит любой тип, который умеет <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// Ключи — строки в пространстве имён приложения; рекомендуемый вид: <c>домен:сущность:поле</c>,
/// например <c>station:last-command</c>. Таблица <c>state</c> создаётся
/// <see cref="DatabaseInitializer.Initialize"/>.
/// </remarks>
public sealed class SqliteStateStore : IStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteSession _session;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы <c>kouki.sqlite</c>.</param>
    public SqliteStateStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteStateStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        string? json = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT value FROM state WHERE key = @key",
                new { key },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return json is null ? default : JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        string json = JsonSerializer.Serialize(value, SerializerOptions);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO state (key, value, updated_at) VALUES (@key, @json, @updatedAt) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                new { key, json, updatedAt = DateTimeOffset.UtcNow.ToString("O") },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM state WHERE key = @key",
                new { key },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

}
