using System.Text.Json;
using Kouki.Kagedama.Api.Api;
using Kouki.Un;
using Xunit;

namespace Kouki.Tests;

public sealed class SpeechVoiceTests : IDisposable
{
    private readonly string _tempDir;

    public SpeechVoiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kouki-voice-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp
        }
    }

    [Fact]
    public void SpeechRequest_deserialization_handles_both_with_and_without_voice()
    {
        string jsonWithoutVoice = """{"text":"Привет мир"}""";
        var req1 = JsonSerializer.Deserialize<SpeechRequest>(jsonWithoutVoice, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(req1);
        Assert.Equal("Привет мир", req1.Text);
        Assert.Null(req1.Voice);

        string jsonWithVoice = """{"text":"Привет мир","voice":"ru_RU-dmitri-medium"}""";
        var req2 = JsonSerializer.Deserialize<SpeechRequest>(jsonWithVoice, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(req2);
        Assert.Equal("Привет мир", req2.Text);
        Assert.Equal("ru_RU-dmitri-medium", req2.Voice);
    }

    [Fact]
    public void TryValidateVoiceName_accepts_valid_names()
    {
        string[] validNames =
        [
            "ru_RU-irina-medium",
            "ru_RU-dmitri-medium",
            "voice.1",
            "voice_test-2",
            "v1"
        ];

        foreach (string name in validNames)
        {
            bool ok = SpeechVoiceResolver.TryValidateVoiceName(name, out string? error);
            Assert.True(ok, $"Expected '{name}' to be valid, but got: {error}");
            Assert.Null(error);
        }
    }

    [Fact]
    public void TryValidateVoiceName_rejects_empty_or_whitespace()
    {
        string[] emptyValues = ["", "   ", "\t\n"];
        foreach (string val in emptyValues)
        {
            bool ok = SpeechVoiceResolver.TryValidateVoiceName(val, out string? error);
            Assert.False(ok);
            Assert.Equal("Поле 'voice' пустое: нечего выбирать.", error);
        }

        bool okNull = SpeechVoiceResolver.TryValidateVoiceName(null, out string? nullError);
        Assert.False(okNull);
        Assert.Equal("Поле 'voice' пустое: нечего выбирать.", nullError);
    }

    [Fact]
    public void TryValidateVoiceName_rejects_invalid_characters_and_path_traversal()
    {
        string[] invalidNames =
        [
            "..",
            "../etc/passwd",
            "sub/dir",
            @"sub\dir",
            "voice@1",
            "voice$test",
            "voice*",
            new string('a', 65) // too long (>64)
        ];

        foreach (string name in invalidNames)
        {
            bool ok = SpeechVoiceResolver.TryValidateVoiceName(name, out string? error);
            Assert.False(ok, $"Expected '{name}' to be rejected");
            Assert.Equal($"Недопустимое имя голоса '{name}'. Разрешены буквы, цифры, точка, дефис и подчёркивание.", error);
        }
    }

    [Fact]
    public void ResolveVoiceDirectory_prefers_vits_piper_prefix_over_plain_directory()
    {
        string voice = "ru_RU-dmitri-medium";
        string vitsDir = Path.Combine(_tempDir, "vits-piper-" + voice);
        string plainDir = Path.Combine(_tempDir, voice);

        Directory.CreateDirectory(vitsDir);
        File.WriteAllText(Path.Combine(vitsDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(vitsDir, "model.onnx"), "onnx");

        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(plainDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(plainDir, "model.onnx"), "onnx");

        string? resolved = SpeechVoiceResolver.ResolveVoiceDirectory(_tempDir, voice);
        Assert.Equal(vitsDir, resolved);
    }

    [Fact]
    public void ResolveVoiceDirectory_falls_back_to_plain_directory_if_no_vits_prefix()
    {
        string voice = "ru_RU-denis-medium";
        string plainDir = Path.Combine(_tempDir, voice);

        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(plainDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(plainDir, "model.onnx"), "onnx");

        string? resolved = SpeechVoiceResolver.ResolveVoiceDirectory(_tempDir, voice);
        Assert.Equal(plainDir, resolved);
    }

    [Fact]
    public void ResolveVoiceDirectory_resolves_base_directory_only_for_default_voice_in_legacy_layout()
    {
        string defaultVoice = "ru_RU-irina-medium";
        File.WriteAllText(Path.Combine(_tempDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(_tempDir, "model.onnx"), "onnx");

        // Default voice matches base dir
        string? resolvedDefault = SpeechVoiceResolver.ResolveVoiceDirectory(_tempDir, defaultVoice, defaultVoice);
        Assert.Equal(_tempDir, resolvedDefault);

        // Another voice does NOT match base dir
        string? resolvedOther = SpeechVoiceResolver.ResolveVoiceDirectory(_tempDir, "ru_RU-dmitri-medium", defaultVoice);
        Assert.Null(resolvedOther);
    }

    [Fact]
    public void EnumerateAvailableVoices_finds_voices_and_formats_error_message()
    {
        string vitsDir = Path.Combine(_tempDir, "vits-piper-ru_RU-irina-medium");
        Directory.CreateDirectory(vitsDir);
        File.WriteAllText(Path.Combine(vitsDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(vitsDir, "model.onnx"), "onnx");

        string plainDir = Path.Combine(_tempDir, "ru_RU-dmitri-medium");
        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(plainDir, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(plainDir, "model.onnx"), "onnx");

        // Invalid dir without onnx
        string emptyDir = Path.Combine(_tempDir, "vits-piper-invalid");
        Directory.CreateDirectory(emptyDir);

        IReadOnlyList<string> voices = SpeechVoiceResolver.EnumerateAvailableVoices(_tempDir);
        Assert.Equal(2, voices.Count);
        Assert.Equal("ru_RU-dmitri-medium", voices[0]);
        Assert.Equal("ru_RU-irina-medium", voices[1]);

        string msg = SpeechVoiceResolver.FormatVoiceNotFoundMessage("unknown-voice", voices);
        Assert.Equal("Голос 'unknown-voice' не установлен. Доступны: ru_RU-dmitri-medium, ru_RU-irina-medium.", msg);

        string emptyMsg = SpeechVoiceResolver.FormatVoiceNotFoundMessage("unknown-voice", Array.Empty<string>());
        Assert.Equal("Голос 'unknown-voice' не установлен. Доступны: (нет).", emptyMsg);
    }
}
