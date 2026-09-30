using Shimi.Context;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Dispatcher;

/// <summary>
/// Базовый класс для роутеров-обёрток, которые делегируют выполнение другому роутеру.
/// </summary>
/// <remarks>
/// Используйте этот класс, когда ваш роутер должен выполнить дополнительную обработку 
/// (например, классификацию интента, кеширование, удалённый вызов), а затем передать управление 
/// внутреннему роутеру (обычно <see cref="StaticRouter"/>).
/// </remarks>
/// <example>
/// <code>
/// public class IntentRouter : DelegatingRouter
/// {
///     private readonly IIntentClassifier _classifier;
///     
///     public IntentRouter(IRouter innerRouter, IIntentClassifier classifier) 
///         : base(innerRouter) 
///     {
///         _classifier = classifier;
///     }
///     
///     public override async Task&lt;JsonNode&gt; RouteAsync(string commandName, JsonNode? argument, CancellationToken ct)
///     {
///         // Преобразуем голосовую фразу в имя команды и аргументы
///         var (name, args) = await _classifier.ClassifyAndExtract(commandName, ct);
///         return await base.RouteAsync(name, args, ct);
///     }
/// }
/// </code>
/// </example>
public abstract class DelegatingRouter : IRouter
{
    /// <summary>
    /// Внутренний роутер, которому делегируется выполнение. TODO: переписать комментарии под context
    /// </summary>
    protected readonly IRouter InnerRouter;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="DelegatingRouter"/>.
    /// </summary>
    /// <param name="innerRouter">Внутренний роутер, которому будет передано выполнение.</param>
    /// <exception cref="ArgumentNullException">Если <paramref name="innerRouter"/> равен null.</exception>
    protected DelegatingRouter(IRouter innerRouter)
    {
        InnerRouter = innerRouter ?? throw new ArgumentNullException(nameof(innerRouter));
    }

    /// <summary>
    /// Выполняет маршрутизацию команды. По умолчанию делегирует вызов внутреннему роутеру.
    /// </summary>
    /// <param name="commandName">Имя команды (или фраза для IntentRouter).</param>
    /// <param name="argument">Аргументы команды в формате JsonNode.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат выполнения команды в виде JsonNode.</returns>
    public virtual async Task<JsonNode> RouteAsync(string commandName, JsonNode? argument, IExecutionContext? context = null, CancellationToken cancellationToken=default)
    {
        return await InnerRouter.RouteAsync(commandName, argument,context, cancellationToken);
    }
}