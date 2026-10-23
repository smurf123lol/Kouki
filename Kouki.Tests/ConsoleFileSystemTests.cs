using Kouki.Ubusuna.Adapters;

namespace Kouki.Tests;

/// <summary>Проверки песочницы файловой системы (защита от выхода за корень).</summary>
public sealed class ConsoleFileSystemTests : IDisposable
{
    private readonly string _root;

    public ConsoleFileSystemTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "fs-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Reads_and_writes_inside_root()
    {
        var fileSystem = new ConsoleFileSystem(_root);

        await fileSystem.WriteTextAsync("note.txt", "hello");

        Assert.Equal("hello", await fileSystem.ReadTextAsync("note.txt"));
        Assert.True(await fileSystem.ExistsAsync("note.txt"));
    }

    [Fact]
    public async Task Rejects_paths_outside_root()
    {
        var fileSystem = new ConsoleFileSystem(_root);

        string[] escapingPaths =
        {
            "../outside.txt",
            Path.Combine("sub", "..", "..", "outside.txt"),
            Path.Combine(Path.GetPathRoot(_root)!, "Windows", "win.ini")
        };

        foreach (var path in escapingPaths)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fileSystem.ReadBytesAsync(path));
    }

    [Fact]
    public async Task Allows_nested_paths_inside_root()
    {
        var fileSystem = new ConsoleFileSystem(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        await fileSystem.WriteTextAsync(Path.Combine("sub", "deep.txt"), "ok");

        Assert.Equal("ok", await fileSystem.ReadTextAsync(Path.Combine("sub", "deep.txt")));
    }
}
