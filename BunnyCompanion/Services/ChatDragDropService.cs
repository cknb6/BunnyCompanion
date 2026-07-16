using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace BunnyCompanion.Services;

/// <summary>
/// 聊天窗拖放解析：资源管理器 + 微信/QQ 等「虚拟文件 / 位图」拖出。
/// 微信 JPG 往往不走标准 FileDrop，而用 FileGroupDescriptorW+FileContents 或 Bitmap。
/// </summary>
public static class ChatDragDropService
{
    private static readonly string[] FileishFormatHints =
    [
        "FileDrop", "FileName", "FileNameW",
        "FileGroupDescriptor", "FileGroupDescriptorW", "FileContents",
        "Shell IDList Array", "Preferred DropEffect",
        "UniformResourceLocator", "UniformResourceLocatorW",
        "text/uri-list", "DragImageBits",
        "Bitmap", "DeviceIndependentBitmap", "System.Drawing.Bitmap",
        "PNG", "JFIF", "image/png", "image/jpeg", "image/jpg",
    ];

    /// <summary>拖入过程中是否应显示可放下（微信在 DragEnter 时常还没有 FileDrop）。</summary>
    public static bool LooksLikeFileOrImageDrag(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop, true))
                return true;
            if (e.Data.GetDataPresent(DataFormats.Bitmap, true))
                return true;
            if (e.Data.GetDataPresent("FileGroupDescriptorW", false)
                || e.Data.GetDataPresent("FileGroupDescriptor", false)
                || e.Data.GetDataPresent("FileContents", false)
                || e.Data.GetDataPresent("FileNameW", true)
                || e.Data.GetDataPresent("FileName", true)
                || e.Data.GetDataPresent("Shell IDList Array", false))
                return true;

            string[] formats;
            try { formats = e.Data.GetFormats(false); }
            catch { formats = []; }

            foreach (var f in formats)
            {
                if (string.IsNullOrEmpty(f))
                    continue;
                foreach (var hint in FileishFormatHints)
                {
                    if (f.Contains(hint, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 微信等：Enter 阶段格式可能为空，但允许 Copy —— 先放行，Drop 再解析
            if (formats.Length == 0 && (e.AllowedEffects & DragDropEffects.Copy) != 0)
                return true;
            if ((e.AllowedEffects & DragDropEffects.Copy) != 0
                && formats.Any(f => f.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                                    || f.Contains("Tencent", StringComparison.OrdinalIgnoreCase)
                                    || f.Contains("QQ", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch
        {
            // 宽松：允许尝试
            return (e.AllowedEffects & DragDropEffects.Copy) != 0;
        }

        return false;
    }

    /// <summary>
    /// 解析拖入内容为本地临时/真实路径列表。
    /// 含：FileDrop、虚拟文件、位图、URI。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractPathsAsync(
        DragEventArgs e,
        CancellationToken ct = default)
    {
        var list = new List<string>();
        var data = e.Data;

        // 1) 标准文件路径
        TryAddFileDrop(data, list);
        TryAddFileName(data, list);

        // 2) 等待微信写完临时文件（路径已有但文件尚未落盘）
        await WaitFilesReadyAsync(list, ct).ConfigureAwait(true);

        // 3) 虚拟文件（微信/邮件/浏览器下载拖出）
        if (list.Count == 0 || list.All(p => !File.Exists(p)))
        {
            var virtualPaths = ExtractVirtualFilesToTemp(data);
            foreach (var p in virtualPaths)
            {
                if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    list.Add(p);
            }
        }

        // 4) 位图（微信有时只给 Bitmap，没有路径）
        if (list.Count == 0 || list.All(p => !File.Exists(p)))
        {
            var bmpPath = TrySaveBitmapToTemp(data);
            if (!string.IsNullOrEmpty(bmpPath))
                list.Add(bmpPath);
        }

        // 5) URI / 文本路径
        if (list.Count == 0)
            TryAddUriOrTextPaths(data, list);

        // 再等一轮存在性
        await WaitFilesReadyAsync(list, ct).ConfigureAwait(true);

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
    }

    private static void TryAddFileDrop(System.Windows.IDataObject data, List<string> list)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, true))
                return;
            switch (data.GetData(DataFormats.FileDrop, true))
            {
                case string[] arr:
                    list.AddRange(arr.Where(s => !string.IsNullOrWhiteSpace(s)));
                    break;
                case string one when !string.IsNullOrWhiteSpace(one):
                    list.Add(one);
                    break;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryAddFileName(System.Windows.IDataObject data, List<string> list)
    {
        foreach (var fmt in new[] { "FileNameW", "FileName" })
        {
            try
            {
                if (!data.GetDataPresent(fmt, true))
                    continue;
                switch (data.GetData(fmt, true))
                {
                    case string[] arr:
                        list.AddRange(arr.Where(s => !string.IsNullOrWhiteSpace(s)));
                        break;
                    case string one when !string.IsNullOrWhiteSpace(one):
                        list.Add(one);
                        break;
                    case System.Collections.IEnumerable en:
                        foreach (var o in en)
                        {
                            if (o is string s && !string.IsNullOrWhiteSpace(s))
                                list.Add(s);
                        }
                        break;
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryAddUriOrTextPaths(System.Windows.IDataObject data, List<string> list)
    {
        try
        {
            foreach (var fmt in new[] { "text/uri-list", "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text })
            {
                if (!data.GetDataPresent(fmt, true))
                    continue;
                var raw = data.GetData(fmt, true)?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                foreach (var line in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith('#') || t.Length < 2)
                        continue;
                    if (t.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var uri = new Uri(t);
                            list.Add(uri.LocalPath);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                    else if (t.Length >= 3 && t[1] == ':' && (t[2] == '\\' || t[2] == '/'))
                    {
                        list.Add(t);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task WaitFilesReadyAsync(List<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0)
            return;
        // 微信临时文件可能晚几百毫秒才写完
        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = paths.Where(p =>
            {
                try
                {
                    if (!File.Exists(p))
                        return true;
                    var fi = new FileInfo(p);
                    return fi.Length == 0;
                }
                catch
                {
                    return true;
                }
            }).ToList();
            if (pending.Count == 0)
                return;
            await Task.Delay(120, ct).ConfigureAwait(true);
        }
    }

    private static string? TrySaveBitmapToTemp(System.Windows.IDataObject data)
    {
        try
        {
            BitmapSource? src = null;
            if (data.GetDataPresent(DataFormats.Bitmap, true))
            {
                var obj = data.GetData(DataFormats.Bitmap, true);
                src = obj as BitmapSource;
                if (src is null && obj is System.Drawing.Bitmap gdi)
                {
                    using var ms = new MemoryStream();
                    gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    src = bi;
                }
            }

            // DeviceIndependentBitmap → 部分客户端
            if (src is null && data.GetDataPresent("DeviceIndependentBitmap", true))
            {
                // 交由失败回退
            }

            if (src is null)
                return null;

            if (src.CanFreeze && !src.IsFrozen)
                src.Freeze();

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BunnyCompanion", "drop-cache");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"wechat_img_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(path))
                encoder.Save(fs);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 虚拟文件：FileGroupDescriptorW + FileContents ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public long sizel; // SIZE 8 bytes as long
        public long pointl; // POINTL 8 bytes
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    private static List<string> ExtractVirtualFilesToTemp(System.Windows.IDataObject data)
    {
        var result = new List<string>();
        try
        {
            // 优先 Unicode 描述符
            MemoryStream? descStream = null;
            var unicode = false;
            if (data.GetDataPresent("FileGroupDescriptorW", false))
            {
                descStream = data.GetData("FileGroupDescriptorW", false) as MemoryStream
                             ?? WrapToMemoryStream(data.GetData("FileGroupDescriptorW", false));
                unicode = true;
            }
            else if (data.GetDataPresent("FileGroupDescriptor", false))
            {
                descStream = data.GetData("FileGroupDescriptor", false) as MemoryStream
                             ?? WrapToMemoryStream(data.GetData("FileGroupDescriptor", false));
            }

            if (descStream is null)
                return result;

            var names = unicode
                ? ReadFileNamesFromDescriptorW(descStream)
                : ReadFileNamesFromDescriptorA(descStream);

            if (names.Count == 0)
                return result;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BunnyCompanion", "drop-cache");
            Directory.CreateDirectory(dir);

            // 通过 COM IDataObject 按 lindex 取 FileContents
            var com = GetComDataObject(data);
            for (var i = 0; i < names.Count; i++)
            {
                var name = SanitizeFileName(names[i]);
                if (string.IsNullOrWhiteSpace(name))
                    name = $"drop_{i}.bin";

                Stream? content = null;
                try
                {
                    content = TryGetFileContentsStream(data, com, i);
                    if (content is null)
                        continue;

                    var dest = Path.Combine(dir, $"{DateTime.Now:HHmmss_fff}_{i}_{name}");
                    using (content)
                    using (var fs = File.Create(dest))
                    {
                        content.CopyTo(fs);
                    }

                    if (new FileInfo(dest).Length > 0)
                        result.Add(dest);
                }
                catch
                {
                    // 单文件失败继续
                }
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    private static MemoryStream? WrapToMemoryStream(object? data)
    {
        switch (data)
        {
            case MemoryStream ms:
                return ms;
            case byte[] bytes:
                return new MemoryStream(bytes);
            case Stream s:
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
            default:
                return null;
        }
    }

    private static List<string> ReadFileNamesFromDescriptorW(MemoryStream stream)
    {
        var list = new List<string>();
        stream.Position = 0;
        using var br = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
        if (stream.Length < 4)
            return list;
        var count = br.ReadInt32();
        if (count is <= 0 or > 64)
            return list;

        var structSize = Marshal.SizeOf<FILEDESCRIPTORW>();
        for (var i = 0; i < count; i++)
        {
            var bytes = br.ReadBytes(structSize);
            if (bytes.Length < structSize)
                break;
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var fd = Marshal.PtrToStructure<FILEDESCRIPTORW>(handle.AddrOfPinnedObject());
                if (!string.IsNullOrWhiteSpace(fd.cFileName))
                    list.Add(fd.cFileName.TrimEnd('\0'));
            }
            finally
            {
                handle.Free();
            }
        }

        return list;
    }

    private static List<string> ReadFileNamesFromDescriptorA(MemoryStream stream)
    {
        // ANSI FILEDESCRIPTOR：文件名 260 ANSI 字节，结构更小；简化：只尝试按 W 失败时的兜底
        var list = new List<string>();
        stream.Position = 0;
        using var br = new BinaryReader(stream, Encoding.Default, leaveOpen: true);
        if (stream.Length < 4)
            return list;
        var count = br.ReadInt32();
        if (count is <= 0 or > 64)
            return list;
        // 粗略跳过：每个描述符约 592 字节（FILEDESCRIPTORA）
        const int approx = 592;
        for (var i = 0; i < count; i++)
        {
            var bytes = br.ReadBytes(approx);
            if (bytes.Length < 260)
                break;
            // 文件名在结构末尾 260 字节
            var nameBytes = bytes[^260..];
            var name = Encoding.Default.GetString(nameBytes).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(name))
                list.Add(name);
        }

        return list;
    }

    private static IComDataObject? GetComDataObject(System.Windows.IDataObject data)
    {
        try
        {
            // WPF DataObject 内部常可转为 COM
            if (data is IComDataObject com)
                return com;
            // 包装一层 Forms DataObject 再取
            var forms = new System.Windows.Forms.DataObject(data);
            return forms as IComDataObject ?? data as IComDataObject;
        }
        catch
        {
            return data as IComDataObject;
        }
    }

    private static Stream? TryGetFileContentsStream(
        System.Windows.IDataObject data,
        IComDataObject? com,
        int index)
    {
        // 先试 WPF 简单 GetData（单文件时有时直接给 Stream）
        if (index == 0)
        {
            try
            {
                if (data.GetDataPresent("FileContents", false))
                {
                    var o = data.GetData("FileContents", false);
                    var ms = WrapToMemoryStream(o);
                    if (ms is not null)
                        return ms;
                    if (o is Stream s)
                        return s;
                }
            }
            catch
            {
                // ignore
            }
        }

        if (com is null)
            return null;

        try
        {
            var format = System.Windows.DataFormats.GetDataFormat("FileContents");
            var formatEtc = new FORMATETC
            {
                cfFormat = (short)format.Id,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTORAGE,
            };
            com.GetData(ref formatEtc, out var medium);
            try
            {
                if (medium.tymed == TYMED.TYMED_ISTREAM && medium.unionmember != IntPtr.Zero)
                {
                    var iStream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    return ComStreamToMemoryStream(iStream);
                }

                if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
                {
                    var lenPtr = GlobalSize(medium.unionmember);
                    var len = lenPtr.ToInt64();
                    if (len <= 0 || len > 80L * 1024 * 1024)
                        return null;
                    var ptr = GlobalLock(medium.unionmember);
                    try
                    {
                        var n = (int)len;
                        var bytes = new byte[n];
                        Marshal.Copy(ptr, bytes, 0, n);
                        return new MemoryStream(bytes);
                    }
                    finally
                    {
                        GlobalUnlock(medium.unionmember);
                    }
                }
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static MemoryStream ComStreamToMemoryStream(IStream iStream)
    {
        var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        // ComTypes.IStream.Read 第三参是 IntPtr，不是 out int
        var pcbRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(pcbRead, 0);
                iStream.Read(buffer, buffer.Length, pcbRead);
                var read = Marshal.ReadInt32(pcbRead);
                if (read <= 0)
                    break;
                ms.Write(buffer, 0, read);
                if (ms.Length > 80L * 1024 * 1024)
                    break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pcbRead);
        }

        ms.Position = 0;
        return ms;
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            name = name[..Math.Min(100, name.Length - ext.Length)] + ext;
        }

        return name;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
}
