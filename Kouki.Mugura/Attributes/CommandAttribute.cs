namespace Kouki.Mugura.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    public string Name { get; }
    public string? Description { get; set; }
    public bool RequiresConfirmation { get; set; }
    public CommandCategory Category { get; set; } = CommandCategory.Uncategorized;
    public string? Example { get; set; }

    public CommandAttribute(string name) => Name = name;
}