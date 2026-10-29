using System.Globalization;
using System.Text.Json.Nodes;

using Kouki.Kagedama.Contracts;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;
using Kouki.Mugura.Commands;

namespace Kouki.Kagedama.Commands;

/// <summary>
/// Создание напоминания из фразы: время разбирается здесь, текстом остаётся остаток фразы.
/// </summary>
/// <param name="Text">Фраза целиком («через 20 минут позвонить маме»).</param>
/// <param name="TaskId">Карточка задачи, если напоминание относится к ней.</param>
/// <param name="Source">Источник фразы; по умолчанию «command».</param>
public sealed record AddReminderCommand(string? Text = null, Guid? TaskId = null, string? Source = null) : ICommand;

/// <summary>
/// Создаёт напоминание.
/// </summary>
[Command(
    "add_reminder",
    Description = "Напоминает о деле в нужное время",
    Category = CommandCategory.Integration,
    Example = "напомни через 20 минут позвонить маме")]
public sealed class AddReminderHandler : ICommandHandler<AddReminderCommand>
{
    private readonly IReminderStore _reminders;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    /// <summary>
    /// Создаёт обработчик напоминаний.
    /// </summary>
    /// <param name="reminders">Хранилище напоминаний.</param>
    /// <param name="clock">Часы (подменяются в тестах).</param>
    /// <param name="zone">Часовой пояс человека.</param>
    public AddReminderHandler(IReminderStore reminders, TimeProvider clock, TimeZoneInfo zone)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        AddReminderCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return Fail("Пустая фраза: нечего напоминать.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (!ReminderTime.TryParse(command.Text, now, _zone, out ParsedReminder parsed))
        {
            return Fail(
                "Не понял, когда напомнить. Скажите «через 20 минут …», " +
                "«завтра в 9 …» или «каждый день в 8 …».");
        }

        string source = string.IsNullOrWhiteSpace(command.Source) ? TaskSources.Command : command.Source.Trim();
        ReminderRecord record = await _reminders
            .AddAsync(parsed.Text, parsed.DueAt, parsed.Repeat, source, command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        return new JsonObject
        {
            ["created"] = true,
            ["reminderId"] = record.Id.ToString(),
            ["text"] = record.Text,
            ["dueAt"] = record.DueAt.ToString("O", CultureInfo.InvariantCulture),
            ["dueText"] = ReminderTime.Describe(record.DueAt, now, _zone, record.Repeat),
            ["repeat"] = record.Repeat,
            ["source"] = record.Source,
            ["taskId"] = record.TaskId?.ToString(),
        };
    }

    private static JsonObject Fail(string message) => new()
    {
        ["created"] = false,
        ["reason"] = "time-not-understood",
        ["error"] = message,
    };
}

/// <summary>
/// Запрос списка напоминаний.
/// </summary>
/// <param name="Limit">Сколько показать; по умолчанию десять.</param>
/// <param name="OpenOnly">Только живые (ожидают срока или реакции).</param>
public sealed record ListRemindersCommand(int? Limit = null, bool? OpenOnly = null) : ICommand;

/// <summary>
/// Показывает напоминания от ближайшего срока.
/// </summary>
[Command(
    "list_reminders",
    Description = "Показывает ближайшие напоминания",
    Category = CommandCategory.Navigation,
    Example = "какие у меня напоминания")]
public sealed class ListRemindersHandler : ICommandHandler<ListRemindersCommand>
{
    private readonly IReminderStore _reminders;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    /// <summary>
    /// Создаёт обработчик списка.
    /// </summary>
    /// <param name="reminders">Хранилище.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="zone">Часовой пояс.</param>
    public ListRemindersHandler(IReminderStore reminders, TimeProvider clock, TimeZoneInfo zone)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ListRemindersCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int limit = Math.Clamp(command.Limit ?? 10, 1, 500);
        IReadOnlyList<ReminderRecord> records = await _reminders
            .ListAsync(command.OpenOnly ?? false, limit, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = _clock.GetUtcNow();
        var items = new JsonArray();
        foreach (ReminderRecord record in records)
        {
            items.Add(ToJson(record, now, _zone));
        }

        return new JsonObject
        {
            ["count"] = records.Count,
            ["reminders"] = items,
        };
    }

    /// <summary>
    /// Сериализует напоминание одинаково для команд и HTTP-слоя.
    /// </summary>
    /// <param name="record">Напоминание.</param>
    /// <param name="now">Текущий момент (для человеческого описания срока).</param>
    /// <param name="zone">Часовой пояс.</param>
    public static JsonObject ToJson(ReminderRecord record, DateTimeOffset now, TimeZoneInfo zone) => new()
    {
        ["id"] = record.Id.ToString(),
        ["text"] = record.Text,
        ["dueAt"] = record.DueAt.ToString("O", CultureInfo.InvariantCulture),
        ["dueText"] = ReminderTime.Describe(record.DueAt, now, zone, record.Repeat),
        ["state"] = record.State,
        ["repeat"] = record.Repeat,
        ["taskId"] = record.TaskId?.ToString(),
        ["source"] = record.Source,
    };
}

/// <summary>
/// Отмена напоминания.
/// </summary>
/// <param name="Id">Идентификатор.</param>
public sealed record CancelReminderCommand(Guid Id) : ICommand;

/// <summary>
/// Отменяет напоминание.
/// </summary>
[Command(
    "cancel_reminder",
    Description = "Отменяет напоминание",
    Category = CommandCategory.Dangerous,
    Example = "отмени напоминание про маму",
    RequiresConfirmation = true)]
public sealed class CancelReminderHandler : ICommandHandler<CancelReminderCommand>
{
    private readonly IReminderStore _reminders;

