using System.IO;

namespace BunnyCompanion.Services;

/// <summary>
/// 本机特殊文件夹与路径别名解析（纯 .NET，可供 Agent 工具与自检共用）。
/// 支持：桌面/Desktop、文档、下载、~、环境变量、前缀「桌面\xxx」。
/// </summary>
public static class FolderPathResolver
{
    /// <summary>解析特殊文件夹名（桌面、Desktop、文档…）。失败返回 null。</summary>
    public static string? ResolveAlias(string? name)
    {
        name = (name ?? "").Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(name))
            return null;

        foreach (var (alias, folder) in FolderAliasMap())
        {
            if (name.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return folder;
        }

        return null;
    }

    /// <summary>
    /// 展开路径：别名、~、环境变量、桌面下相对路径。空串默认桌面。
    /// </summary>
    public static string Expand(string? path)
    {
        path = (path ?? "").Trim().Trim('"').Trim('\'');
        if (string.IsNullOrWhiteSpace(path))
            return SafeSpecial(Environment.SpecialFolder.Desktop);

        var exact = ResolveAlias(path);
        if (exact is not null)
            return exact;

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = SafeSpecial(Environment.SpecialFolder.UserProfile);
            var rest = path.Length <= 1 ? "" : path[2..].TrimStart('/', '\\');
            path = string.IsNullOrEmpty(rest) ? home : Path.Combine(home, rest);
        }

        path = Environment.ExpandEnvironmentVariables(path);
        path = ExpandLeadingAlias(path);

        var desktop = SafeSpecial(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop))
        {
            path = path.Replace("/桌面", desktop, StringComparison.OrdinalIgnoreCase)
                .Replace("\\桌面", desktop, StringComparison.OrdinalIgnoreCase);
        }

        if (!Path.IsPathRooted(path))
        {
            var alias = ResolveAlias(path);
            if (alias is not null)
                return alias;
            if (!string.IsNullOrEmpty(desktop))
                path = Path.Combine(desktop, path);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public static string GetDownloadsPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Windows 常见下载目录；也可能被重定向，先试标准名
            foreach (var name in new[] { "Downloads", "下载" })
            {
                var dl = Path.Combine(home, name);
                if (Directory.Exists(dl))
                    return dl;
            }

            return home;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    private static string ExpandLeadingAlias(string path)
    {
        foreach (var (alias, folder) in FolderAliasMap())
        {
            if (path.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return folder;

            var prefixSlash = alias + "/";
            var prefixBack = alias + "\\";
            if (path.StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(folder, path[prefixSlash.Length..].Replace('/', Path.DirectorySeparatorChar));
            if (path.StartsWith(prefixBack, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(folder, path[prefixBack.Length..]);
        }

        return path;
    }

    private static IEnumerable<(string Alias, string Folder)> FolderAliasMap()
    {
        var desktop = SafeSpecial(Environment.SpecialFolder.Desktop);
        var desktopDir = SafeSpecial(Environment.SpecialFolder.DesktopDirectory);
        var docs = SafeSpecial(Environment.SpecialFolder.MyDocuments);
        var pics = SafeSpecial(Environment.SpecialFolder.MyPictures);
        var music = SafeSpecial(Environment.SpecialFolder.MyMusic);
        var videos = SafeSpecial(Environment.SpecialFolder.MyVideos);
        var home = SafeSpecial(Environment.SpecialFolder.UserProfile);
        var appData = SafeSpecial(Environment.SpecialFolder.ApplicationData);
        var local = SafeSpecial(Environment.SpecialFolder.LocalApplicationData);
        var dl = GetDownloadsPath();
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrEmpty(desktop))
        {
            yield return ("desktop", desktop);
            yield return ("桌面", desktop);
            yield return ("我的桌面", desktop);
        }
        else if (!string.IsNullOrEmpty(desktopDir))
        {
            yield return ("desktop", desktopDir);
            yield return ("桌面", desktopDir);
        }

        if (!string.IsNullOrEmpty(docs))
        {
            yield return ("documents", docs);
            yield return ("docs", docs);
            yield return ("文档", docs);
            yield return ("我的文档", docs);
        }

        yield return ("downloads", dl);
        yield return ("download", dl);
        yield return ("下载", dl);

        if (!string.IsNullOrEmpty(pics))
        {
            yield return ("pictures", pics);
            yield return ("图片", pics);
        }

        if (!string.IsNullOrEmpty(music))
        {
            yield return ("music", music);
            yield return ("音乐", music);
        }

        if (!string.IsNullOrEmpty(videos))
        {
            yield return ("videos", videos);
            yield return ("视频", videos);
        }

        if (!string.IsNullOrEmpty(home))
        {
            yield return ("userprofile", home);
            yield return ("home", home);
            yield return ("用户", home);
            yield return ("用户目录", home);
        }

        if (!string.IsNullOrEmpty(appData))
        {
            yield return ("appdata", appData);
            yield return ("roaming", appData);
        }

        if (!string.IsNullOrEmpty(local))
        {
            yield return ("localappdata", local);
            yield return ("local", local);
        }

        yield return ("temp", temp);
        yield return ("临时", temp);
    }

    private static string SafeSpecial(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return "";
        }
    }
}
