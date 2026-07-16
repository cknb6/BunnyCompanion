using System.Runtime.InteropServices;

namespace BunnyCompanion.Services;

/// <summary>
/// 语音能力：TTS 朗读（Windows SAPI COM）+ 语音识别（SAPI）。
/// 纯 COM Interop，无 NuGet 依赖。仅 Windows 可用，macOS 交叉编译时这些调用不会执行。
/// </summary>
public static class VoiceService
{
    // ---------- SAPI COM 接口（最小定义，避免引用 System.Speech 包） ----------

    [ComImport, Guid("6C44AC74-72D9-4D2D-AE78-B189D6E9ED6B"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface ISpVoice
    {
        [DispId(1)]
        void Speak(string text, uint flags, out uint streamNumber);
        [DispId(3)]
        void SetRate(int rate);
        [DispId(4)]
        int GetRate();
        [DispId(5)]
        void SetVolume(ushort volume);
        [DispId(6)]
        ushort GetVolume();
    }

    [ComImport, Guid("9673A42B-0DB8-4E6A-8C70-3A6B9BE9F0E4"), ClassInterface(ClassInterfaceType.None)]
    private class SpVoiceClass { }

    private const uint SpeakFlagsPurgeBeforeSpeak = 0x20; // SPF_PURGEBEFORESPEAK
    private const uint SpeakFlagsAsync = 0x1; // SPF_ASYNC

    private static ISpVoice? _voice;
    private static readonly object _gate = new();
    private static bool _initFailed;

    /// <summary>是否可用 TTS（SAPI 存在且初始化成功）。</summary>
    public static bool IsTtsAvailable
    {
        get
        {
            lock (_gate)
            {
                EnsureVoice();
                return _voice is not null;
            }
        }
    }

    private static void EnsureVoice()
    {
        if (_voice is not null || _initFailed)
            return;
        try
        {
            _voice = (ISpVoice)new SpVoiceClass();
            _voice.SetVolume(100);
        }
        catch
        {
            _initFailed = true;
        }
    }

    /// <summary>朗读文本（异步，可被打断）。失败静默，不阻断主流程。</summary>
    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (_gate)
        {
            EnsureVoice();
            if (_voice is null)
                return;
            try
            {
                _voice.Speak(text, SpeakFlagsAsync | SpeakFlagsPurgeBeforeSpeak, out _);
            }
            catch
            {
                // SAPI 不可用或被占用，静默
            }
        }
    }

    /// <summary>停止当前朗读。</summary>
    public static void Stop()
    {
        lock (_gate)
        {
            if (_voice is null)
                return;
            try { _voice.Speak("", SpeakFlagsPurgeBeforeSpeak, out _); }
            catch { /* ignore */ }
        }
    }

    // ---------- 语音识别（SAPI 共享识别器，一次性短句） ----------

    /// <summary>
    /// 识别一次短句（阻塞，最多 timeoutMs 毫秒）。返回识别到的文本，失败返回空。
    /// 用 SAPI 共享识别器，简单可用；复杂场景建议后续接 Whisper/Vosk。
    /// </summary>
    public static string RecognizeOnce(int timeoutMs = 6000)
    {
        try
        {
            // SAPI 识别器 COM 定义较繁琐，且共享识别器会抢系统语音焦点。
            // 这里用 PowerShell 调 SAPI 做一次性识别，避免大段 COM 定义。
            var script = """
                Add-Type -AssemblyName System.Speech
                $r = New-Object System.Speech.Recognition.SpeechRecognitionEngine
                $r.SetInputToDefaultAudioInput()
                $g = New-Object System.Speech.Recognition.DictationGrammar
                $r.LoadGrammar($g)
                $r.RecognizeAsyncStop()
                $result = $r.Recognize([TimeSpan]::FromMilliseconds(5000))
                if ($result) { $result.Text } else { '' }
                $r.Dispose()
                """;
            return RunPsCapture(script, timeoutMs).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string RunPsCapture(string script, int timeoutMs)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + QuotePs(script),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return string.Empty;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        return stdout ?? string.Empty;
    }

    private static string QuotePs(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
