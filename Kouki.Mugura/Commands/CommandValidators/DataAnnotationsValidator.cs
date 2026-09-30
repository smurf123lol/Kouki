
using Kouki.Mugura.Registry;
using System.ComponentModel.DataAnnotations;

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
    private readonly IReadOnlyList<ValidationRule> _validationRules;

    public DataAnnotationsValidator(CommandRegistry registry)
    {
        // Предполагается, что в CommandRegistry есть метод GetEntryByCommandType
        var entry = registry.GetEntryByCommandType(typeof(TCommand));
        _validationRules = entry?.ValidationRules ?? Array.Empty<ValidationRule>();
    }

    public bool TryValidate(TCommand command, out string? error)
    {
        var errors = new List<string>();
        foreach (var rule in _validationRules)
        {
            var prop = typeof(TCommand).GetProperty(rule.PropertyName);
            if (prop == null) continue;
            var value = prop.GetValue(command);
            if (!rule.Attribute.IsValid(value))
            {
                errors.Add(rule.Attribute.FormatErrorMessage(rule.PropertyName));
            }
        }

        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return false;
        }

        error = null;
        return true;
    }
}