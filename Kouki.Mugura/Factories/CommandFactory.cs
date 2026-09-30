using Kouki.Mugura.Commands;
using Kouki.Mugura.Registry;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Factories;
/// <summary>
/// Реализация <see cref="ICommandFactory"/>, создающая DTO команды из <see cref="JsonNode"/>.
/// </summary>
/// <remarks>
/// <para>
/// Для десериализации используется <see cref="System.Text.Json"/> с кешированием делегатов для каждого типа команды.
/// </para>
/// <para>
/// Если аргумент <paramref name="argument"/> равен null, он заменяется на пустой <see cref="JsonObject"/>.
/// </para>
/// <para>
/// При ошибке десериализации возвращается <c>null</c> и заполняется сообщение об ошибке.
/// </para>
/// <para>
/// Фабрика не зависит от контекста выполнения и не должна его учитывать — это задача роутера и хендлера.
/// </para>
/// </remarks>
public class CommandFactory : ICommandFactory
{
    public ICommand? Create(CommandEntry entry, JsonNode? argument)
    {
        return Activator.CreateInstance(entry.CommandType, argument) as ICommand;
    }
}