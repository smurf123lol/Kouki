using Dapper;
using Microsoft.Data.Sqlite;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Хранилище задач в SQLite (файл <c>kouki.sqlite</c>).
/// </summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private const string SelectColumns =
        "id AS Id, created_at AS CreatedAt, updated_at AS UpdatedAt, source AS Source, " +
        "raw_text AS RawText, title AS Title, status AS Status, priority AS Priority, " +
        "workspace AS Workspace, meta_json AS MetaJson, " +
        "parent_id AS ParentId, tree_id AS TreeId, position AS Position";

    private const string SubtreeSql = """
        WITH RECURSIVE subtree(id) AS (
            SELECT id FROM tasks WHERE id = @id
            UNION ALL
            SELECT child.id FROM tasks child JOIN subtree parent ON child.parent_id = parent.id
        )
        """;

    private readonly SqliteSession _session;
    private readonly IUnitOfWork _work;

    /// <summary>
    /// Создаёт хранилище с собственной сессией (для тестов и утилит).
    /// </summary>
    /// <param name="databasePath">Путь к файлу базы.</param>
    public SqliteTaskStore(string databasePath)
        : this(new SqliteSession(databasePath))
    {
    }

    /// <summary>
    /// Создаёт хранилище поверх общей сессии: тогда транзакция приёма охватывает все хранилища.
    /// </summary>
    /// <param name="session">Сессия базы.</param>
    public SqliteTaskStore(SqliteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        // Задача с деревом — это две таблицы, поэтому запись идёт через единицу работы
        // этой же сессии: своего соединения хранилище не открывает.
        _work = new SqliteUnitOfWork(session);
    }

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath => _session.DatabasePath;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken = default,
        bool topLevel = false,
        Guid? parentId = null)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<TaskRecord> rows = await lease.Connection.QueryAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks " +
                "WHERE (@status IS NULL OR status = @status) " +
                "AND (NOT @topLevel OR parent_id IS NULL) " +
                "AND (@parentId IS NULL OR parent_id = @parentId) " +
                "ORDER BY position ASC, created_at DESC, id ASC LIMIT @limit",
                new { status, limit, topLevel, parentId = parentId?.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await lease.Connection.QuerySingleOrDefaultAsync<TaskRecord>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM tasks WHERE id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AddAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.ParentId == record.Id)
        {
            throw new ArgumentException("Задача не может быть родителем самой себе.", nameof(record));
        }

        return _work.RunAsync(
            async token =>
            {
                await using SqliteLease lease = await _session.OpenAsync(token).ConfigureAwait(false);

                Guid treeId = record.ParentId is { } parentId
                    ? await ResolveTreeOfParentAsync(lease, parentId, token).ConfigureAwait(false)
                    : record.Id;

                // Следующий номер в паре (status, parent_id) считается в той же транзакции,
                // что и вставка: иначе две одновременные задачи получат один и тот же номер.
                int position = await lease.Connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COALESCE(MAX(position) + 1, @first) FROM tasks " +
                        "WHERE status = @status AND parent_id IS @parentId",
                        new
                        {
                            status = record.Status,
                            parentId = record.ParentId?.ToString(),
                            first = TaskPositions.First,
                        },
                        transaction: lease.Transaction,
                        cancellationToken: token)).ConfigureAwait(false);

                // Корень — начало дерева: строка дерева появляется вместе с задачей.
                if (record.ParentId is null)
                {
                    await lease.Connection.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT OR IGNORE INTO task_trees (id, created_at) VALUES (@id, @createdAt)",
                            new { id = record.Id.ToString(), createdAt = record.CreatedAt },
                            transaction: lease.Transaction,
                            cancellationToken: token)).ConfigureAwait(false);
                }

                await lease.Connection.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO tasks " +
                        "(id, created_at, updated_at, source, raw_text, title, status, priority, " +
                        "workspace, meta_json, parent_id, tree_id, position) " +
                        "VALUES (@id, @createdAt, @updatedAt, @source, @rawText, @title, @status, " +
                        "@priority, @workspace, @metaJson, @parentId, @treeId, @position)",
                        new DynamicParameters(new Dictionary<string, object?>
                        {
                            ["id"] = record.Id.ToString(),
                            ["createdAt"] = record.CreatedAt,
                            ["updatedAt"] = record.UpdatedAt,
                            ["source"] = record.Source,
                            ["rawText"] = record.RawText,
                            ["title"] = record.Title,
                            ["status"] = record.Status,
                            ["priority"] = record.Priority,
                            ["workspace"] = record.Workspace,
                            ["metaJson"] = record.MetaJson,
                            ["parentId"] = record.ParentId?.ToString(),
                            ["treeId"] = treeId.ToString(),
                            ["position"] = position,
                        }),
                        transaction: lease.Transaction,
                        cancellationToken: token)).ConfigureAwait(false);

                // Вызывающий получает вычисленные поля: они нужны ответу add_task и TaskDto.
                record.TreeId = treeId;
                record.Position = position;
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Новые поля (<c>parent_id</c>, <c>tree_id</c>, <c>position</c>) здесь не меняются:
    /// перенос карточки — отдельная операция, а не частичное обновление.
    /// </remarks>
    public async Task<bool> UpdateAsync(
        Guid id,
        string? status,
        string? title,
        int? priority,
        string? metaJson = null,
        CancellationToken cancellationToken = default)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected = await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tasks SET " +
                "status = COALESCE(@status, status), " +
                "title = COALESCE(@title, title), " +
                "priority = COALESCE(@priority, priority), " +
                "meta_json = COALESCE(@metaJson, meta_json), " +
                "updated_at = @updatedAt " +
                "WHERE id = @id",
                new
                {
                    id = id.ToString(),
                    status,
                    title,
                    priority,
                    metaJson,
                    updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
    }

    /// <inheritdoc />
    public Task<TaskDeleteResult> DeleteAsync(
        Guid id,
        TaskDeleteMode mode = TaskDeleteMode.Cascade,
        CancellationToken cancellationToken = default)
        => _work.RunAsync(
            token => mode switch
            {
                TaskDeleteMode.Cascade => DeleteCascadeAsync(id, token),
                TaskDeleteMode.Detach => DeleteDetachAsync(id, token),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим удаления."),
            },
            cancellationToken);

    /// <summary>Каскад: всё поддерево по <c>parent_id</c>.</summary>
    private async Task<TaskDeleteResult> DeleteCascadeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        string[] ids = (await lease.Connection.QueryAsync<string>(
            new CommandDefinition(
                SubtreeSql + " SELECT id FROM subtree",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        if (ids.Length == 0)
        {
            return TaskDeleteResult.NotFound;
        }

        // Деревья берём до удаления задач: после DELETE их уже не найти по tree_id.
        string[] treeIds = (await lease.Connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT DISTINCT tree_id FROM tasks WHERE id IN @ids AND tree_id IS NOT NULL",
                new { ids },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        await DeleteReferencesAsync(lease, ids, cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM tasks WHERE id IN @ids",
                new { ids },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await DeleteEmptyTreesAsync(lease, treeIds, cancellationToken).ConfigureAwait(false);

        return new TaskDeleteResult(true, ids.Length - 1);
    }

    /// <summary>Отвязывание: удаляется только задача, прямые дети остаются в дереве.</summary>
    private async Task<TaskDeleteResult> DeleteDetachAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqliteLease lease = await _session.OpenAsync(cancellationToken).ConfigureAwait(false);

        string? treeId = await lease.Connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT tree_id FROM tasks WHERE id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(treeId))
        {
            return TaskDeleteResult.NotFound;
        }

        int detached = await lease.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM tasks WHERE parent_id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Сначала отвязываем детей: иначе FK «parent_id → tasks.id» не даст удалить родителя.
        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tasks SET parent_id = NULL WHERE parent_id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await DeleteReferencesAsync(lease, new[] { id.ToString() }, cancellationToken).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM tasks WHERE id = @id",
                new { id = id.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await DeleteEmptyTreesAsync(lease, new[] { treeId }, cancellationToken).ConfigureAwait(false);

        return new TaskDeleteResult(true, detached);
    }

    /// <summary>
    /// Обнуляет ссылки на удаляемые задачи: действия и классификации — аудит и датасет,
    /// их записи остаются; напоминания без задачи бессмысленны и удаляются.
    /// </summary>
    private static async Task DeleteReferencesAsync(
        SqliteLease lease,
        string[] ids,
        CancellationToken cancellationToken)
    {
        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM reminders WHERE task_id IN @ids",
                new { ids },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE actions SET task_id = NULL WHERE task_id IN @ids",
                new { ids },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE classifications SET task_id = NULL WHERE task_id IN @ids",
                new { ids },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Удаляет строки <c>task_trees</c>, в которых не осталось задач.</summary>
    private static async Task DeleteEmptyTreesAsync(
        SqliteLease lease,
        string[] treeIds,
        CancellationToken cancellationToken)
    {
        if (treeIds.Length == 0)
        {
            return;
        }

        await lease.Connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM task_trees WHERE id IN @ids " +
                "AND NOT EXISTS (SELECT 1 FROM tasks WHERE tasks.tree_id = task_trees.id)",
                new { ids = treeIds },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Дерево родителя: подзадача всегда живёт в дереве родителя.</summary>
    private static async Task<Guid> ResolveTreeOfParentAsync(
        SqliteLease lease,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        string? treeId = await lease.Connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT tree_id FROM tasks WHERE id = @id",
                new { id = parentId.ToString() },
                transaction: lease.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (string.IsNullOrEmpty(treeId))
        {
            throw new InvalidOperationException("Родительская задача не найдена.");
        }

        return Guid.Parse(treeId);
    }
}
