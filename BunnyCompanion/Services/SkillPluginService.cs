using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 技能插件系统：Markdown 技能目录 + 外部命令插件。
/// 每个技能是一个 .md 文件，带 YAML frontmatter：
///   ---
///   name: 技能名
///   description: 一句话描述
///   triggers: 触发词1,触发词2
///   command: powershell 或可执行路径（可选）
///   ---
///   正文：给 Agent 的指令模板/说明。
/// 目录：%LocalAppData%\BunnyCompanion\skills\
/// 无 NuGet 依赖，纯本地解析。
/// </summary>
public sealed class SkillPluginService
{
    private readonly string _skillsDir;
    private readonly object _gate = new();

    public SkillPluginService(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BunnyCompanion");
        _skillsDir = Path.Combine(dir, "skills");
        Directory.CreateDirectory(_skillsDir);
        EnsureBuiltinSkills();
    }

    public string SkillsDirectory => _skillsDir;

    public sealed record Skill(
        string FileName,
        string Name,
        string Description,
        IReadOnlyList<string> Triggers,
        string? Command,
        string Body);

    /// <summary>加载所有技能（.md 文件）。</summary>
    public List<Skill> LoadAll()
    {
        var list = new List<Skill>();
        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(_skillsDir, "*.md"))
            {
                try
                {
                    var skill = Parse(File.ReadAllText(file), Path.GetFileName(file));
                    if (skill is not null)
                        list.Add(skill);
                }
                catch
                {
                    // 单个技能解析失败跳过
                }
            }
        }
        return list;
    }

    /// <summary>按用户文本匹配技能（触发词命中）。</summary>
    public Skill? Match(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;
        foreach (var skill in LoadAll())
        {
            if (skill.Triggers.Any(t => userText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return skill;
        }
        return null;
    }

    /// <summary>列出技能摘要，供 Agent 工具返回。</summary>
    public string ListText()
    {
        var skills = LoadAll();
        if (skills.Count == 0)
            return "（暂无技能，可在 " + _skillsDir + " 放 .md 技能文件）";
        var sb = new StringBuilder();
        sb.AppendLine($"技能目录: {_skillsDir}");
        sb.AppendLine($"共 {skills.Count} 个技能：");
        foreach (var s in skills)
        {
            sb.Append("- ").Append(s.Name);
            if (!string.IsNullOrWhiteSpace(s.Description))
                sb.Append("：").Append(s.Description);
            if (s.Triggers.Count > 0)
                sb.Append("（触发词：").Append(string.Join("/", s.Triggers)).Append("）");
            if (!string.IsNullOrWhiteSpace(s.Command))
                sb.Append(" [带命令]");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>读取技能正文，供注入 Agent 指令。</summary>
    public string? GetBody(string nameOrFile)
    {
        var skills = LoadAll();
        var hit = skills.FirstOrDefault(s => s.Name.Equals(nameOrFile, StringComparison.OrdinalIgnoreCase)
                                             || s.FileName.Equals(nameOrFile, StringComparison.OrdinalIgnoreCase));
        return hit?.Body;
    }

    /// <summary>执行技能里定义的命令（PowerShell 或可执行），返回输出。</summary>
    public string RunCommand(string nameOrFile, string? arguments = null, int timeoutSec = 30)
    {
        var skills = LoadAll();
        var hit = skills.FirstOrDefault(s => s.Name.Equals(nameOrFile, StringComparison.OrdinalIgnoreCase)
                                             || s.FileName.Equals(nameOrFile, StringComparison.OrdinalIgnoreCase));
        if (hit is null || string.IsNullOrWhiteSpace(hit.Command))
            return $"错误：技能 {nameOrFile} 未找到或未定义 command";

        var cmd = hit.Command.Trim();
        try
        {
            // command 形如：powershell -Command "..." 或直接 exe 路径
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (cmd.StartsWith("powershell", StringComparison.OrdinalIgnoreCase)
                || cmd.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                // 把 command 当作 PowerShell 命令体
                var body = Regex.Replace(cmd, @"^(powershell|pwsh)\s+(-Command\s+)?", "", RegexOptions.IgnoreCase).Trim('"');
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + QuotePs(body);
            }
            else
            {
                // 当作可执行路径 + 可选参数
                var parts = SplitArgs(cmd);
                psi.FileName = parts[0];
                psi.Arguments = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";
            }

            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments += " " + arguments;

            using var proc = Process.Start(psi);
            if (proc is null) return "错误：无法启动进程";
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            var finished = proc.WaitForExit(timeoutSec * 1000);
            if (!finished)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return $"错误：命令超时（{timeoutSec}s）\nSTDOUT:\n{Trim(stdout)}\nSTDERR:\n{Trim(stderr)}";
            }
            return $"exit={proc.ExitCode}\nSTDOUT:\n{Trim(stdout)}\nSTDERR:\n{Trim(stderr)}";
        }
        catch (Exception ex)
        {
            return $"错误：{ex.Message}";
        }
    }

    // ---------- 内置示例技能 ----------

    private void EnsureBuiltinSkills()
    {
        // 首次创建几个示例技能，让用户知道怎么写
        void WriteIfMissing(string file, string content)
        {
            var path = Path.Combine(_skillsDir, file);
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, content, Encoding.UTF8); } catch { /* ignore */ }
            }
        }

        WriteIfMissing("清理临时文件.md", """
            ---
            name: 清理临时文件
            description: 清理当前用户临时目录里的旧文件
            triggers: 清理临时,清temp,清理缓存
            command: powershell -Command "Get-ChildItem $env:TEMP -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item -Force -ErrorAction SilentlyContinue; '已清理7天前的临时文件'"
            ---
            当用户说“清理临时文件”时执行：删除 %TEMP% 下超过 7 天的文件。
            执行后用一句话告诉用户清理完成。
            """);

        WriteIfMissing("今日待办.md", """
            ---
            name: 今日待办
            description: 用备忘工具列出今日待办
            triggers: 今日待办,今天要做什么,今日任务
            ---
            当用户问“今天要做什么”时，调用 memo_list 工具列出未完成备忘，并用温柔语气总结今日重点。
            """);

        WriteIfMissing("打开常用.md", """
            ---
            name: 打开常用
            description: 打开一组常用网站
            triggers: 打开常用,开常用网站,打开工作台
            command: powershell -Command "Start-Process 'https://github.com'; Start-Process 'https://www.bing.com'"
            ---
            当用户说“打开常用”时，用默认浏览器打开 GitHub 和 Bing。
            """);
    }

    // ---------- 解析 ----------

    private static Skill? Parse(string raw, string fileName)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string? name = null, desc = null, cmd = null;
        var triggers = new List<string>();
        string body = raw;

        // frontmatter: --- ... ---
        var fmMatch = Regex.Match(raw, @"^---\s*\n(.*?)\n---\s*\n(.*)$", RegexOptions.Singleline);
        if (fmMatch.Success)
        {
            var fm = fmMatch.Groups[1].Value;
            body = fmMatch.Groups[2].Value.Trim();

            foreach (var line in fm.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim().ToLowerInvariant();
                var val = line[(idx + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "name": name = val; break;
                    case "description": desc = val; break;
                    case "triggers":
                        triggers = val.Split(',', '，', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        break;
                    case "command": cmd = val; break;
                }
            }
        }

        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name!;
        return new Skill(fileName, name, desc ?? "", triggers, cmd, body);
    }

    private static string QuotePs(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string[] SplitArgs(string cmd)
    {
        // 简单按空格切，不处理引号嵌套（够用）
        return cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Trim(StringBuilder sb)
    {
        var s = sb.ToString();
        return s.Length > 4000 ? s[..4000] + "\n…(输出过长已截断)" : (s.Length == 0 ? "(空)" : s);
    }
}
