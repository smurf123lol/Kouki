using Kouki.Kagedama;

namespace Kouki.Tests;

/// <summary>
/// Интент сессии приходит от клиента и подменяет решение классификатора, поэтому
/// список разрешённых значений — часть контракта, а не удобство.
/// </summary>
public sealed class SessionIntentsTests
{
    [Fact]
    public void Known_intents_are_accepted()
    {
        Assert.True(SessionIntents.IsKnown("add_task"));
        Assert.True(SessionIntents.IsKnown(SessionIntents.Ask));
        Assert.True(SessionIntents.IsKnown(" add_task "));
    }

    [Fact]
    public void Unknown_intent_is_rejected()
    {
        Assert.False(SessionIntents.IsKnown("patch_task"));
        Assert.False(SessionIntents.IsKnown("list_tasks"));
        Assert.False(SessionIntents.IsKnown("add_task; drop"));
        Assert.False(SessionIntents.IsKnown(null));
        Assert.False(SessionIntents.IsKnown("   "));
    }

    [Fact]
    public void Every_session_intent_is_a_known_command_or_an_answer_mode()
    {
        // Клиент называет только режимы станции. Если сюда попадёт что-то ещё,
        // это либо новая сессия (обновите protocol.md), либо обход классификатора.
        Assert.Equal(2, SessionIntents.All.Count);
        Assert.Contains(SessionIntents.AddTask, SessionIntents.All);
        Assert.Contains(SessionIntents.Ask, SessionIntents.All);
    }
}
