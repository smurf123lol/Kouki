using System.Text.Json.Nodes;
using Kouki.Mugura.Commands;
using Kouki.Shimi.Context;

namespace Kouki.Mugura.Dispatcher;

/// <summary>
/// Выполняет типизированную команду в изолированном DI-скоупе.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Отправить команду хендлеру. <paramref name="context"/> обязателен:
    /// именно он даёт хендлеру доступ к файловой системе, терминалу и состоянию.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Если <paramref name="command"/> или <paramref name="context"/> равен <c>null</c>.
    /// </exception>
    Task<JsonNode> SendAsync<TCommand>(TCommand command, IExecutionContext context, CancellationToken cancellationToken = default) where TCommand : ICommand;
}
