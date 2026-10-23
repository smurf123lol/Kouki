using Kouki.Mugura.Dispatcher;
using Kouki.Mugura.Registry;
using Kouki.Shimi.Context;

namespace Kouki.Yasabi;

/// <summary>
/// Маршрутизатор естественного языка: понимает фразу, находит команду по имени интента
/// и отдаёт её обычному <see cref="IRouter"/>. Порог уверенности — единственная причина
/// не исполнять действие молча.
/// </summary>
public sealed class IntentRouter : IIntentRouter
{
    private readonly IIntentClassifier _classifier;
    private readonly CommandRegistry _registry;
    private readonly IRouter _router;
    private readonly YasabiOptions _options;

    /// <summary>
    /// Создаёт маршрутизатор.
    /// </summary>
    /// <param name="classifier">Классификатор интентов.</param>
    /// <param name="registry">Реестр команд.</param>
    /// <param name="router">Обычный маршрутизатор команд.</param>
    /// <param name="options">Настройки.</param>
    public IntentRouter(
        IIntentClassifier classifier,
        CommandRegistry registry,
        IRouter router,
        YasabiOptions options)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(options);

        _classifier = classifier;
        _registry = registry;
        _router = router;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<IntentRoute> RouteAsync(string text, IExecutionContext? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new IntentRoute(IntentResult.Unknown(_classifier.GetType().Name), null, null, false, "Пустая фраза: нечего маршрутизировать.");
        }

        IntentResult intent = await _classifier.ClassifyAsync(text, cancellationToken).ConfigureAwait(false);
        return await RouteAsync(intent, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IntentRoute> RouteAsync(IntentResult intent, IExecutionContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (!intent.IsConfident(_options.ConfidenceThreshold))
        {
            return new IntentRoute(intent, null, null, false, null);
        }

        string? commandName = ResolveCommandName(intent.Intent);
        if (commandName is null)
        {
            return new IntentRoute(intent, null, null, false, $"Для интента '{intent.Intent}' не зарегистрирована команда.");
        }

        var result = await _router.RouteAsync(commandName, intent.Slots, context, cancellationToken).ConfigureAwait(false);
        return new IntentRoute(intent, commandName, result, true, null);
    }

    private string? ResolveCommandName(string intent)
    {
        if (_registry.GetEntry(intent) is not null)
        {
            return intent;
        }

        string dashed = intent.Replace('_', '-');
        if (_registry.GetEntry(dashed) is not null)
        {
            return dashed;
        }

        string underscored = intent.Replace('-', '_');
        return _registry.GetEntry(underscored) is not null ? underscored : null;
    }
}
