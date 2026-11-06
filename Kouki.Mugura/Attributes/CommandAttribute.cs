namespace Kouki.Mugura.Attributes;

/// <summary>
/// Помечает класс-хендлер как команду. Имя должно быть уникальным в пределах реестра.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    public string Name { get; }
    public string? Description { get; set; }
    public bool RequiresConfirmation { get; set; }
    public CommandCategory Category { get; set; } = CommandCategory.Uncategorized;
    public string? Example { get; set; }

    /// <summary>
    /// Путь к файлу фраз команды — относительно каталога приложения
    /// (<see cref="AppContext.BaseDirectory"/>), например <c>"Phrases/add_task.txt"</c>.
    /// </summary>
    /// <remarks>
    /// Файл — материал для обучения классификатора интентов, а не рантайм-правила:
    /// ядро путь не открывает и содержимое не разбирает, а передаёт его потребителю
    /// через <see cref="Registry.CommandEntry.PhrasesFile"/>. <c>null</c> — фраз у команды нет.
    /// </remarks>
    public string? PhrasesFile { get; set; }

    public CommandAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
