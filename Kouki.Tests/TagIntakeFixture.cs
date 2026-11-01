using Kouki.Kagedama;
using Kouki.Kagedama.Commands;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Mugura.DependencyInjection;
using Kouki.Shimi.Context;
using Kouki.Shimi.DependencyInjection;
using Kouki.Yasabi;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Сервер на временной базе с хранилищем тегов и единицей работы — для проверок тегов в приёме.
/// </summary>
/// <remarks>
/// Отличается от фикстуры <see cref="IntakeTests"/> именно тегами: приём получает
/// <see cref="ITagStore"/> и <see cref="IUnitOfWork"/>, иначе транзакционность тегов не проверить.
/// </remarks>
internal sealed class TagIntakeFixture : IDisposable
{
    /// <summary>Готовит чистую базу и контейнер.</summary>
    /// <param name="classifier">Классификатор вместо правил: так в тест попадают теги модели.</param>
    /// <param name="options">Настройки приёма: режим тегов, порог, источник по умолчанию.</param>
    /// <param name="tags">Хранилище тегов вместо настоящего: например, всегда падающее.</param>
    public TagIntakeFixture(
        IIntentClassifier? classifier = null,
        IntakeOptions? options = null,
        ITagStore? tags = null)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "kouki-tag-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "kouki.sqlite");
        DatabaseInitializer.Initialize(DatabasePath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMugura(typeof(Kouki.Ubusuna.Commands.PingHandler).Assembly, typeof(ListTasksHandler).Assembly);
        services.AddShimiContext();

        // Свой классификатор регистрируем до AddYasabi: там TryAddSingleton, первый и побеждает.
        if (classifier is not null)
        {
            services.AddSingleton(classifier);
        }

        services.AddYasabi(options => options.Source = "test");
        services.AddKagedamaData(DatabasePath);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(TimeZoneInfo.Local);
        services.AddSingleton(sp => new TaskIntakeService(
            sp.GetRequiredService<IIntentClassifier>(),
            sp.GetRequiredService<IIntentRouter>(),
            sp.GetRequiredService<ITaskStore>(),
            sp.GetRequiredService<IActionLog>(),
            sp.GetRequiredService<IContextRegistry>(),
            options ?? new IntakeOptions
            {
                ConfidenceThreshold = 0.75,
                DefaultSource = "test",
            },
            sp.GetRequiredService<IClassificationJournal>(),
            sp.GetRequiredService<IUnitOfWork>(),
            tags ?? sp.GetRequiredService<ITagStore>()));

        Provider = services.BuildServiceProvider();
        Provider.GetRequiredService<IContextRegistry>().SetDefaultContext(TestHost.Context("server"));
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public ServiceProvider Provider { get; }

    public TaskIntakeService Intake => Provider.GetRequiredService<TaskIntakeService>();

    public ITaskStore Store => Provider.GetRequiredService<ITaskStore>();

    public ITagStore Tags => Provider.GetRequiredService<ITagStore>();

    public IClassificationJournal Journal => Provider.GetRequiredService<IClassificationJournal>();

    public IActionLog Actions => Provider.GetRequiredService<IActionLog>();

    /// <summary>Создаёт задачу в обход команд — когда тест проверяет чтение, а не приём.</summary>
    public async Task<TaskRecord> NewTaskAsync(string title, params string[] tags)
    {
        TaskRecord task = Kouki.Kagedama.TaskFactory.Create(title, TaskSources.Api);
        await Store.AddAsync(task);
        if (tags.Length > 0)
        {
            await Tags.AddAsync(task.Id, tags, TagSources.Text);
        }

        return task;
    }

    /// <summary>Теги задачи вместе с источником: из них видно, кто их поставил.</summary>
    public async Task<IReadOnlyList<(string Tag, string Source)>> TagRowsAsync(Guid taskId)
    {
        List<(string, string)> rows = new();
        await using SqliteConnection connection = new($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tag, source FROM task_tags WHERE task_id = $id ORDER BY rowid";
        command.Parameters.AddWithValue("$id", taskId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>Задача, к которой журнал привязал запись разбора (<c>classifications.task_id</c>).</summary>
    public async Task<string?> LastLinkedTaskIdAsync()
    {
        await using SqliteConnection connection = new($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT task_id FROM classifications ORDER BY rowid DESC LIMIT 1";
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>Сколько строк в таблице: по нему видно, откатилась ли транзакция.</summary>
    public async Task<long> CountAsync(string table)
    {
        await using SqliteConnection connection = new($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Provider.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — тест от этого не зависит.
        }
    }
}
