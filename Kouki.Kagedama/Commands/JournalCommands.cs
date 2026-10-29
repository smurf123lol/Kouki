using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;

namespace Kouki.Kagedama.Commands;

/*
 * Журнал действий и журнал разбора — тоже команды.
 *
 * Хранилища (`IActionLog`, `IClassificationJournal`) умеют читать и писать, и соблазн
 * дёргать их из хоста напрямую велик. Но тогда у одной и той же операции появляются
 * разные входные правила: эндпоинт `POST /api/classifications/{id}/label` отбивает пустую
 * метку, а MCP-инструмент — нет, потому что проверка жила в эндпоинте.
 *
 * Поэтому чтения и разметка идут через маршрутизатор, как и команды задач: одно поведение,
 * одинаковые ошибки, одна запись в аудит. Хост остаётся транспортом.
 */

/// <summary>Запрос журнала действий.</summary>
/// <param name="TaskId">Только действия по этой задаче; <c>null</c> — все.</param>
/// <param name="Limit">Сколько записей вернуть (1…500).</param>
public sealed record ListActionsCommand(Guid? TaskId = null, int? Limit = null) : ICommand;

/// <summary>Отдаёт журнал действий, новые сверху.</summary>
[Command("list_actions", Description = "Показывает журнал действий", Category = CommandCategory.Navigation, Example = "что делал Kouki последние полчаса")]
public sealed class ListActionsHandler(IActionLog actions) : ICommandHandler<ListActionsCommand>
{
    /// <summary>Предел выдачи за один вызов.</summary>
    public const int MaxLimit = 500;

    /// <summary>Лимит по умолчанию.</summary>
    public const int DefaultLimit = 100;

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ListActionsCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int take = Math.Clamp(command.Limit ?? DefaultLimit, 1, MaxLimit);
        IReadOnlyList<ActionRecord> records = await actions
            .ListAsync(take, command.TaskId, cancellationToken)
            .ConfigureAwait(false);

        var array = new JsonArray();
        foreach (ActionRecord record in records)
        {
            array.Add(ToJson(record));
        }

        return new JsonObject
        {
            ["count"] = array.Count,
            ["actions"] = array,
        };
    }

    /// <summary>Действие → JSON (те же поля, что в API).</summary>
    public static JsonObject ToJson(ActionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new JsonObject
        {
            ["id"] = record.Id.ToString(),
            ["createdAt"] = record.CreatedAt,
            ["taskId"] = record.TaskId?.ToString(),
            ["classificationId"] = record.ClassificationId?.ToString(),
            ["source"] = record.Source,
            ["kind"] = record.Kind,
            ["intent"] = record.Intent,
            ["command"] = record.Command,
            ["outcome"] = record.Outcome,
            ["args"] = Parse(record.ArgsJson),
            ["result"] = Parse(record.ResultJson),
            ["error"] = record.Error,
            ["durationMs"] = record.DurationMs,
        };
    }

    private static JsonNode? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}

/// <summary>Запрос журнала разбора.</summary>
/// <param name="Limit">Сколько записей вернуть (1…1000).</param>
/// <param name="Unresolved">Только неразмеченные неуверенные записи.</param>
public sealed record ListClassificationsCommand(int? Limit = null, bool? Unresolved = null) : ICommand;

/// <summary>Отдаёт журнал разбора, новые сверху.</summary>
[Command("list_classifications", Description = "Показывает журнал разбора фраз", Category = CommandCategory.Navigation, Example = "что не поняли сегодня")]
public sealed class ListClassificationsHandler(IClassificationJournal journal)
    : ICommandHandler<ListClassificationsCommand>
{
    /// <summary>Предел выдачи за один вызов.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Лимит по умолчанию.</summary>
    public const int DefaultLimit = 100;

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ListClassificationsCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int take = Math.Clamp(command.Limit ?? DefaultLimit, 1, MaxLimit);
        IReadOnlyList<ClassificationEntry> entries = await journal
            .ListAsync(take, command.Unresolved ?? false, cancellationToken)
            .ConfigureAwait(false);

        var array = new JsonArray();
        foreach (ClassificationEntry entry in entries)
        {
            array.Add(ToJson(entry));
        }

        return new JsonObject
        {
            ["count"] = array.Count,
            ["entries"] = array,
        };
    }

    /// <summary>Запись журнала → JSON (те же поля, что в API).</summary>
    public static JsonObject ToJson(ClassificationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new JsonObject
        {
            ["id"] = entry.Id.ToString(),
            ["createdAt"] = entry.CreatedAt,
            ["source"] = entry.Source,
            ["inputKind"] = entry.InputKind,
            ["inputText"] = entry.InputText,
            ["intent"] = entry.Intent,
            ["score"] = entry.Score,
            ["confident"] = entry.Confident,
            ["model"] = entry.Model,
            ["error"] = entry.Error,
            ["label"] = entry.Label,
            ["labeledAt"] = entry.LabeledAt,
            ["asrConfidence"] = entry.AsrConfidence,
            ["audioPath"] = entry.AudioPath,
            ["hintedIntent"] = entry.HintedIntent,
        };
    }
}

