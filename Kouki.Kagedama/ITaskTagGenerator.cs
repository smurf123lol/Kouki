namespace Kouki.Kagedama;

/// <summary>
/// Генератор автотегов для задачи на основе заголовка и исходного текста.
/// </summary>
public interface ITaskTagGenerator
{
    /// <summary>
    /// Извлекает список коротких тегов (в нижнем регистре, без '#').
    /// </summary>
    Task<IReadOnlyList<string>> GenerateTagsAsync(string title, string rawText, CancellationToken cancellationToken = default);
}

/// <summary>
/// Заглушка по умолчанию (не генерирует теги).
/// </summary>
public sealed class NullTaskTagGenerator : ITaskTagGenerator
{
    public static readonly NullTaskTagGenerator Instance = new();

    private NullTaskTagGenerator() { }

    public Task<IReadOnlyList<string>> GenerateTagsAsync(string title, string rawText, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
