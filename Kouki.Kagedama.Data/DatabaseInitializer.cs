using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Contracts;

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

        // Дерево было позже задач: колонки добавляются после выравнивания идентификаторов,
        // иначе tree_id у старой базы остался бы в верхнем регистре.
        EnsureTaskTreeSchema(connection);

        // Журнал запросов и телеметрия (T20): метрические колонки в actions и classifications.
        EnsureRequestLogSchema(connection);

        return fullPath;
    }

    /// <summary>
    /// Догоняет старую базу до схемы дерева задач: колонки, индексы и backfill.
    /// </summary>
    /// <remarks>
    /// Идемпотентно: на новой базе колонки добавляются один раз, backfill выбирает только
    /// <c>tree_id IS NULL</c>, а <c>INSERT OR IGNORE</c> не дублирует строки деревьев.
    /// </remarks>
    private static void EnsureTaskTreeSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "tasks", "tree_id", "TEXT NULL REFERENCES task_trees (id)");
        EnsureColumn(connection, "tasks", "parent_id", "TEXT NULL REFERENCES tasks (id)");
        EnsureColumn(connection, "tasks", "position", $"INTEGER NOT NULL DEFAULT {TaskPositions.First}");

        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_tasks_tree ON tasks (tree_id); " +
                "CREATE INDEX IF NOT EXISTS ix_tasks_parent ON tasks (parent_id); " +
                "CREATE INDEX IF NOT EXISTS ix_tasks_status_position ON tasks (status, position);";
            indexes.ExecuteNonQuery();
        }

        // Отдельная задача становится деревом из одного узла: tree_id = id, parent_id = null.
        // Порядок у старых задач одинаковый (position = 0), его добирает created_at DESC.
        using var backfill = connection.CreateCommand();
        backfill.CommandText =
            "INSERT OR IGNORE INTO task_trees (id, created_at) " +
            "SELECT id, created_at FROM tasks WHERE tree_id IS NULL; " +
            "UPDATE tasks SET tree_id = id WHERE tree_id IS NULL;";
        backfill.ExecuteNonQuery();
    }

    /// <summary>
    /// Догоняет старую базу до схемы журнала запросов телеметрии (T20): метрические колонки и индекс.
    /// </summary>
    private static void EnsureRequestLogSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "actions", "http_status", "INTEGER NULL");
        EnsureColumn(connection, "actions", "audio_bytes", "INTEGER NULL");
        EnsureColumn(connection, "actions", "audio_duration_sec", "REAL NULL");

        EnsureColumn(connection, "classifications", "audio_bytes", "INTEGER NULL");
        EnsureColumn(connection, "classifications", "audio_duration_sec", "REAL NULL");
        EnsureColumn(connection, "classifications", "duration_ms", "INTEGER NULL");

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_actions_kind_created ON actions (kind, created_at DESC);";
        command.ExecuteNonQuery();
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

        CREATE TABLE IF NOT EXISTS task_trees (
            id         TEXT PRIMARY KEY,
            created_at TEXT NOT NULL
        );

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
            audio_path         TEXT NULL,
            hinted_intent      TEXT NULL,
            audio_bytes        INTEGER NULL,
            audio_duration_sec REAL NULL,
            duration_ms        INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS ix_classifications_created ON classifications (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_classifications_label ON classifications (label);
        CREATE INDEX IF NOT EXISTS ix_classifications_task ON classifications (task_id);

        CREATE TABLE IF NOT EXISTS actions (
            id                 TEXT PRIMARY KEY,
            created_at         TEXT NOT NULL,
            task_id            TEXT NULL REFERENCES tasks (id),
            classification_id  TEXT NULL REFERENCES classifications (id),
            source             TEXT NOT NULL,
            kind               TEXT NOT NULL,
            intent             TEXT NULL,
            command            TEXT NULL,
            outcome            TEXT NOT NULL,
            args_json          TEXT NULL,
            result_json        TEXT NULL,
            error              TEXT NULL,
            duration_ms        INTEGER NULL,
            http_status        INTEGER NULL,
            audio_bytes        INTEGER NULL,
            audio_duration_sec REAL NULL
        );

        CREATE INDEX IF NOT EXISTS ix_actions_created ON actions (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_actions_task ON actions (task_id);
        CREATE INDEX IF NOT EXISTS ix_actions_classification ON actions (classification_id);
        CREATE INDEX IF NOT EXISTS ix_actions_kind_created ON actions (kind, created_at DESC);

        CREATE TABLE IF NOT EXISTS state (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS reminders (
            id              TEXT PRIMARY KEY,
            created_at      TEXT NOT NULL,
            due_at          TEXT NOT NULL,
            text            TEXT NOT NULL,
            repeat_rule     TEXT NULL,
            state           TEXT NOT NULL,
            source          TEXT NOT NULL,
            task_id         TEXT NULL REFERENCES tasks (id),
            sent_at         TEXT NULL,
            acknowledged_at TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_reminders_due ON reminders (state, due_at);
        CREATE INDEX IF NOT EXISTS ix_reminders_task ON reminders (task_id);

        CREATE TABLE IF NOT EXISTS task_tags (
            task_id    TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            tag        TEXT NOT NULL,
            source     TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (task_id, tag)
        );

        CREATE INDEX IF NOT EXISTS ix_task_tags_tag ON task_tags (tag);
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
