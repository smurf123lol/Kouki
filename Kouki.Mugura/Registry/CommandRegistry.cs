using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Kouki.Mugura.Registry;
/// <summary>
/// Реестр команд, построенный путём сканирования сборок.
/// </summary>
/// <remarks>
/// Сканирование выполняется в конструкторе один раз. Находит классы с <see cref="CommandAttribute"/>,
/// проверяет реализацию <see cref="ICommandHandler{ICommand}"/>, извлекает DTO и правила валидации.
/// Предоставляет доступ к записям по имени команды и по типу DTO.
/// </remarks>
public sealed class CommandRegistry
{
    private readonly ImmutableDictionary<string, CommandEntry> _entriesByName;
    private readonly ImmutableDictionary<Type, CommandEntry> _entriesByType;

    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_entriesByName.Keys;

    public CommandRegistry(IEnumerable<Assembly> assemblies)
    {
        var builderByName = ImmutableDictionary.CreateBuilder<string, CommandEntry>();
        var builderByType = ImmutableDictionary.CreateBuilder<Type, CommandEntry>();

        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                var attr = type.GetCustomAttribute<CommandAttribute>();
                if (attr == null) continue;

                var handlerInterface = type.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<>));
                if (handlerInterface == null) continue;

                var commandType = handlerInterface.GetGenericArguments()[0];
                if (!typeof(ICommand).IsAssignableFrom(commandType)) continue;

                var entry = new CommandEntry(
                    CommandType: commandType,
                    HandlerType: type,
                    Name: attr.Name,
                    Description: attr.Description,
                    RequiresConfirmation: attr.RequiresConfirmation,
                    Category: attr.Category,
                    ValidationRules: ExtractValidationRules(commandType).AsReadOnly(),
                    Example: attr.Example
                );

                builderByName[attr.Name] = entry;
                builderByType[commandType] = entry;
            }
        }

        _entriesByName = builderByName.ToImmutable();
        _entriesByType = builderByType.ToImmutable();
    }

    public CommandEntry? GetEntry(string name) => _entriesByName.GetValueOrDefault(name);

    public CommandEntry? GetEntryByCommandType(Type commandType) => _entriesByType.GetValueOrDefault(commandType);

    private static List<ValidationRule> ExtractValidationRules(Type commandType)
    {
        var rules = new List<ValidationRule>();
        foreach (var prop in commandType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attrs = prop.GetCustomAttributes<ValidationAttribute>(true);
            foreach (var attr in attrs)
            {
                rules.Add(new ValidationRule(prop.Name, attr));
            }
        }
        return rules;
    }
}