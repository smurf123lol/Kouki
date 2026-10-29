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

        // Одна сессия на процесс: она включает busy_timeout и держит общую транзакцию,
        // поэтому все хранилища обязаны ходить через неё, а не открывать свои соединения.
        services.TryAddSingleton(_ => new SqliteSession(path));
        services.TryAddSingleton<IUnitOfWork>(sp => new SqliteUnitOfWork(sp.GetRequiredService<SqliteSession>()));

        services.TryAddSingleton<ITaskStore>(sp => new SqliteTaskStore(sp.GetRequiredService<SqliteSession>()));
        services.TryAddSingleton<IClassificationJournal>(sp => new SqliteClassificationJournal(sp.GetRequiredService<SqliteSession>()));
        services.TryAddSingleton<IActionLog>(sp => new SqliteActionLog(sp.GetRequiredService<SqliteSession>()));
        services.TryAddSingleton<IStateStore>(sp => new SqliteStateStore(sp.GetRequiredService<SqliteSession>()));
        services.TryAddSingleton<IReminderStore>(sp => new SqliteReminderStore(sp.GetRequiredService<SqliteSession>()));

        return services;
    }
}
