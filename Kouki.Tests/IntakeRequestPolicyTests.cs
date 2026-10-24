using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;

namespace Kouki.Tests;

/// <summary>
/// Правила границы приёма — одно место на все хосты: api, MCP, бот.
/// Проверяем, что «пусто», «длинно», «неизвестный источник» и «неизвестный интент»
/// отсекаются одинаково, а не по-своему в каждом эндпоинте.
/// </summary>
public sealed class IntakeRequestPolicyTests
{
    [Fact]
    public void Defaults_are_applied_and_text_is_trimmed()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            "  запиши задачу  ",
            null,
            null,
            TaskSources.Telegram,
            2000,
            out IntakeSpec? spec,
            out IntakeRejection? rejection);

        Assert.True(ok);
        Assert.Null(rejection);
        Assert.NotNull(spec);
        Assert.Equal("запиши задачу", spec!.Text);
        Assert.Equal(TaskSources.Telegram, spec.Source);
        Assert.Null(spec.IntentHint);
    }

    [Fact]
    public void Empty_text_is_rejected_with_a_code()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            "   ",
            null,
            null,
            TaskSources.Api,
            2000,
            out _,
            out IntakeRejection? rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(IntakeRequestPolicy.MissingText, rejection!.Code);
    }

    [Fact]
    public void Too_long_text_is_rejected()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            new string('а', 10),
            null,
            null,
            TaskSources.Api,
            5,
            out _,
            out IntakeRejection? rejection);

        Assert.False(ok);
        Assert.Equal(IntakeRequestPolicy.TextTooLong, rejection!.Code);
        Assert.Contains("5", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_source_is_rejected_and_lists_known_ones()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            "запиши задачу",
            "telepathy",
            null,
            TaskSources.Api,
            2000,
            out _,
            out IntakeRejection? rejection);

        Assert.False(ok);
        Assert.Equal(IntakeRequestPolicy.UnknownSource, rejection!.Code);
        Assert.Contains(TaskSources.Station, rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_intent_hint_is_rejected()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            "запиши задачу",
            null,
            "patch_task",
            TaskSources.Api,
            2000,
            out _,
            out IntakeRejection? rejection);

        Assert.False(ok);
        Assert.Equal(IntakeRequestPolicy.UnknownIntent, rejection!.Code);
        Assert.Contains(SessionIntents.AddTask, rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_intent_hint_passes_through()
    {
        bool ok = IntakeRequestPolicy.TryPrepare(
            "запиши задачу",
            TaskSources.Station,
            " " + SessionIntents.Ask + " ",
            TaskSources.Api,
            2000,
            out IntakeSpec? spec,
            out _);

        Assert.True(ok);
        Assert.Equal(SessionIntents.Ask, spec!.IntentHint);
    }

    [Fact]
    public void Blank_intent_hint_means_no_hint()
    {
        Assert.True(IntakeRequestPolicy.TryResolveIntentHint("   ", out string? hint, out _));
        Assert.Null(hint);
    }
}
