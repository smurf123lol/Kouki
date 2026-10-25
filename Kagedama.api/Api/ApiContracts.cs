namespace Kouki.Kagedama.Api.Api;

/// <summary>Запрос на приём фразы.</summary>
/// <param name="Text">Текст от человека.</param>
/// <param name="Source">Источник: telegram, voice, dashboard, mcp, api. По умолчанию api.</param>
/// <param name="Intent">Подсказка сессии: add_task или ask (станция сообщает, что это запись).</param>
public sealed record IntakeRequest(string Text, string? Source = null, string? Intent = null);

/// <summary>Запрос на сравнение классификаторов: одна фраза, все «роутеры».</summary>
/// <param name="Text">Фраза.</param>
/// <param name="Classifiers">Имена классификаторов; <c>null</c> — все зарегистрированные.</param>
public sealed record ClassifyRequest(string Text, List<string>? Classifiers = null);

/// <summary>Частичное обновление задачи.</summary>
public sealed record TaskPatchRequest(string? Status = null, string? Title = null, int? Priority = null);

/// <summary>Ручная разметка записи журнала.</summary>
public sealed record LabelRequest(string Label);
