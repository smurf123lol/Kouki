using Kouki.Mugura.Attributes;

namespace Kouki.Tests;

/// <summary>
/// Регрессия на баг: категории были записаны как <c>1 &gt;&gt; n</c>, то есть все ненулевые
/// члены совпадали и были равны нулю, из-за чего [Flags] не работал.
/// </summary>
public class CommandCategoryTests
{
    [Fact]
    public void Uncategorized_is_zero()
    {
        Assert.Equal(0, (int)CommandCategory.Uncategorized);
    }

    [Fact]
    public void All_other_categories_are_distinct_powers_of_two()
    {
        var values = Enum.GetValues<CommandCategory>()
            .Where(value => value != CommandCategory.Uncategorized)
            .ToArray();

        Assert.Equal(8, values.Length);
        Assert.Equal(values.Length, values.Distinct().Count());

        Assert.All(values, value =>
        {
            var numeric = (int)value;
            Assert.NotEqual(0, numeric);
            Assert.True((numeric & (numeric - 1)) == 0, $"{value} = {numeric} не является степенью двойки");
        });
    }

    [Fact]
    public void Expected_numeric_values_are_stable()
    {
        Assert.Equal(2, (int)CommandCategory.Navigation);
        Assert.Equal(4, (int)CommandCategory.FileOperations);
        Assert.Equal(8, (int)CommandCategory.Terminal);
        Assert.Equal(16, (int)CommandCategory.Environment);
        Assert.Equal(32, (int)CommandCategory.Git);
        Assert.Equal(64, (int)CommandCategory.Dangerous);
        Assert.Equal(128, (int)CommandCategory.Admin);
        Assert.Equal(256, (int)CommandCategory.Integration);
    }

    [Fact]
    public void Categories_can_be_combined()
    {
        var combined = CommandCategory.Navigation | CommandCategory.Git;

        Assert.True(combined.HasFlag(CommandCategory.Navigation));
        Assert.True(combined.HasFlag(CommandCategory.Git));
        Assert.False(combined.HasFlag(CommandCategory.Dangerous));
    }
}
