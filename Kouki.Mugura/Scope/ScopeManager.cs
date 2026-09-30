using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Mugura.Scope;
/// <summary>
/// Реализация <see cref="IScopeManager"/>, создающая DI-скоп для каждого вызова.
/// </summary>
/// <remarks>
/// Использует <see cref="IServiceScopeFactory"/> для создания скоупа.
/// </remarks>
/// <remarks>
/// <strong>TODO:</strong> Добавить параметр configureScope для передачи контекста в скоуп.
/// Это позволит роутерам настраивать Scoped-сервисы (например, IContext) до резолва хендлера.
/// </remarks>
public class ScopeManager : IScopeManager
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopeManager(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}