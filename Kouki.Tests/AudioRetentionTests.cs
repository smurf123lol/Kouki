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

    [Fact]
    public async Task Labeled_audio_is_removed_only_after_its_own_cutoff()
    {
        string oldPath = Path.Combine(_root, "labeled-old.wav");
        string freshPath = Path.Combine(_root, "labeled-fresh.wav");
        await File.WriteAllBytesAsync(oldPath, new byte[] { 1 });
        await File.WriteAllBytesAsync(freshPath, new byte[] { 2 });

        var journal = new ListJournal(
            new ClassificationEntry
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-200).ToString("O"),
                LabeledAt = DateTimeOffset.UtcNow.AddDays(-120).ToString("O"),
                AudioPath = oldPath,
            },
            new ClassificationEntry
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10).ToString("O"),
                LabeledAt = DateTimeOffset.UtcNow.AddDays(-2).ToString("O"),
                AudioPath = freshPath,
            });

        FileAudioStore store = new(_root, journal, NullLogger<FileAudioStore>.Instance);

        int removed = await store.RemoveLabeledAsync(DateTimeOffset.UtcNow.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public async Task Labeled_audio_without_date_is_treated_as_old()
    {
        string path = Path.Combine(_root, "labeled-undated.wav");
        await File.WriteAllBytesAsync(path, new byte[] { 1 });

        var journal = new ListJournal(new ClassificationEntry
        {
            Id = Guid.NewGuid(),
            CreatedAt = string.Empty,
            Label = "add_task",
            LabeledAt = null,
            AudioPath = path,
        });

        FileAudioStore store = new(_root, journal, NullLogger<FileAudioStore>.Instance);

        int removed = await store.RemoveLabeledAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(path));
    }

    private sealed class ListJournal(params ClassificationEntry[] entries) : IClassificationJournal
    {
        public Task<Guid> RecordAsync(ClassificationRecord record, CancellationToken cancellationToken = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<IReadOnlyList<ClassificationEntry>> ListAsync(
            int limit,
            bool onlyUnresolved,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ClassificationEntry>>(entries);

        public Task<bool> AnnotateAudioAsync(
            Guid id,
            double? asrConfidence,
            string? audioPath,
            CancellationToken cancellationToken = default,
            string? source = null)
            => Task.FromResult(true);

        public Task<bool> AnnotateHintAsync(Guid id, string? hintedIntent, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> LinkToTaskAsync(Guid id, Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ClassificationEntry>> ListLabeledAsync(
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ClassificationEntry>>(
                entries.Where(e => e.Label is not null || e.LabeledAt is not null).ToArray());

        public Task<bool> LabelAsync(Guid id, string label, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
