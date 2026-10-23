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

    private readonly string _connectionString;

    /// <summary>
    /// Создаёт хранилище.
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы <c>kouki.sqlite</c>.</param>
    public SqliteStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = DatabaseInitializer.BuildConnectionString(databasePath);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string? json = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT value FROM state WHERE key = @key",
                new { key },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return json is null ? default : JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        string json = JsonSerializer.Serialize(value, SerializerOptions);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO state (key, value, updated_at) VALUES (@key, @json, @updatedAt) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                new { key, json, updatedAt = DateTimeOffset.UtcNow.ToString("O") },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM state WHERE key = @key",
                new { key },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
