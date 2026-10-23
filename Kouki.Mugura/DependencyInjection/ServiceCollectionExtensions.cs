using Kouki.Mugura.Dispatcher;
using Kouki.Mugura.Factories;
using Kouki.Mugura.Handlers;
using Kouki.Mugura.Registry;
using Kouki.Mugura.Scope;
using Kouki.Mugura.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Kouki.Mugura.DependencyInjection;

/// <summary>
/// Методы расширения для регистрации ядра Mugura в DI-контейнере.
/// </summary>
/// <remarks>
/// <para>
/// Метод <see cref="AddMugura"/> регистрирует все необходимые компоненты:
/// </para>
/// <list type="bullet">
///   <item><see cref="CommandRegistry"/> — синглтон.</item>
///   <item><see cref="ICommandFactory"/> (реализация <see cref="CommandFactory"/>) — синглтон.</item>
///   <item><see cref="IScopeManager"/> (реализация <see cref="ScopeManager"/>) — синглтон.</item>
///   <item><see cref="ICommandDispatcher"/> (реализация <see cref="CommandDispatcher"/>) — синглтон.</item>
///   <item><see cref="IRouter"/> (реализация <see cref="StaticRouter"/>) — синглтон.</item>
///   <item>Открытый generic <see cref="Validators.ICommandValidator{TCommand}"/> → <see cref="Validators.DataAnnotationsValidator{TCommand}"/>.</item>
///   <item>Все классы, реализующие <see cref="ICommandHandler{TCommand}"/>, из указанных сборок регистрируются как Scoped.</item>
/// </list>
/// <para>
/// Если сборки не указаны, используется вызывающая сборка (assembly вызывающего метода).
/// </para>
/// <para>
/// После вызова этого метода можно переопределить валидатор для конкретной команды, 
/// зарегистрировав свою реализацию <see cref="Validators.ICommandValidator{TCommand}"/>.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует ядро Mugura в DI контейнере.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="commandHandlerAssemblies">Сборки, в которых нужно искать хендлеры команд (если не указаны, используется вызывающая сборка).</param>
    /// <returns>IServiceCollection для цепочки вызовов.</returns>
    public static IServiceCollection AddMugura(this IServiceCollection services, params Assembly[] commandHandlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(commandHandlerAssemblies);

        var assemblies = commandHandlerAssemblies.Length == 0
            ? new[] { Assembly.GetCallingAssembly() }
            : commandHandlerAssemblies;

        // Реестр команд (сканирует сборки)
        services.TryAddSingleton(new CommandRegistry(assemblies));

        // Фабрика команд
        services.TryAddSingleton<ICommandFactory, CommandFactory>();

        // Менеджер скоупов (управление временем жизни)
        services.TryAddSingleton<IScopeManager, ScopeManager>();

        // Диспетчер команд (вызывает хендлер через ScopeManager)
        services.TryAddSingleton<ICommandDispatcher, CommandDispatcher>();

        // Роутер по имени команды (статический)
        services.TryAddSingleton<IRouter, StaticRouter>();

        // Валидатор по умолчанию: открытый generic. TryAdd — чтобы приложение могло
        // зарегистрировать свою реализацию ICommandValidator<T> раньше или позже вызова AddMugura.
        services.TryAddScoped(typeof(ICommandValidator<>), typeof(DataAnnotationsValidator<>));

        // Регистрация всех хендлеров команд (каждый как Scoped).
        // TryAddEnumerable делает повторный AddMugura идемпотентным.
        foreach (var asm in assemblies)
        {
            foreach (var type in AssemblyScanning.GetLoadableTypes(asm))
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;

                foreach (var iface in type.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(ICommandHandler<>))
                    {
                        services.TryAddEnumerable(ServiceDescriptor.Scoped(iface, type));
                        break; // хендлер реализует ровно один ICommandHandler<>
                    }
                }
            }
        }

        return services;
    }
}