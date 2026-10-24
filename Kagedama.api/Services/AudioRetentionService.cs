using Kouki.Kagedama;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kouki.Kagedama.Api.Services;

/// <summary>
/// Удаляет голосовые записи старше настроенного срока (<c>Kouki:AudioRetentionDays</c>).
/// </summary>
/// <remarks>
/// Срок — настройка, а не константа: запись нужна как материал для будущего обучения,
/// но держать её вечно на домашнем сервере незачем. <c>0</c> отключает чистку по возрасту
/// (удаление размеченного остаётся в <see cref="IAudioStore.RemoveLabeledAsync"/>).
/// Первая чистка идёт сразу при старте, дальше — раз в 12 часов.
/// </remarks>
public sealed class AudioRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    private readonly IServiceProvider _services;
    private readonly IOptions<KoukiServerOptions> _options;
    private readonly ILogger<AudioRetentionService> _logger;

    /// <summary>
    /// Создаёт службу чистки.
    /// </summary>
    /// <param name="services">Провайдер: хранилище регистрируется лениво, чтобы не мешать старту.</param>
    /// <param name="options">Настройки сервера.</param>
    /// <param name="logger">Лог.</param>
    public AudioRetentionService(
        IServiceProvider services,
        IOptions<KoukiServerOptions> options,
        ILogger<AudioRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        KoukiServerOptions options = _options.Value;

        if (!options.StoreAudio || options.AudioRetentionDays <= 0)
        {
            return;
        }

        IAudioStore? store = _services.GetService(typeof(IAudioStore)) as IAudioStore;
        if (store is null)
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-options.AudioRetentionDays);

        try
        {
            int removed = await store.RemoveOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation(
                    "Аудио старше {Days} дн.: удалено {Count} записей",
                    options.AudioRetentionDays,
                    removed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Чистка аудио по сроку хранения не удалась");
        }
    }
}
