namespace Kouki.Mugura.Commands;

/// <summary>
/// Маркерный интерфейс команды: DTO, который описывает аргументы вызова.
/// </summary>
/// <remarks>
/// Реализация должна быть создаваема из JSON (public-конструктор или init-свойства),
/// см. <c>ICommandFactory.TryCreate</c>.
/// </remarks>
public interface ICommand
{
}
