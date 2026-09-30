using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kouki.Mugura.Commands;
using Shimi.Context;

namespace Kouki.Mugura.Handlers;

/// <summary>
/// Обработчик команды. Возвращает результат в виде JsonNode (универсальный формат).
/// </summary>
/// <typeparam name="TCommand">Тип команды (DTO).</typeparam>
public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    Task<JsonNode> HandleAsync(TCommand command, IExecutionContext? context = null, CancellationToken cancellationToken = default);
}