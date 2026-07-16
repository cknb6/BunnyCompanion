using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO;

namespace BunnyCompanion.Services;

/// <summary>
/// 语音能力：TTS 朗读 + 语音识别。
/// TTS 优先级：阶跃在线 TTS（真人级，复用现有 key）→ Windows SAPI 离线兜底。
/// ASR：Windows SAPI 离线（PowerShell 调 System.Speech.Recognition，无需录音采集依赖）。
/// 纯 COM/HTTP，无 NuGet 依赖。界面只暴露“语音输入/朗读”，不显示接口名。
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static VoiceService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion/1.3");
    }

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

    /// <summary>Windows PowerShell 与系统语音组件是当前离线识别链路的运行前提。</summary>
    public static bool IsRecognitionAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(powerShell);
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

    /// <summary>
    /// 朗读文本：优先阶跃在线 TTS（真人级），失败回退 SAPI 离线。后台线程执行，不阻塞 UI。
    /// </summary>
    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        // 阶跃 TTS 单次上限 1000 字符
        if (text.Length > 1000)
            text = text[..1000];
        _ = Task.Run(() => SpeakAsync(text));
    }

    private static async Task SpeakAsync(string text)
    {
        // 1) 阶跃在线 TTS（真人级，复用现有 key）
        if (await TryStepTtsAsync(text).ConfigureAwait(false))
            return;
        // 2) SAPI 离线兜底
        SpeakSapi(text);
    }

    /// <summary>阶跃在线 TTS：POST /audio/speech 返回 wav，写临时文件用 SoundPlayer 同步播放。</summary>
    private static async Task<bool> TryStepTtsAsync(string text)
    {
        try
        {
            // StepBaseUrl 为 step_plan/v1，阶跃语音在该域下同样可用
            var url = $"{AiConfig.StepBaseUrl.TrimEnd('/')}/audio/speech";
            var payload = new JsonObject
            {
                ["model"] = AiConfig.StepTtsModel,
                ["input"] = text,
                ["voice"] = AiConfig.StepTtsVoice,
                ["response_format"] = "wav",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiConfig.StepApiKey);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(request).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length < 100)
                return false;

            // 写临时 wav 并同步播放（在后台线程，不影响 UI）
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xiaoshen_tts_{Guid.NewGuid():N}.wav");
            await System.IO.File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            try
            {
                using var player = new System.Media.SoundPlayer(tmp);
                player.PlaySync();
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { /* ignore */ }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>SAPI 离线朗读（同步，应在后台线程调用）。</summary>
    private static void SpeakSapi(string text)
    {
        lock (_gate)
        {
            EnsureVoice();
            if (_voice is null)
                return;
            try { _voice.Speak(text, SpeakFlagsAsync | SpeakFlagsPurgeBeforeSpeak, out _); }
            catch { /* ignore */ }
        }
    }

    /// <summary>停止当前朗读（仅 SAPI；在线 wav 播放中无法中断）。</summary>
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
                [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
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
        timeoutMs = Math.Clamp(timeoutMs, 1000, 30000);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(powerShell) ? powerShell : "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encodedCommand);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return string.Empty;

        // 先异步排空管道，再等待退出；否则 ReadToEnd 会让超时保护永远到不了。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { proc.WaitForExit(2000); } catch { /* ignore */ }
            return string.Empty;
        }

        try
        {
            Task.WaitAll([stdoutTask, stderrTask], 2000);
            return stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
