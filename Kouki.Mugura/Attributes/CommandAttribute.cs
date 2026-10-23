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

    public CommandAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
