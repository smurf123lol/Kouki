using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kouki.Tests;

/// <summary>
/// Инициализатор базы: разовое приведение идентификаторов к нижнему регистру,
/// отмеченное в <c>PRAGMA user_version</c>.
/// </summary>
/// <remarks>
/// Признак «приведено» — целое в заголовке файла SQLite, поэтому тесты читают его
/// и пишут сырым SQL, не через хранилища: проверяется поведение самого
/// <see cref="DatabaseInitializer"/>, а не слой данных поверх него.
/// </remarks>
public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    /// <summary>Готовит путь к временной базе: в репозиторий файлы не попадают.</summary>
    public DatabaseInitializerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "kouki.sqlite");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — тест от этого не зависит.
        }
    }

    [Fact]
    public void New_database_gets_version_one_and_repeated_initialize_does_not_throw()
    {
        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(1, ReadUserVersion());

        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(1, ReadUserVersion());
    }

    [Fact]
    public void Old_database_with_uppercase_ids_is_lowered_and_marked()
    {
        DatabaseInitializer.Initialize(_databasePath);
        Execute(
            "PRAGMA user_version = 0;" +
            InsertTaskSql("AAAAAAA1-0000-0000-0000-000000000001", "старая задача") +
            InsertClassificationSql("BBBBBBB1-0000-0000-0000-000000000001"));

        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(new[] { "aaaaaaa1-0000-0000-0000-000000000001" }, ReadIds("tasks"));
        Assert.Equal(new[] { "bbbbbbb1-0000-0000-0000-000000000001" }, ReadIds("classifications"));
        Assert.Equal(1, ReadUserVersion());
    }

    [Fact]
    public void Repeated_initialize_does_not_lower_ids_added_after_migration()
    {
        DatabaseInitializer.Initialize(_databasePath);
        Execute(
            InsertTaskSql("CCCCCCC1-0000-0000-0000-000000000001", "задача после миграции") +
            InsertClassificationSql("DDDDDDD1-0000-0000-0000-000000000001"));

        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(new[] { "CCCCCCC1-0000-0000-0000-000000000001" }, ReadIds("tasks"));
        Assert.Equal(new[] { "DDDDDDD1-0000-0000-0000-000000000001" }, ReadIds("classifications"));
        Assert.Equal(1, ReadUserVersion());
    }

    [Fact]
    public void Normal_database_is_unchanged_by_initialize()
    {
        DatabaseInitializer.Initialize(_databasePath);
        Execute(
            InsertTaskSql("eeeeeee1-0000-0000-0000-000000000001", "нормальная задача") +
            InsertClassificationSql("fffffff1-0000-0000-0000-000000000001"));

        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal(new[] { "eeeeeee1-0000-0000-0000-000000000001" }, ReadIds("tasks"));
        Assert.Equal(new[] { "fffffff1-0000-0000-0000-000000000001" }, ReadIds("classifications"));
        Assert.Equal("нормальная задача", ReadSingleString("SELECT title FROM tasks;"));
        Assert.Equal(1, ReadUserVersion());
    }

    /// <summary>
    /// Ошибка внутри миграции откатывает транзакцию целиком: <c>user_version</c> остаётся 0,
    /// а уже выполненные <c>UPDATE</c> не сохраняются (§2.4 спеки).
    /// </summary>
    /// <remarks>
    /// Заодно это проверка риска §5: в старом бэкапе дочерняя строка ссылается на id задачи
    /// в верхнем регистре, и при <c>ForeignKeys = true</c> приведение родительского ключа
    /// падает с <c>SQLITE_CONSTRAINT_FOREIGNKEY</c> («FOREIGN KEY constraint failed»).
    /// Второй шаг проверяет атомарность: <c>UPDATE tasks</c> успевает выполниться, падает
    /// <c>UPDATE classifications</c>, и откат обязан вернуть id задачи в верхний регистр.
    /// </remarks>
    [Fact]
    public void Failed_migration_rolls_back_identifiers_and_keeps_version_zero()
    {
        DatabaseInitializer.Initialize(_databasePath);

        // Старая база: задача с id в верхнем регистре и тег, ссылающийся на этот же id.
        Execute(
            "PRAGMA user_version = 0;" +
            InsertTaskSql("AAAAAAA1-0000-0000-0000-000000000001", "старая задача") +
            InsertTaskTagSql("AAAAAAA1-0000-0000-0000-000000000001"));

        SqliteException childReference = Assert.Throws<SqliteException>(
            () => DatabaseInitializer.Initialize(_databasePath));

        // Фактический текст Microsoft.Data.Sqlite: "SQLite Error 19: 'FOREIGN KEY constraint failed'."
        Assert.Contains("FOREIGN KEY constraint failed", childReference.Message);
        Assert.Equal(0, ReadUserVersion());
        Assert.Equal(new[] { "AAAAAAA1-0000-0000-0000-000000000001" }, ReadIds("tasks"));

        // Атомарность: снимаем ссылку с tasks, чтобы первый UPDATE прошёл; второй UPDATE
        // (classifications) падает на FK из actions.classification_id.
        Execute(
            "DELETE FROM task_tags;" +
            InsertClassificationSql("BBBBBBB1-0000-0000-0000-000000000001") +
            InsertActionSql("EEEEEEE1-0000-0000-0000-000000000001", "BBBBBBB1-0000-0000-0000-000000000001"));

        SqliteException childOfClassification = Assert.Throws<SqliteException>(
            () => DatabaseInitializer.Initialize(_databasePath));

        Assert.Contains("FOREIGN KEY constraint failed", childOfClassification.Message);
        Assert.Equal(0, ReadUserVersion());
        Assert.Equal(new[] { "AAAAAAA1-0000-0000-0000-000000000001" }, ReadIds("tasks"));
        Assert.Equal(new[] { "BBBBBBB1-0000-0000-0000-000000000001" }, ReadIds("classifications"));
    }

    private static string InsertTaskSql(string id, string title) =>
        "INSERT INTO tasks (id, created_at, updated_at, source, raw_text, title, status, priority) VALUES " +
        $"('{id}', '2026-11-06T10:00:00+00:00', '2026-11-06T10:00:00+00:00', " +
        $"'{TaskSources.Api}', 'сырой текст', '{title}', '{TaskStatuses.New}', {TaskPriorities.Default});";

    // task_id намеренно NULL: связь с задачей проверяет риск из §5 спеки (дочерние ссылки
    // с верхним регистром при ForeignKeys = true), здесь проверяется только приведение id.
    private static string InsertClassificationSql(string id) =>
        "INSERT INTO classifications (id, task_id, created_at, source, input_kind, input_text, intent, score, " +
        "confident, model) VALUES " +
        $"('{id}', NULL, '2026-11-06T10:00:00+00:00', '{TaskSources.Api}', 'text', 'фраза', " +
        "'add_task', 0.9, 1, 'rules-v1');";

    private static string InsertTaskTagSql(string taskId) =>
        "INSERT INTO task_tags (task_id, tag, source, created_at) VALUES " +
        $"('{taskId}', 'старый', '{TaskSources.Api}', '2026-11-06T10:00:00+00:00');";

    private static string InsertActionSql(string id, string classificationId) =>
        "INSERT INTO actions (id, created_at, classification_id, source, kind, outcome) VALUES " +
        $"('{id}', '2026-11-06T10:00:00+00:00', '{classificationId}', '{TaskSources.Api}', 'classify', 'ok');";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(DatabaseInitializer.BuildConnectionString(_databasePath));
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private int ReadUserVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private IReadOnlyList<string> ReadIds(string table)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {table} ORDER BY id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private string ReadSingleString(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
