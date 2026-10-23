namespace Kouki.Kagedama.Bot;

/// <summary>
/// Цикл бота: длинный опрос Telegram → приём задач в Kouki → ответ в чат.
/// Ошибки одного сообщения не останавливают бота.
/// </summary>
public sealed class BotRunner
{
    private readonly TelegramApiClient _telegram;
    private readonly KoukiIntakeClient _kouki;
    private readonly BotOptions _options;
    private readonly Action<string> _log;

    /// <summary>
    /// Создаёт цикл бота.
    /// </summary>
    /// <param name="telegram">Клиент Telegram.</param>
    /// <param name="kouki">Клиент сервера Kouki.</param>
    /// <param name="options">Настройки.</param>
    /// <param name="log">Куда писать диагностику.</param>
    public BotRunner(TelegramApiClient telegram, KoukiIntakeClient kouki, BotOptions options, Action<string>? log = null)
    {
        _telegram = telegram ?? throw new ArgumentNullException(nameof(telegram));
        _kouki = kouki ?? throw new ArgumentNullException(nameof(kouki));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? Console.WriteLine;
    }

    /// <summary>
    /// Работает до отмены.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        long offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<TelegramMessage> messages;

            try
            {
                messages = await _telegram
                    .GetUpdatesAsync(offset, _options.PollTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"getUpdates: {ex.GetType().Name}: {ex.Message}");
                await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (TelegramMessage message in messages)
            {
                offset = Math.Max(offset, message.UpdateId + 1);

                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    await TrySendAsync(message.ChatId, "Пришли текстом, что нужно сделать. Голос — на этапе M4.", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string reply;
                try
                {
                    IntakeResponseDto response = await _kouki.SubmitAsync(message.Text, cancellationToken).ConfigureAwait(false);
                    reply = IntakeReplyFormatter.Format(response);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"intake: {ex.GetType().Name}: {ex.Message}");
                    reply = $"Kouki недоступен: {ex.Message}";
                }

                await TrySendAsync(message.ChatId, reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _telegram.SendMessageAsync(chatId, text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"sendMessage: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
