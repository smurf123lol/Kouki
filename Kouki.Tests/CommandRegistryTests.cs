using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.DependencyInjection;
using Kouki.Mugura.Registry;
using Kouki.Ubusuna.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

public class CommandRegistryTests
{
    [Fact]
    public void Finds_ping_in_ubusuna_assembly()
    {
        using var provider = TestHost.Build();

        var registry = provider.GetRequiredService<CommandRegistry>();
        var entry = registry.GetEntry("ping");

        Assert.NotNull(entry);
        Assert.Equal(typeof(PingCommand), entry!.CommandType);
        Assert.Equal(typeof(PingHandler), entry.HandlerType);
    }

    [Fact]
    public void Names_returns_registered_commands_without_throwing()
    {
        using var provider = TestHost.Build();

        var registry = provider.GetRequiredService<CommandRegistry>();

        // До правки это свойство падало с InvalidCastException: Keys — не IReadOnlyCollection<string>.
        var names = registry.Names;

        Assert.Contains("ping", names);
        Assert.Contains("echo", names);
        Assert.Contains("create-user", names);
    }

    [Fact]
    public void GetEntryByCommandType_returns_same_entry()
    {
        using var provider = TestHost.Build();

        var registry = provider.GetRequiredService<CommandRegistry>();
        var byName = registry.GetEntry("ping");
        var byType = registry.GetEntryByCommandType(typeof(PingCommand));

        Assert.NotNull(byType);
        Assert.Equal(byName, byType);
    }

    [Fact]
    public void Unknown_command_returns_null()
    {
        using var provider = TestHost.Build();

        var registry = provider.GetRequiredService<CommandRegistry>();

        Assert.Null(registry.GetEntry("does-not-exist"));
        Assert.Null(registry.GetEntryByCommandType(typeof(string)));
    }

    [Fact]
    public void Command_attribute_on_dto_is_not_registered()
    {
        // AttributeOnDtoCommand помечен [Command], но атрибут стоит на DTO, а не на хендлере,
        // поэтому реестр такую команду не видит — фиксируем текущую семантику.
        var services = new ServiceCollection();
        services.AddMugura(typeof(Fixtures.AttributeOnDtoCommand).Assembly);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CommandRegistry>();

        Assert.Null(registry.GetEntry("dto-attr-test"));
    }

    [Fact]
    public void Scanning_the_same_assembly_twice_reports_duplicate_command_names()
    {
        var assembly = typeof(EchoHandler).Assembly;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new CommandRegistry(new[] { assembly, assembly }));

        Assert.Contains("Duplicate command name", exception.Message);
    }

    [Fact]
    public void Phrases_file_is_copied_from_attribute_to_registry_entry()
    {
        using var provider = TestHost.Build();
        var registry = provider.GetRequiredService<CommandRegistry>();

        var entry = registry.GetEntry("fixture-phrases");

        Assert.NotNull(entry);
        Assert.Equal("Phrases/fixture-phrases.txt", entry!.PhrasesFile);
    }

    [Fact]
    public void Commands_without_phrases_file_have_null()
    {
        using var provider = TestHost.Build();
        var registry = provider.GetRequiredService<CommandRegistry>();

        Assert.Null(registry.GetEntry("ping")!.PhrasesFile);
    }

    [Fact]
    public void Voice_commands_declare_phrase_files_journal_commands_do_not()
    {
        // Контракт датасета (спека T80 §2): фразы есть у семи голосовых команд,
        // у patch_task и журнальных/админских их нет.
        var registry = new CommandRegistry(new[] { typeof(Kouki.Kagedama.Commands.AddTaskHandler).Assembly });

        string[] voice =
        [
            "add_task", "list_tasks", "complete_task",
            "add_reminder", "list_reminders", "complete_reminder", "ask",
        ];
        foreach (string name in voice)
        {
            Assert.False(string.IsNullOrWhiteSpace(registry.GetEntry(name)!.PhrasesFile), name);
        }

        string[] withoutPhrases =
        [
            "patch_task", "list_actions", "list_classifications",
            "label_classification", "export_dataset", "list_telemetry",
        ];
        foreach (string name in withoutPhrases)
        {
            Assert.Null(registry.GetEntry(name)!.PhrasesFile);
        }
    }

    [Fact]
    public void Command_attribute_rejects_empty_name()
    {
        Assert.Throws<ArgumentException>(() => new CommandAttribute(" "));
        Assert.Throws<ArgumentNullException>(() => new CommandAttribute(null!));
    }
}