    /// <summary>
    /// Создаёт обработчик отмены.
    /// </summary>
    /// <param name="reminders">Хранилище.</param>
    public CancelReminderHandler(IReminderStore reminders)
        => _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        CancelReminderCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ReminderRecord? cancelled = await _reminders.CancelAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return cancelled is null
            ? new JsonObject { ["cancelled"] = false, ["reason"] = "not-found" }
            : new JsonObject { ["cancelled"] = true, ["reminderId"] = cancelled.Id.ToString() };
    }
}

/// <summary>
/// Подтверждение напоминания («готово»).
/// </summary>
/// <param name="Id">Идентификатор.</param>
public sealed record CompleteReminderCommand(Guid Id) : ICommand;

/// <summary>
/// Отмечает напоминание выполненным; у повторяющегося ставит следующий срок.
/// </summary>
[Command(
    "complete_reminder",
    Description = "Отмечает напоминание выполненным",
    Category = CommandCategory.Integration,
    Example = "готово с напоминанием")]
public sealed class CompleteReminderHandler : ICommandHandler<CompleteReminderCommand>
{
    private readonly IReminderStore _reminders;

    /// <summary>
    /// Создаёт обработчик подтверждения.
    /// </summary>
    /// <param name="reminders">Хранилище.</param>
    public CompleteReminderHandler(IReminderStore reminders)
        => _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        CompleteReminderCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ReminderRecord? done = await _reminders.AcknowledgeAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return done is null
            ? new JsonObject { ["done"] = false, ["reason"] = "not-found" }
            : new JsonObject
            {
                ["done"] = true,
                ["reminderId"] = done.Id.ToString(),
                ["state"] = done.State,
                ["dueAt"] = done.DueAt.ToString("O", CultureInfo.InvariantCulture),
            };
    }
}

/// <summary>
/// Отложить напоминание.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Minutes">На сколько минут отложить; по умолчанию десять.</param>
public sealed record SnoozeReminderCommand(Guid Id, int? Minutes = null) : ICommand;

/// <summary>
/// Откладывает напоминание на несколько минут.
/// </summary>
[Command(
    "snooze_reminder",
    Description = "Откладывает напоминание",
    Category = CommandCategory.Integration,
    Example = "отложи напоминание на 10 минут")]
public sealed class SnoozeReminderHandler : ICommandHandler<SnoozeReminderCommand>
{
    private readonly IReminderStore _reminders;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    /// <summary>
    /// Создаёт обработчик отсрочки.
    /// </summary>
    /// <param name="reminders">Хранилище.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="zone">Часовой пояс.</param>
    public SnoozeReminderHandler(IReminderStore reminders, TimeProvider clock, TimeZoneInfo zone)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        SnoozeReminderCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int minutes = Math.Clamp(command.Minutes ?? 10, 1, 24 * 60);
        DateTimeOffset now = _clock.GetUtcNow();
        ReminderRecord? snoozed = await _reminders
            .SnoozeAsync(command.Id, TimeSpan.FromMinutes(minutes), now, cancellationToken)
            .ConfigureAwait(false);

        return snoozed is null
            ? new JsonObject { ["snoozed"] = false, ["reason"] = "not-found" }
            : new JsonObject
            {
                ["snoozed"] = true,
                ["reminderId"] = snoozed.Id.ToString(),
                ["dueAt"] = snoozed.DueAt.ToString("O", CultureInfo.InvariantCulture),
                ["dueText"] = ReminderTime.Describe(snoozed.DueAt, now, _zone, snoozed.Repeat),
            };
    }
}

/// <summary>
/// Забрать созревшие напоминания (их отправляет канал доставки).
/// </summary>
/// <param name="Limit">Сколько забрать; по умолчанию пять.</param>
public sealed record TakeDueRemindersCommand(int? Limit = null) : ICommand;

/// <summary>
/// Возвращает созревшие напоминания и помечает их отправленными.
/// </summary>
/// <remarks>
/// Помечает <c>sent</c> внутри команды: два канала (бот и станция), опросившие
/// одновременно, не должны отправить одно напоминание дважды.
/// </remarks>
[Command(
    "take_due_reminders",
    Description = "Забирает созревшие напоминания для отправки",
    Category = CommandCategory.Integration,
    Example = "напоминания на отправку")]
public sealed class TakeDueRemindersHandler : ICommandHandler<TakeDueRemindersCommand>
{
    private readonly IReminderStore _reminders;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    /// <summary>
    /// Создаёт обработчик выборки.
    /// </summary>
    /// <param name="reminders">Хранилище.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="zone">Часовой пояс.</param>
    public TakeDueRemindersHandler(IReminderStore reminders, TimeProvider clock, TimeZoneInfo zone)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
    }

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        TakeDueRemindersCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int limit = Math.Clamp(command.Limit ?? 5, 1, 50);
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<ReminderRecord> due = await _reminders
            .DueAsync(now, limit, cancellationToken)
            .ConfigureAwait(false);

        var items = new JsonArray();
        foreach (ReminderRecord record in due)
        {
            items.Add(ListRemindersHandler.ToJson(record, now, _zone));
        }

        return new JsonObject
        {
            ["count"] = due.Count,
            ["reminders"] = items,
        };
    }
}
