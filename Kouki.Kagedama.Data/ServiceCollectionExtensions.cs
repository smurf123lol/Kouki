using Kouki.Shimi.ServiceAbstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama.Data;

/// <summary>
/// Регистрация хранилища Kouki: одна база — все интерфейсы.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Подключает SQLite-реализации <see cref="ITaskStore"/>, <see cref="IClassificationJournal"/>,
    /// <see cref="IActionLog"/> и <see cref="IStateStore"/>.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="databasePath">Путь к файлу базы; относительный считается от текущего каталога.</param>
    /// <remarks>
    /// Вызывайте до <c>AddShimiContext</c>: там <c>IStateStore</c> регистрируется через
    /// <c>TryAddSingleton</c>, и первая регистрация выигрывает.
    /// </remarks>
    public static IServiceCollection AddKagedamaData(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string path = Path.GetFullPath(databasePath);

        services.TryAddSingleton<ITaskStore>(_ => new SqliteTaskStore(path));
        services.TryAddSingleton<IClassificationJournal>(_ => new SqliteClassificationJournal(path));
        services.TryAddSingleton<IActionLog>(_ => new SqliteActionLog(path));
        services.TryAddSingleton<IStateStore>(_ => new SqliteStateStore(path));

        return services;
    }
}
