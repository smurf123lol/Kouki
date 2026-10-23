using Kouki.Kagedama.Data;

namespace Kouki.Tests;

public sealed class SqliteStateStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteStateStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "kouki.sqlite");
        DatabaseInitializer.Initialize(_databasePath);
    }

    [Fact]
    public async Task Set_and_get_string_roundtrip()
    {
        var store = new SqliteStateStore(_databasePath);

        await store.SetAsync("station:last-command", "покажи задачи");

        Assert.Equal("покажи задачи", await store.GetAsync<string>("station:last-command"));
    }

    [Fact]
    public async Task Get_of_missing_key_returns_default()
    {
        var store = new SqliteStateStore(_databasePath);

        Assert.Null(await store.GetAsync<string>("station:never-written"));
        Assert.Equal(0, await store.GetAsync<int>("station:never-written-number"));
    }

    [Fact]
    public async Task Complex_value_roundtrips_through_json()
    {
        var store = new SqliteStateStore(_databasePath);
        var value = new Snapshot("server", 3, new[] { "add_task", "list_tasks" });

        await store.SetAsync("last:intake", value);

        Snapshot? loaded = await store.GetAsync<Snapshot>("last:intake");

        Assert.NotNull(loaded);
        Assert.Equal("server", loaded!.Source);
        Assert.Equal(3, loaded.TaskCount);
        Assert.Equal(new[] { "add_task", "list_tasks" }, loaded.Intents);
    }

    [Fact]
    public async Task Set_overwrites_previous_value()
    {
        var store = new SqliteStateStore(_databasePath);

        await store.SetAsync("station:last-command", "раз");
        await store.SetAsync("station:last-command", "два");

        Assert.Equal("два", await store.GetAsync<string>("station:last-command"));
    }

    [Fact]
    public async Task Remove_deletes_value()
    {
        var store = new SqliteStateStore(_databasePath);
        await store.SetAsync("station:last-command", "покажи задачи");

        await store.RemoveAsync("station:last-command");

        Assert.Null(await store.GetAsync<string>("station:last-command"));
    }

    [Fact]
    public async Task State_survives_new_store_instance_and_schema_rerun()
    {
        await new SqliteStateStore(_databasePath).SetAsync("station:last-command", "покажи задачи");

        // Повторная инициализация схемы не должна стирать состояние.
        DatabaseInitializer.Initialize(_databasePath);

        Assert.Equal("покажи задачи", await new SqliteStateStore(_databasePath).GetAsync<string>("station:last-command"));
    }

    [Fact]
    public async Task Blank_key_is_rejected()
    {
        var store = new SqliteStateStore(_databasePath);

        foreach (string key in new[] { string.Empty, "   " })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync<string>(key));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(key, "x"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.RemoveAsync(key));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл может быть ещё занят — временный каталог удалит система.
        }
    }

    internal sealed record Snapshot(string Source, int TaskCount, string[] Intents);
}
