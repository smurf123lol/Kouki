using Kouki.Mugura.Validators;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

public class ValidationTests
{
    [Fact]
    public void Validation_works_without_extra_registration()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        // Если AddMugura не регистрирует открытый ICommandValidator<>, здесь будет null.
        var validator = scope.ServiceProvider.GetService<ICommandValidator<CreateUserCommand>>();

        Assert.NotNull(validator);
    }

    [Fact]
    public void Required_property_fails_validation()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var validator = scope.ServiceProvider.GetRequiredService<ICommandValidator<CreateUserCommand>>();

        var valid = validator.TryValidate(new CreateUserCommand(null, 30), out var error);

        Assert.False(valid);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("Name", error!);
    }

    [Fact]
    public void Range_violation_fails_validation()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var validator = scope.ServiceProvider.GetRequiredService<ICommandValidator<CreateUserCommand>>();

        var valid = validator.TryValidate(new CreateUserCommand("bob", 200), out var error);

        Assert.False(valid);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("Age", error!);
    }

    [Fact]
    public void Valid_command_passes_validation()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var validator = scope.ServiceProvider.GetRequiredService<ICommandValidator<CreateUserCommand>>();

        var valid = validator.TryValidate(new CreateUserCommand("bob", 30), out var error);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void Command_without_rules_passes_validation()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var validator = scope.ServiceProvider.GetRequiredService<ICommandValidator<EchoCommand>>();

        var valid = validator.TryValidate(new EchoCommand(null), out var error);

        Assert.True(valid);
        Assert.Null(error);
    }
}
