using Kouki.Un;

namespace Kouki.Tests;

/// <summary>
/// Проверка каталога моделей распознавания: именно она решает, поднимать
/// sherpa-onnx или честно сказать «распознавания нет».
/// </summary>
/// <remarks>
/// Цена ошибки — сервер, который падает при старте из-за отсутствующего каталога
/// (так и случилось на первом деплое). Поэтому проверка живёт в <c>Kouki.Un</c>
/// и покрыта тестами в обычной сборке, без пакета sherpa-onnx.
/// </remarks>
public sealed class SpeechModelCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kouki-model-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Missing_directory_is_reported_with_its_path()
    {
        bool ok = SpeechModelCheck.TryValidate(Path.Combine(_root, "нет-такого"), out string? reason);

        Assert.False(ok);
        Assert.Contains("нет каталога моделей", reason, StringComparison.Ordinal);
        Assert.Contains("нет-такого", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_setting_is_reported_as_configuration_problem()
    {
        Assert.False(SpeechModelCheck.TryValidate(null, out string? reason1));
        Assert.Contains("не задан каталог", reason1, StringComparison.Ordinal);

        Assert.False(SpeechModelCheck.TryValidate("   ", out string? reason2));
        Assert.Contains("не задан каталог", reason2, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_without_model_files_lists_what_is_missing()
    {
        Directory.CreateDirectory(_root);

        bool ok = SpeechModelCheck.TryValidate(_root, out string? reason);

        Assert.False(ok);
        Assert.Contains("tokens.txt", reason, StringComparison.Ordinal);
        Assert.Contains("encoder", reason, StringComparison.Ordinal);
        Assert.Contains("decoder", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_model_directory_passes()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "small-tokens.txt"), "a 1\n");
        File.WriteAllBytes(Path.Combine(_root, "small-encoder.int8.onnx"), [0]);
        File.WriteAllBytes(Path.Combine(_root, "small-decoder.int8.onnx"), [0]);

        Assert.True(SpeechModelCheck.TryValidate(_root, out string? reason), reason);
        Assert.Null(reason);
    }

    [Fact]
    public void Half_downloaded_model_is_rejected_even_with_tokens()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "small-tokens.txt"), "a 1\n");
        File.WriteAllBytes(Path.Combine(_root, "small-encoder.onnx"), [0]);

        bool ok = SpeechModelCheck.TryValidate(_root, out string? reason);

        Assert.False(ok);
        Assert.Contains("decoder", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("tokens.txt", reason, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // временный каталог: не смогли удалить — не повод валить тест
        }
    }
}
