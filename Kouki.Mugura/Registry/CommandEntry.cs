using Kouki.Mugura.Attributes;
using System.ComponentModel.DataAnnotations;

namespace Kouki.Mugura.Registry;


/// <summary>
/// Запись параметров команд.
/// </summary>
/// <remarks>
/// <c>PhrasesFile</c> — путь к файлу фраз команды (для сборки обучающего датасета),
/// относительный от каталога приложения. Ядро его не читает: это данные для потребителя.
/// </remarks>
public sealed record CommandEntry(
    Type CommandType,
    Type HandlerType,
    string Name,
    string? Description,
    bool RequiresConfirmation,
    CommandCategory Category,
    IReadOnlyList<ValidationRule> ValidationRules,
    string? Example,
    string? PhrasesFile = null
);

/// <summary>
/// Правило валидации для одного свойства DTO команды.
/// </summary>
/// <remarks>
/// Содержит имя свойства и экземпляр <see cref="ValidationAttribute"/>.
/// Используется <see cref="Validators.DataAnnotationsValidator{TCommand}"/> для проверки.
/// </remarks>
public sealed record ValidationRule(
string PropertyName,
ValidationAttribute Attribute);