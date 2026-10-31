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

        // Чистка по объёму не зависит от хранилища: она ходит напрямую по каталогу,
        // поэтому выполняется даже если IAudioStore ещё не зарегистрирован.
        if (options.AudioMaxBytes > 0)
        {
            SweepBySize(options);
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

    // Возраст не спасает, когда фраз много: смотрим суммарный объём и удаляем самое
    // старое, пока не влезем в предел. Ошибки файловой системы не валят приём.
    private void SweepBySize(KoukiServerOptions options)
    {
        try
        {
            if (!Directory.Exists(options.AudioDirectory))
            {
                return;
            }

            var files = new DirectoryInfo(options.AudioDirectory)
                .EnumerateFiles("*.wav", SearchOption.AllDirectories)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            long total = files.Sum(file => file.Length);
            if (total <= options.AudioMaxBytes)
            {
                return;
            }

            int removed = 0;
            long freed = 0;

            foreach (FileInfo file in files)
            {
                if (total <= options.AudioMaxBytes)
                {
                    break;
                }

                long size = file.Length;
                try
                {
                    file.Delete();
                    total -= size;
                    freed += size;
                    removed++;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Аудио {Name}: удалить не удалось", file.Name);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Аудио {Name}: нет доступа", file.Name);
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    "Аудио по размеру: удалено {Count} файлов, освобождено {Freed} МБ (предел {Limit} МБ)",
                    removed,
                    freed / (1024 * 1024),
                    options.AudioMaxBytes / (1024 * 1024));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Чистка аудио по размеру не удалась");
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
