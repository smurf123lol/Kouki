using Kouki.Kagedama;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kouki.Kagedama.Api.Services;

/// <summary>
/// Чистит голосовые записи по двум срокам из настроек: возраст записи
/// (<c>Kouki:AudioRetentionDays</c>) и возраст разметки (<c>Kouki:AudioLabeledRetentionDays</c>).
/// </summary>
/// <remarks>
/// Сроки — настройки, а не константы: запись нужна как материал для будущего обучения,
/// но держать её вечно на домашнем сервере незачем. <c>0</c> отключает соответствующий срок.
/// Размеченное аудио удаляется раньше обычного: датасет оно уже дало, а голос человека
/// в нём остаётся. Первая чистка идёт сразу при старте, дальше — раз в 12 часов.
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

        if (!options.StoreAudio)
        {
            return;
        }

        IAudioStore? store = _services.GetService(typeof(IAudioStore)) as IAudioStore;
        if (store is null)
        {
            return;
        }

        if (options.AudioRetentionDays > 0)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-options.AudioRetentionDays);
            await SweepOnceAsync(
                () => store.RemoveOlderThanAsync(cutoff, cancellationToken),
                $"Аудио старше {options.AudioRetentionDays} дн.",
                cancellationToken).ConfigureAwait(false);
        }

        if (options.AudioLabeledRetentionDays > 0)
        {
            DateTimeOffset labeledCutoff =
                DateTimeOffset.UtcNow.AddDays(-options.AudioLabeledRetentionDays);
            await SweepOnceAsync(
                () => store.RemoveLabeledAsync(labeledCutoff, cancellationToken),
                $"Аудио, размеченное более {options.AudioLabeledRetentionDays} дн. назад,",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(
        Func<Task<int>> sweep,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            int removed = await sweep().ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("{What}: удалено {Count} записей", what, removed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{What}: чистка не удалась", what);
        }
    }
}
