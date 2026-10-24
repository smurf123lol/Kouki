using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kouki.Tests;

public sealed class AudioRetentionTests : IDisposable
{
    private readonly string _root;

    public AudioRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kouki-audio-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог, уборка не критична
        }
    }

    [Fact]
    public async Task Removes_files_older_than_cutoff_and_keeps_fresh()
    {
        FileAudioStore store = new(_root, NullClassificationJournal.Instance, NullLogger<FileAudioStore>.Instance);

        string oldFile = Path.Combine(_root, "old.wav");
        string freshFile = Path.Combine(_root, "fresh.wav");
        await File.WriteAllBytesAsync(oldFile, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(freshFile, new byte[] { 4, 5, 6 });

        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));

        int removed = await store.RemoveOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task Missing_directory_returns_zero()
    {
        FileAudioStore store = new(
            Path.Combine(_root, "нет-такого-каталога"),
            NullClassificationJournal.Instance,
            NullLogger<FileAudioStore>.Instance);

        int removed = await store.RemoveOlderThanAsync(DateTimeOffset.UtcNow);

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task Retention_keeps_everything_when_cutoff_is_in_the_past()
    {
        FileAudioStore store = new(_root, NullClassificationJournal.Instance, NullLogger<FileAudioStore>.Instance);
        string file = Path.Combine(_root, "recent.wav");
        await File.WriteAllBytesAsync(file, new byte[] { 1 });

        int removed = await store.RemoveOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(0, removed);
        Assert.True(File.Exists(file));
    }
}
