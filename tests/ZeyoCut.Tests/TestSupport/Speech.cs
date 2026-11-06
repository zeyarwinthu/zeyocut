// SPDX-License-Identifier: MIT

using ZeyoCut.Render.Export;
using ZeyoCut.Render.Video;

namespace ZeyoCut.Tests.TestSupport;

// Spoken test audio from Windows text-to-speech, and the whisper install to transcribe it.
public static class Speech
{
    private static readonly Lazy<WhisperPaths?> Paths = new(() =>
        WhisperLocator.TryLocate(null, null, out var paths, out _) ? paths : null);

    public static WhisperPaths RequireWhisper()
    {
        Assert.SkipWhen(Paths.Value is null, "whisper-cli not found (set WHISPER_PATH to whisper-cli.exe or its folder, or put it on PATH); skipping transcript test");
        return Paths.Value!;
    }

    // Writes 'text' spoken by the default Windows voice as a WAV file.
    public static string Speak(string text, string path)
    {
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
            + $"$s.SetOutputToWaveFile('{path.Replace("'", "''")}'); $s.Speak('{text.Replace("'", "''")}'); $s.Dispose()";
        var result = ProcessRunner.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(60));
        Assert.SkipWhen(result.ExitCode != 0 || !File.Exists(path), $"Windows text-to-speech is not available: {result.StandardError}");
        return path;
    }
}
