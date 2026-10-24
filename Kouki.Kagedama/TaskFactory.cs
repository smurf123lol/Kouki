using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama;

/// <summary>
/// Единственное место, где из фразы рождается запись задачи.
/// </summary>
/// <remarks>
/// Раньше задача собиралась в двух местах — в приёме и в обработчике <c>add_task</c>,
/// и они успели разойтись (у одного была мета, у другого нет). Любая новая ветка
/// создания задачи должна идти через <see cref="Create"/>, чтобы поля не разъезжались.
/// </remarks>
public static class TaskFactory
{
    /// <summary>Приоритет задачи по умолчанию: 2 — обычный.</summary>
    public const int DefaultPriority = 2;

    /// <summary>
    /// Создаёт запись задачи из текста фразы.
    /// </summary>
    /// <param name="text">Фраза человека.</param>
    /// <param name="source">Источник, см. <see cref="TaskSources"/>.</param>
    /// <param name="status">Начальный статус, см. <see cref="TaskStatuses"/>.</param>
    /// <param name="metaJson">Служебная мета или null.</param>
    /// <param name="now">Момент создания; по умолчанию — текущий.</param>
    public static TaskRecord Create(
        string text,
        string source,
        string status = TaskStatuses.New,
        string? metaJson = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string trimmed = text.Trim();
        DateTimeOffset stamp = now ?? DateTimeOffset.UtcNow;
        string created = stamp.ToString("O");

        return new TaskRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = created,
            UpdatedAt = created,
            Source = source.Trim(),
            RawText = trimmed,
            Title = TaskTitles.Make(trimmed),
            Status = status,
            Priority = DefaultPriority,
            MetaJson = metaJson,
        };
    }
}
