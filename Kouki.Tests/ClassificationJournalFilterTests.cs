using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;

namespace Kouki.Tests;

/// <summary>
/// Фильтр журнала разбора по задаче — прямой тест хранилища, без HTTP и команд.
/// </summary>
public sealed class ClassificationJournalFilterTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteClassificationJournal _journal;

    /// <summary>Временная база на тест: схема та же, что у боевого инициализатора.</summary>
    public ClassificationJournalFilterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-journal-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        string databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(databasePath);
        _journal = new SqliteClassificationJournal(databasePath);
    }

    [Fact]
    public async Task ListAsync_filters_by_task_and_keeps_unresolved_condition()
    {
        // Две записи привязаны к задаче (одна неразмеченная, одна уверенная) и одна
        // неразмеченная запись без задачи. Так видно оба прошлых дефекта: фильтр по задаче
        // не должен ни терять условие «неразмеченные», ни перебивать его собой.
        Guid target = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();

        Guid unresolvedTarget = await RecordAsync("покажи задачи по дому", Confident: false);
        Guid resolvedTarget = await RecordAsync("покажи все задачи", Confident: true);
        Guid unresolvedForeign = await RecordAsync("погода завтра", Confident: false);

        Assert.True(await _journal.LinkToTaskAsync(unresolvedTarget, target));
        Assert.True(await _journal.LinkToTaskAsync(resolvedTarget, target));
        Assert.True(await _journal.LinkToTaskAsync(unresolvedForeign, foreign));

        IReadOnlyList<ClassificationEntry> byTask = await _journal.ListAsync(
            limit: 10,
            onlyUnresolved: false,
            taskId: target);

        Assert.Equal(2, byTask.Count);
        Assert.All(byTask, entry => Assert.Equal(target, entry.TaskId));

        IReadOnlyList<ClassificationEntry> unresolvedByTask = await _journal.ListAsync(
            limit: 10,
            onlyUnresolved: true,
            taskId: target);

        ClassificationEntry single = Assert.Single(unresolvedByTask);
        Assert.Equal(unresolvedTarget, single.Id);
    }

    [Fact]
    public async Task GetAsync_returns_entry_or_null_for_missing()
    {
        Guid id = await RecordAsync("найди эту запись", Confident: false);

        ClassificationEntry? found = await _journal.GetAsync(id);

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
        Assert.Equal("найди эту запись", found.InputText);

        Assert.Null(await _journal.GetAsync(Guid.NewGuid()));
    }

    private Task<Guid> RecordAsync(string text, bool Confident)
        => _journal.RecordAsync(new ClassificationRecord(
            TaskSources.Api,
            "text",
            text,
            "unclassified",
            0,
            "rules-v1",
            Confident: Confident,
            Error: null));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка под стенд: не удалилась — не повод ронять тест.
        }
    }
}
