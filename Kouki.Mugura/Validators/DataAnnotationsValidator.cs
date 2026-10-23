using Kouki.Mugura.Commands;

using Kouki.Mugura.Registry;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Kouki.Mugura.Validators;

/// <summary>
/// Валидатор на основе DataAnnotations, использующий правила из <see cref="CommandRegistry"/>.
/// </summary>
/// <typeparam name="TCommand">Тип DTO команды (должен быть классом, реализующим <see cref="ICommand"/>).</typeparam>
/// <remarks>
/// <para>
/// В конструкторе получает <see cref="CommandRegistry"/>, извлекает для данного типа команды
/// список <see cref="ValidationRule"/> и сохраняет их для последующей проверки.
/// </para>
/// <para>
/// При вызове <see cref="TryValidate"/> перебирает все правила и для каждого свойства
/// вызывает метод <see cref="ValidationAttribute.IsValid(object?, ValidationContext?)"/>.
/// Если хотя бы одно правило нарушено, возвращает false и сообщение об ошибке.
/// </para>
/// <para>
/// Кастомные атрибуты валидации (наследники <see cref="ValidationAttribute"/>) работают 
/// автоматически — достаточно применить их к свойствам DTO.
/// </para>
/// <para>
/// Чтобы переопределить валидацию для конкретной команды, зарегистрируйте свою реализацию 
/// <see cref="ICommandValidator{TCommand}"/> в DI-контейнере после вызова <see cref="DependencyInjection.ServiceCollectionExtensions.AddMugura"/>.
/// </para>
/// </remarks>
public class DataAnnotationsValidator<TCommand> : ICommandValidator<TCommand>
    where TCommand : class, ICommand
{
    private readonly (PropertyInfo Property, ValidationAttribute Attribute)[] _rules;

    public DataAnnotationsValidator(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var entry = registry.GetEntryByCommandType(typeof(TCommand));
        var validationRules = entry?.ValidationRules ?? Array.Empty<ValidationRule>();

        // PropertyInfo резолвится один раз: валидатор живёт в скоупе и вызывается на каждый запрос.
        _rules = validationRules
            .Select(rule => (Property: typeof(TCommand).GetProperty(rule.PropertyName), rule.Attribute))
            .Where(static rule => rule.Property is not null)
            .Select(static rule => (rule.Property!, rule.Attribute))
            .ToArray();
    }

    public bool TryValidate(TCommand command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<string>? errors = null;
        foreach (var (property, attribute) in _rules)
        {
            var value = property.GetValue(command);
            if (!attribute.IsValid(value))
            {
                (errors ??= new List<string>()).Add(attribute.FormatErrorMessage(property.Name));
            }
        }

        if (errors is { Count: > 0 })
        {
            error = string.Join("; ", errors);
            return false;
        }

        error = null;
        return true;
    }
}