/// <summary>Ручная разметка записи журнала.</summary>
/// <param name="Id">Идентификатор записи.</param>
/// <param name="Label">Метка; пустая метка — ошибка.</param>
public sealed record LabelClassificationCommand(Guid Id, string? Label = null) : ICommand;

/// <summary>Ставит метку на запись журнала — единственный путь записи метки.</summary>
[Command("label_classification", Description = "Размечает запись журнала", Category = CommandCategory.Admin, RequiresConfirmation = false, Example = "разметь запись как add_task")]
public sealed class LabelClassificationHandler(IClassificationJournal journal)
    : ICommandHandler<LabelClassificationCommand>
{
    /// <summary>Предел длины метки.</summary>
    public const int MaxLabelLength = 64;

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        LabelClassificationCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Раньше эта проверка жила в эндпоинте, поэтому MCP мог поставить пустую метку.
        string label = command.Label?.Trim() ?? string.Empty;
        if (label.Length == 0)
        {
            return new JsonObject
            {
                ["labeled"] = false,
                ["reason"] = "invalid",
                ["error"] = "Поле 'label' обязательно: пустой меткой нельзя размечать.",
            };
        }

        if (label.Length > MaxLabelLength)
        {
            return new JsonObject
            {
                ["labeled"] = false,
                ["reason"] = "invalid",
                ["error"] = $"Метка длиннее {MaxLabelLength} символов.",
            };
        }

        bool labeled = await journal
            .LabelAsync(command.Id, label, cancellationToken)
            .ConfigureAwait(false);

        return new JsonObject
        {
            ["labeled"] = labeled,
            ["id"] = command.Id.ToString(),
            ["label"] = labeled ? label : null,
            ["reason"] = labeled ? null : "not-found",
            ["error"] = labeled ? null : $"Запись {command.Id} не найдена.",
        };
    }
}

/// <summary>
/// Выгрузка размеченных фраз в датасет для обучения классификатора.
/// </summary>
/// <param name="Limit">Сколько записей брать; по умолчанию 1000.</param>
public sealed record ExportDatasetCommand(int? Limit = null) : ICommand;

/// <summary>
/// Отдаёт датасет в виде JSON Lines: по примеру на строку.
/// </summary>
/// <remarks>
/// Датасет собирается из живого журнала: чем больше фраз размечено вручную
/// в дашборде, тем полезнее файл. Формат и подсчёт — в <see cref="DatasetExport"/>.
/// </remarks>
/// <param name="journal">Журнал классификаций.</param>
[Command("export_dataset", Description = "Выгружает размеченные фразы в датасет", Category = CommandCategory.Admin, RequiresConfirmation = false, Example = "выгрузи датасет")]
public sealed class ExportDatasetHandler(IClassificationJournal journal)
    : ICommandHandler<ExportDatasetCommand>
{
    /// <summary>Сколько записей берём, если вызывающий не сказал иначе.</summary>
    public const int DefaultLimit = 1000;

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        ExportDatasetCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        int take = Math.Clamp(command.Limit ?? DefaultLimit, 1, DatasetExport.MaxEntries);
        IReadOnlyList<ClassificationEntry> entries = await journal
            .ListLabeledAsync(take, cancellationToken)
            .ConfigureAwait(false);

        return new JsonObject
        {
            ["count"] = DatasetExport.Count(entries),
            ["jsonl"] = DatasetExport.ToJsonLines(entries),
        };
    }
}
