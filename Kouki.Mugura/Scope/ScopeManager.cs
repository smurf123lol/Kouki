using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Mugura.Scope;

/// <summary>
/// Реализация <see cref="IScopeManager"/>, создающая DI-скоуп для каждого вызова.
/// </summary>
/// <remarks>
/// Использует <see cref="IServiceScopeFactory"/> для создания asynchronous-скоупа.
/// Контекст выполнения (<c>IExecutionContext</c>) в скоуп не кладётся: он передаётся
/// параметром в диспетчер команд и далее в хендлер.
/// </remarks>
public class ScopeManager : IScopeManager
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopeManager(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}