using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Создание файла базы и схемы. База — <c>kouki.sqlite</c>.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Готовит строку подключения для файла базы.
    /// </summary>
    /// <param name="databasePath">Путь к файлу; относительный разрешается от текущего каталога.</param>
    /// <returns>Строка подключения SQLite.</returns>
    public static string BuildConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        };

        return builder.ToString();
    }

    /// <summary>
    /// Создаёт каталог, файл базы и таблицы, если их ещё нет.
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    /// <returns>Абсолютный путь к файлу базы.</returns>
    public static string Initialize(string databasePath)
    {
        string connectionString = BuildConnectionString(databasePath);
        string fullPath = new SqliteConnectionStringBuilder(connectionString).DataSource!;

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(GuidAsTextHandler.Instance);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();

        // Базы, созданные до появления подсказки интента, нужно догнать: CREATE TABLE IF NOT EXISTS
        // не добавляет новые колонки в уже существующую таблицу.
        EnsureColumn(connection, "classifications", "hinted_intent", "TEXT NULL");

        // Идентификаторы пишутся текстом в нижнем регистре. Раньше Guid, переданный параметром,
        // попадал в файл в верхнем регистре — выравниваем уже сохранённые строки.
        command.CommandText = "UPDATE tasks SET id = lower(id) WHERE id <> lower(id); " +
            "UPDATE classifications SET id = lower(id) WHERE id <> lower(id);";
        command.ExecuteNonQuery();

        return fullPath;
    }

    /// <summary>
    /// Добавляет колонку, если её нет: <c>CREATE TABLE IF NOT EXISTS</c> не меняет уже существующую таблицу.
    /// </summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private const string Schema = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS tasks (
            id          TEXT PRIMARY KEY,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL,
            source      TEXT NOT NULL,
            raw_text    TEXT NOT NULL,
            title       TEXT NOT NULL,
            status      TEXT NOT NULL,
            priority    INTEGER NOT NULL DEFAULT 2,
            workspace   TEXT NULL,
            meta_json   TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_tasks_created ON tasks (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks (status);

        CREATE TABLE IF NOT EXISTS classifications (
            id             TEXT PRIMARY KEY,
            task_id        TEXT NULL,
            created_at     TEXT NOT NULL,
            source         TEXT NOT NULL,
            input_kind     TEXT NOT NULL,
            input_text     TEXT NOT NULL,
            asr_confidence REAL NULL,
            intent         TEXT NOT NULL,
            score          REAL NOT NULL,
            confident      INTEGER NOT NULL,
            model          TEXT NOT NULL,
            error          TEXT NULL,
            label          TEXT NULL,
            labeled_at     TEXT NULL,
            audio_path     TEXT NULL,
            hinted_intent  TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_classifications_created ON classifications (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_classifications_label ON classifications (label);

        CREATE TABLE IF NOT EXISTS actions (
            id                TEXT PRIMARY KEY,
            created_at        TEXT NOT NULL,
            task_id           TEXT NULL REFERENCES tasks (id),
            classification_id TEXT NULL REFERENCES classifications (id),
            source            TEXT NOT NULL,
            kind              TEXT NOT NULL,
            intent            TEXT NULL,
            command           TEXT NULL,
            outcome           TEXT NOT NULL,
            args_json         TEXT NULL,
            result_json       TEXT NULL,
            error             TEXT NULL,
            duration_ms       INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS ix_actions_created ON actions (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_actions_task ON actions (task_id);
        CREATE INDEX IF NOT EXISTS ix_actions_classification ON actions (classification_id);

        CREATE TABLE IF NOT EXISTS state (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;
}

/// <summary>
/// Dapper читает <see cref="Guid"/>, сохранённый как текст: без обработчика он пытается
/// привести строку к Guid напрямую и падает.
/// </summary>
internal sealed class GuidAsTextHandler : SqlMapper.TypeHandler<Guid>
{
    public static GuidAsTextHandler Instance { get; } = new();

    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = value.ToString();
    }

    public override Guid Parse(object value) => value switch
    {
        Guid guid => guid,
        string text => Guid.Parse(text),
        byte[] bytes => new Guid(bytes),
        _ => throw new DataException($"Не удалось прочитать Guid из значения типа {value?.GetType().Name ?? "null"}."),
    };
}
