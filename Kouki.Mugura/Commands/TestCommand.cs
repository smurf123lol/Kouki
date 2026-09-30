using Kouki.Mugura.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Kouki.Mugura.Commands;

[Command(
    "test",
    Category = CommandCategory.Uncategorized,
    Description = "just a test command",
    RequiresConfirmation = false)]
public record TestCommand(
    [Required(ErrorMessage = "Value is required")]
    [StringLength(10, ErrorMessage = "Value too long")]
    string? Value
) : ICommand;