using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BunnyCompanion.Models;
using BunnyCompanion.Services;
using Microsoft.Win32;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using TextBox = System.Windows.Controls.TextBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace BunnyCompanion;

public partial class ChatWindow : Window
{
    private readonly PetSettings _settings;
    private readonly AiAgentService _agent;
    private readonly Window? _hostWindow;
    private readonly Action<AgentResult> _onPetReply;
    private readonly Action _onUserSpoke;
    private bool _busy;
    private bool _loadingAttachments;
    private bool _cancelRequested;
    private CancellationTokenSource? _cts;
    private readonly List<PendingAttachment> _pending = [];
    private DateTime _lastTimeStampShown = DateTime.MinValue;
    /// <summary>聊天窗内朗读开关（右上角按钮）；关时立刻 Stop 并不再读新回复。</summary>
    private bool _chatSoundOn;

    private sealed class PendingAttachment
    {
        public required string Path { get; init; }
        public required string FileName { get; init; }
        public required ChatAttachmentKind Kind { get; init; }
        public string? MimeType { get; set; }
        public string? TextPreview { get; set; }
        public byte[]? ImageBytes { get; set; }
        public long SizeBytes { get; init; }
    }

    private sealed record AttachmentLoadResult(PendingAttachment? Item, string? Message);

    public ChatWindow(
        PetSettings settings,
        AiAgentService agent,
        Window? hostWindow,
        Action<AgentResult> onPetReply,
        Action onUserSpoke)
    {
        InitializeComponent();
        _settings = settings;
        _agent = agent;
        _hostWindow = hostWindow;
        _onPetReply = onPetReply;
        _onUserSpoke = onUserSpoke;
        TitleText.Text = settings.PetName;
        Title = settings.PetName;
        Topmost = settings.AlwaysOnTop;
        // 与全局「朗读回复」同步初始状态
        _chatSoundOn = settings.TtsEnabled;
        Closed += (_, _) =>
        {
            try { VoiceService.Stop(); } catch { /* ignore */ }
            _cts?.Cancel();
            _cts?.Dispose();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        RefreshSoundToggleUi();
        AppendTimeIfNeeded(force: true);
        AppendBubble(
            _settings.PetName,
            $"嗨，{_settings.PartnerName}～\n像微信一样随便聊就行。\n• 把文件拖进这个窗口（图片/代码/任意路径）\n• 点「＋」选择，或 Ctrl+V 粘贴截图\n• 点「看桌面」让我瞅屏幕\n断网也没关系，我会继续陪你。",
            isPet: true);
        InputBox.Focus();
    }

    /// <summary>
    /// 按工作区自适应窗口大小，避免小屏裁切、大屏过小。
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            var area = _hostWindow is not null
                ? ScreenService.GetWorkingArea(_hostWindow)
                : new Rect(
                    SystemParameters.WorkArea.Left,
                    SystemParameters.WorkArea.Top,
                    SystemParameters.WorkArea.Width,
                    SystemParameters.WorkArea.Height);

            // 约 1/3 宽、3/4 高，并保证在小屏/高 DPI 下输入区始终可见。
            var maxWidth = Math.Max(280, area.Width - 24);
            var maxHeight = Math.Max(360, area.Height - 24);
            MinWidth = Math.Min(320, maxWidth);
            MinHeight = Math.Min(420, maxHeight);
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
            Width = Math.Min(Math.Clamp(area.Width * 0.34, 360, 560), maxWidth);
            Height = Math.Min(Math.Clamp(area.Height * 0.78, 520, 860), maxHeight);
        }
        catch
        {
            Width = 420;
            Height = 680;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击顶栏：在自适应尺寸与最大化高度之间切换
            try
            {
                var area = ScreenService.GetWorkingArea(this);
                var targetHeight = double.IsFinite(MaxHeight)
                    ? Math.Min(MaxHeight, area.Height)
                    : Math.Max(360, area.Height - 24);
                if (Math.Abs(Height - targetHeight) < 8)
                    FitToScreen();
                else
                {
                    Height = targetHeight;
                    Top = area.Top + Math.Max(0, (area.Height - targetHeight) / 2);
                    Left = Math.Clamp(Left, area.Left, area.Right - Width);
                }
            }
            catch
            {
                // ignore
            }
            return;
        }

        try { DragMove(); } catch { /* ignore */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>右上角声音按钮：开 ↔ 关；关闭时立即停止当前朗读。</summary>
    private void SoundToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _chatSoundOn = !_chatSoundOn;
        if (!_chatSoundOn)
        {
            try { VoiceService.Stop(); } catch { /* ignore */ }
            AppendSystemTip("已关闭朗读声音。再点 🔇 可重新打开。");
        }
        else
        {
            // 同步打开全局朗读偏好，避免设置里关了却只在本窗开却无效
            _settings.TtsEnabled = true;
            AppendSystemTip("已打开朗读声音，之后回复会读出来～再点 🔊 可关闭。");
        }

        RefreshSoundToggleUi();
    }

    private void RefreshSoundToggleUi()
    {
        if (SoundToggleButton is null)
            return;
        if (_chatSoundOn)
        {
            SoundToggleButton.Content = "🔊";
            SoundToggleButton.ToolTip = "声音开 · 点击关闭朗读（并停止当前播放）";
            SoundToggleButton.Opacity = 1;
        }
        else
        {
            SoundToggleButton.Content = "🔇";
            SoundToggleButton.ToolTip = "声音关 · 点击开启朗读";
            SoundToggleButton.Opacity = 0.75;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CancelCurrentRequest();
            return;
        }
        _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);
    }

    private void LookDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (string.IsNullOrWhiteSpace(InputBox.Text))
            InputBox.Text = "帮我看一下桌面，说说现在屏幕上有什么，并给一点建议。";
        DesktopCheck.IsChecked = true;
        _ = SendAsync(includeDesktop: true);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _agent.ClearHistory();
        ChatPanel.Children.Clear();
        ClearPendingAttachments();
        _lastTimeStampShown = DateTime.MinValue;
        AppendTimeIfNeeded(force: true);
        AppendBubble(_settings.PetName, "好，刚才的先放下。我们重新开始聊～", isPet: true);
        StatusText.Text = "在线";
    }

    private void ClearAttachments_Click(object sender, RoutedEventArgs e) =>
        ClearPendingAttachments();

    private void ClearPendingAttachments()
    {
        _pending.Clear();
        AttachPreviewPanel.Children.Clear();
        AttachBar.Visibility = Visibility.Collapsed;
    }

    // ---------- 拖拽文件（资源管理器 + 微信虚拟文件/位图） ----------

    /// <summary>
    /// 文件夹：展开第一层文件（最多补到 6 个附件额度），不递归整树。
    /// </summary>
    private static IEnumerable<string> ExpandPathsForAttach(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            // 先 yield 标记：调用方看到目录会提示；同时给出子文件
            yield return path;
            string[] files;
            try
            {
                files = Directory.GetFiles(path);
            }
            catch
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Take(12))
                yield return f;
        }
    }

    private void SetDropOverlay(bool visible)
    {
        if (!IsLoaded)
            return;
        DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewDragEnter(object sender, DragEventArgs e)
    {
        // 微信拖图在 Enter 时常没有 FileDrop，须宽松识别，且 Handled=true 才能显示可放置
        if (_busy || _loadingAttachments || !ChatDragDropService.LooksLikeFileOrImageDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        SetDropOverlay(true);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_busy || _loadingAttachments || !ChatDragDropService.LooksLikeFileOrImageDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            SetDropOverlay(false);
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        SetDropOverlay(true);
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // 离开窗口时关掉遮罩；在子控件间移动时也可能触发，用坐标判断是否仍在窗内
        try
        {
            var pos = e.GetPosition(this);
            if (pos.X < 2 || pos.Y < 2 || pos.X > ActualWidth - 2 || pos.Y > ActualHeight - 2)
                SetDropOverlay(false);
        }
        catch
        {
            SetDropOverlay(false);
        }

        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        SetDropOverlay(false);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true; // 阻止 TextBox 把路径当文字粘贴

        if (_busy || _loadingAttachments)
        {
            AppendSystemTip("我还在忙/读附件中，稍后再拖文件进来～");
            return;
        }

        StatusText.Text = "正在接收拖入文件…";
        IReadOnlyList<string> paths;
        try
        {
            // 微信 JPG：虚拟文件 / 位图 / 延迟临时路径
            paths = await ChatDragDropService.ExtractPathsAsync(e).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendSystemTip("拖入解析失败：" + ex.Message);
            StatusText.Text = "在线";
            return;
        }

        if (paths.Count == 0)
        {
            AppendSystemTip(
                "没识别到文件。微信可试：①图片另存到桌面再拖 ②或点「＋」选择。" +
                "（已兼容微信虚拟文件与预览位图）");
            StatusText.Text = "在线";
            return;
        }

        await AddAttachmentPathsAsync(ExpandPathsForAttach(paths)).ConfigureAwait(true);
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loadingAttachments)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "选择要发送的文件",
            Multiselect = true,
            Filter =
                "常用文件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.csv;*.log;*.html;*.css;*.sql;*.java;*.go;*.rs;*.cpp;*.h;*.yaml;*.yml" +
                "|图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" +
                "|文本/代码|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.csv;*.log;*.html;*.css;*.sql;*.java;*.go;*.rs;*.cpp;*.h;*.yaml;*.yml" +
                "|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await AddAttachmentPathsAsync(dialog.FileNames).ConfigureAwait(true);
    }

    /// <summary>
    /// 统一入口：对话框选择 / 拖拽 / 剪贴板文件列表 都走这里。
    /// </summary>
    private async Task AddAttachmentPathsAsync(IEnumerable<string> paths)
    {
        if (_busy || _loadingAttachments)
            return;

        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0)
            return;

        var previousStatus = StatusText.Text;
        var before = _pending.Count;
        var folderHints = 0;
        _loadingAttachments = true;
        AttachButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        StatusText.Text = "正在读取附件…";
        try
        {
            foreach (var path in list)
            {
                if (_pending.Count >= 6)
                {
                    AppendSystemTip("一次最多 6 个附件，其余文件已跳过。");
                    break;
                }

                // 图片张数软限制（与多模态 API 一致）
                if (ClassifyAttachment(Path.GetExtension(path).ToLowerInvariant()) == ChatAttachmentKind.Image
                    && _pending.Count(p => p.Kind == ChatAttachmentKind.Image) >= 4)
                {
                    AppendSystemTip("图片最多 4 张，其余图片已跳过（文本/其它文件仍可加）。");
                    continue;
                }

                // 文件夹：提示（Expand 可能已塞入子文件）；目录本身不当附件
                if (Directory.Exists(path) && !File.Exists(path))
                {
                    folderHints++;
                    if (folderHints == 1)
                    {
                        AppendSystemTip(
                            $"「{Path.GetFileName(path)}」是文件夹：已尝试添加其中文件；完整操作可让我说「列出这个目录」。");
                    }
                    continue;
                }

                if (!File.Exists(path))
                {
                    AppendSystemTip($"找不到文件：{path}");
                    continue;
                }

                // 已添加同路径则跳过
                if (_pending.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var result = await Task.Run(() => LoadAttachment(path)).ConfigureAwait(true);
                if (!IsLoaded)
                    return;
                if (result.Item is not null)
                    _pending.Add(result.Item);
                if (!string.IsNullOrWhiteSpace(result.Message))
                    AppendSystemTip(result.Message);
            }
        }
        finally
        {
            _loadingAttachments = false;
            if (IsLoaded)
            {
                RefreshAttachBar();
                AttachButton.IsEnabled = !_busy;
                SendButton.IsEnabled = !_busy || _cancelRequested;
                var added = _pending.Count - before;
                if (added > 0)
                {
                    StatusText.Text = $"已添加 {added} 个附件，可发送";
                    AppendSystemTip($"✅ 已加入 {_pending.Count} 个待发送附件（点发送交给 Agent）。");
                }
                else if (StatusText.Text == "正在读取附件…")
                {
                    StatusText.Text = string.IsNullOrWhiteSpace(previousStatus) ? "在线" : previousStatus;
                }
            }
        }
    }

    /// <summary>从剪贴板添加文件列表或截图。</summary>
    private async Task<bool> TryAddFromClipboardAsync()
    {
        if (_busy || _loadingAttachments)
            return false;

        try
        {
            // 1) 资源管理器复制的文件
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var drop = System.Windows.Clipboard.GetFileDropList();
                if (drop is { Count: > 0 })
                {
                    var paths = drop.Cast<string?>().Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>();
                    await AddAttachmentPathsAsync(paths).ConfigureAwait(true);
                    return true;
                }
            }

            // 2) 截图 / 画图复制的位图
            if (System.Windows.Clipboard.ContainsImage())
            {
                var src = System.Windows.Clipboard.GetImage();
                if (src is null)
                    return false;

                var bytes = EncodeBitmapSourceToPng(src);
                if (bytes is null || bytes.Length == 0)
                {
                    AppendSystemTip("剪贴板图片读取失败，请另存为文件后再拖入。");
                    return true;
                }

                if (_pending.Count >= 6)
                {
                    AppendSystemTip("一次最多 6 个附件，请先清除部分再粘贴。");
                    return true;
                }

                var normalized = TryDownscaleImage(bytes);
                if (normalized is null && bytes.Length > 1_200_000)
                {
                    AppendSystemTip("剪贴板图片过大且压缩失败，请另存为较小文件后再添加。");
                    return true;
                }

                var payload = normalized ?? bytes;
                var name = $"粘贴图片_{DateTime.Now:HHmmss}.png";
                _pending.Add(new PendingAttachment
                {
                    Path = name,
                    FileName = name,
                    Kind = ChatAttachmentKind.Image,
                    MimeType = normalized is not null ? "image/jpeg" : "image/png",
                    ImageBytes = payload,
                    SizeBytes = payload.LongLength,
                });
                RefreshAttachBar();
                AppendSystemTip($"已从剪贴板添加图片「{name}」。");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppendSystemTip("粘贴附件失败：" + ex.Message);
            return true;
        }

        return false;
    }

    private static byte[]? EncodeBitmapSourceToPng(BitmapSource source)
    {
        try
        {
            // 统一 Freeze，避免跨线程/跨调度器问题
            if (source.CanFreeze && !source.IsFrozen)
                source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static AttachmentLoadResult LoadAttachment(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AttachmentLoadResult(null, "文件不存在或已被移动，已跳过。");

            var info = new FileInfo(path);
            // 单文件上限 8MB，避免撑爆 API（路径类附件也限制，防止误拖巨型 ISO）
            if (info.Length > 8 * 1024 * 1024)
                return new AttachmentLoadResult(null, $"「{info.Name}」超过 8MB，已跳过。");

            var ext = info.Extension.ToLowerInvariant();
            var kind = ClassifyAttachment(ext);
            var fullPath = info.FullName;
            var item = new PendingAttachment
            {
                Path = fullPath,
                FileName = info.Name,
                Kind = kind,
                MimeType = GuessMime(ext),
                SizeBytes = info.Length,
            };

            if (kind == ChatAttachmentKind.Image)
            {
                var original = File.ReadAllBytes(fullPath);
                // 所有图片统一解码并限到 1280px，防止小体积超大像素图耗尽内存。
                var normalized = TryDownscaleImage(original);
                if (normalized is not null)
                {
                    item.ImageBytes = normalized;
                    item.MimeType = "image/jpeg";
                    return new AttachmentLoadResult(item, null);
                }

                // 解码失败时：仅在原图不大时直传，避免把数 MB 原图塞进多模态导致静默丢弃却仍声称「已附图」
                if (original.Length <= 1_200_000)
                {
                    item.ImageBytes = original;
                    return new AttachmentLoadResult(item, null);
                }

                return new AttachmentLoadResult(
                    null, $"「{info.Name}」图片处理失败或体积过大，已跳过。请换一张或先压缩。");
            }

            if (kind == ChatAttachmentKind.Text)
            {
                // 文本最多读 80KB，保证完整可读又不爆上下文
                item.TextPreview = ReadTextFileLimited(fullPath, maxChars: 80_000);
                return new AttachmentLoadResult(item, null);
            }

            // 其它类型：仍加入附件，只交本机绝对路径给 Agent（可用工具处理）
            var pathOnly = new PendingAttachment
            {
                Path = fullPath,
                FileName = info.Name,
                Kind = ChatAttachmentKind.Other,
                MimeType = "application/octet-stream",
                SizeBytes = info.Length,
            };
            return new AttachmentLoadResult(
                pathOnly, $"「{info.Name}」已作为本机路径交给 Agent（可用工具打开/读取/移动）。");
        }
        catch
        {
            return new AttachmentLoadResult(null, "读取文件失败，请换一个文件试试。");
        }
    }

    private void RefreshAttachBar()
    {
        AttachPreviewPanel.Children.Clear();
        if (_pending.Count == 0)
        {
            AttachBar.Visibility = Visibility.Collapsed;
            return;
        }

        AttachBar.Visibility = Visibility.Visible;
        foreach (var item in _pending)
        {
            var chip = new Border
            {
                Background = ColorBrush("#FFFFFF"),
                BorderBrush = ColorBrush("#E5E5E5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 6, 4),
            };
            var sizeKb = Math.Max(1, item.SizeBytes / 1024);
            var label = item.Kind switch
            {
                ChatAttachmentKind.Image => $"🖼 {item.FileName} · {sizeKb}KB",
                ChatAttachmentKind.Text => $"📄 {item.FileName} · {sizeKb}KB",
                _ => $"📎 {item.FileName} · 路径",
            };
            chip.Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = ColorBrush("#191919"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220,
            };
            AttachPreviewPanel.Children.Add(chip);
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V：优先尝试粘贴文件/截图为附件（有文件时不往输入框塞路径乱码）
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            _ = HandlePasteAsync(e);
            return;
        }

        // Enter 发送；Shift+Enter 换行（更接近微信习惯）
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);
        }
    }

    private async Task HandlePasteAsync(KeyEventArgs e)
    {
        // 先探测是否有文件/图，再决定是否拦截默认粘贴
        var hasFileOrImage = false;
        try
        {
            hasFileOrImage = System.Windows.Clipboard.ContainsFileDropList()
                             || System.Windows.Clipboard.ContainsImage();
        }
        catch
        {
            // 剪贴板被占用时放行默认粘贴
        }

        if (!hasFileOrImage)
            return;

        e.Handled = true;
        await TryAddFromClipboardAsync().ConfigureAwait(true);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 窗口级 Ctrl+V：输入框未聚焦时也能贴附件
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Alt) == 0
            && !InputBox.IsKeyboardFocusWithin)
        {
            _ = HandlePasteAsync(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_busy)
                CancelCurrentRequest();
            else
                Close();
            e.Handled = true;
        }
    }

    private void QuickChip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (sender is Button { Tag: string text })
        {
            InputBox.Text = text;
            _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);
        }
    }

    private async Task SendAsync(bool includeDesktop)
    {
        if (_busy || _loadingAttachments)
            return;

        var text = InputBox.Text.Trim();
        var hasAttach = _pending.Count > 0;
        if (text.Length == 0 && !includeDesktop && !hasAttach)
            return;

        InputBox.Clear();
        var attachments = SnapshotAttachments();
        ClearPendingAttachments();

        if (IsLoaded)
        {
            AppendTimeIfNeeded();
            var display = BuildUserDisplayText(text, attachments, includeDesktop);
            AppendBubble("我", display, isPet: false, attachments: attachments);
            // 图片气泡额外展示缩略图
            foreach (var a in attachments.Where(x => x.Kind == ChatAttachmentKind.Image && x.ImageBytes is { Length: > 0 }))
                AppendImageBubble(isPet: false, a.ImageBytes!, a.FileName);
        }

        _onUserSpoke();
        _cts?.Cancel();
        _cts?.Dispose();
        var requestCts = new CancellationTokenSource();
        _cts = requestCts;
        _cancelRequested = false;
        SetBusy(true, includeDesktop ? "正在看你的桌面…" : hasAttach ? "正在看你发的文件…" : "在线思考中…");
        var finalStatus = "在线";

        // UI 线程进度：工具执行状态显示在状态栏
        var progress = new Progress<string>(msg =>
        {
            if (!IsLoaded || !_busy || !ReferenceEquals(_cts, requestCts)
                          || requestCts.IsCancellationRequested)
                return;
            StatusText.Text = msg;
            TypingHint.Text = msg;
        });

        try
        {
            var result = await _agent.ChatAsync(
                    text,
                    _settings,
                    includeDesktop,
                    _hostWindow,
                    attachments,
                    progress,
                    requestCts.Token)
                .ConfigureAwait(true);
            if (!IsLoaded)
                return;

            AppendTimeIfNeeded();
            // 工具轨迹（轻量系统提示，不打断微信气泡主对话）
            if (result.ToolTrace is { Count: > 0 })
            {
                var tools = string.Join(" → ", result.ToolTrace.Distinct().Take(8));
                AppendSystemTip($"Agent 工具：{tools}");
            }

            // 完整显示回复，不截断正文
            AppendBubble(_settings.PetName, result.Text, isPet: true, meta: null);
            var status = ShortProvider(result.Provider);
            if (result.UsedDesktopImage) status = "已看桌面 · " + status;
            if (result.ToolTrace is { Count: > 0 }) status += " · 已调用本机工具";
            StatusText.Text = status;
            finalStatus = status;
            // TTS 朗读回复（需用户在设置开启，且非安静时段）
            TrySpeakReply(result.Text);
            _onPetReply(result);
        }
        catch (OperationCanceledException)
        {
            finalStatus = "已停止";
            if (IsLoaded)
                AppendBubble(_settings.PetName, "好，那先这样～", isPet: true);
        }
        catch (Exception)
        {
            var offline = ChatReplyService.Reply(text, _settings, offlineMode: true, desktopRequested: includeDesktop);
            if (IsLoaded)
            {
                AppendBubble(
                    _settings.PetName,
                    offline.Text + "\n（网络不太好，我先用本地模式陪你）",
                    isPet: true);
                StatusText.Text = "本地陪伴";
            }
            finalStatus = "本地陪伴";
            _onPetReply(new AgentResult(offline.Text, offline.ActionKey, offline.AffectionGain, "本地", false));
        }
        finally
        {
            if (ReferenceEquals(_cts, requestCts))
            {
                _cts.Dispose();
                _cts = null;
            }
            if (IsLoaded)
            {
                SetBusy(false, finalStatus);
                InputBox.Focus();
            }
        }
    }

    private void CancelCurrentRequest()
    {
        if (!_busy || _cancelRequested)
            return;
        _cancelRequested = true;
        StatusText.Text = "正在停止…";
        TypingHint.Text = "正在停止…";
        SendButton.Content = "停止中…";
        SendButton.IsEnabled = false;
        _cts?.Cancel();
    }

    private List<ChatAttachment> SnapshotAttachments()
    {
        var list = new List<ChatAttachment>(_pending.Count);
        foreach (var p in _pending)
        {
            list.Add(new ChatAttachment(
                FileName: p.FileName,
                Kind: p.Kind,
                MimeType: p.MimeType,
                TextContent: p.TextPreview,
                ImageBytes: p.ImageBytes,
                FullPath: p.Path));
        }
        return list;
    }

    private static string BuildUserDisplayText(string text, IReadOnlyList<ChatAttachment> attachments, bool desktop)
    {
        var parts = new List<string>();
        if (text.Length > 0)
            parts.Add(text);
        if (attachments.Count > 0)
        {
            var names = string.Join("、", attachments.Select(a => a.FileName));
            parts.Add($"[附件 {attachments.Count} 个：{names}]");
        }
        if (desktop && text.Length == 0 && attachments.Count == 0)
            parts.Add("[请看桌面]");
        else if (desktop)
            parts.Add("[附带桌面截图]");
        return string.Join("\n", parts);
    }

    private static string ShortProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return "在线";
        if (provider.Contains("阶跃", StringComparison.Ordinal) || provider.Contains("预取", StringComparison.Ordinal))
            return "在线";
        if (provider.Contains("备用", StringComparison.Ordinal) || provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase))
            return "在线·备用";
        if (provider.Contains("本地", StringComparison.Ordinal))
            return "本地陪伴";
        if (provider.Contains("阶跃", StringComparison.Ordinal))
            return "在线";
        if (provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase))
            return "在线";
        return "在线";
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        if (!busy)
            _cancelRequested = false;
        SendButton.IsEnabled = !_loadingAttachments && (!busy || !_cancelRequested);
        InputBox.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;
        StatusText.Text = status;
        TypingHint.Text = busy ? "正在输入…" : string.Empty;
        SendButton.Content = busy ? "停止" : "发送";
        SendButton.Background = ColorBrush(busy ? "#E15B64" : "#07C160");
        if (Content is DependencyObject root)
            SetButtonsEnabled(root, !busy);
    }

    private void SetButtonsEnabled(DependencyObject root, bool enabled)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button
                && !ReferenceEquals(button, SendButton)
                && !ReferenceEquals(button, CloseButton))
                button.IsEnabled = enabled;
            SetButtonsEnabled(child, enabled);
        }
    }

    private void AppendTimeIfNeeded(bool force = false)
    {
        var now = DateTime.Now;
        if (!force && (now - _lastTimeStampShown).TotalMinutes < 4)
            return;
        _lastTimeStampShown = now;
        ChatPanel.Children.Add(new TextBlock
        {
            Text = now.ToString("HH:mm"),
            FontSize = 11,
            Foreground = ColorBrush("#B2B2B2"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 10),
        });
    }

    private void AppendSystemTip(string text)
    {
        ChatPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = ColorBrush("#B2B2B2"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(20, 4, 20, 8),
            TextAlignment = TextAlignment.Center,
        });
        ScrollToEnd();
    }

    private void AppendBubble(
        string speaker,
        string text,
        bool isPet,
        string? meta = null,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        // 微信布局：头像 + 气泡，对方左 / 自己右
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 头像：小申立绘 / 用户「我」可爱头像（不再显示单字「小」「宝」）
        var avatar = CreateAvatar(isPet ? _settings.PetName : "我", isPet);
        var bubble = CreateTextBubble(text, isPet);

        if (isPet)
        {
            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(bubble, 1);
            avatar.Margin = new Thickness(0, 0, 8, 0);
            bubble.HorizontalAlignment = HorizontalAlignment.Left;
            bubble.MaxWidth = Math.Max(180, ActualWidth > 0 ? ActualWidth * 0.68 : 280);
        }
        else
        {
            Grid.SetColumn(avatar, 2);
            Grid.SetColumn(bubble, 1);
            avatar.Margin = new Thickness(8, 0, 0, 0);
            bubble.HorizontalAlignment = HorizontalAlignment.Right;
            bubble.MaxWidth = Math.Max(180, ActualWidth > 0 ? ActualWidth * 0.68 : 280);
        }

        row.Children.Add(avatar);
        row.Children.Add(bubble);
        ChatPanel.Children.Add(row);
        ScrollToEnd();
    }

    private void AppendImageBubble(bool isPet, byte[] imageBytes, string fileName)
    {
        try
        {
            var bitmap = LoadBitmap(imageBytes);
            if (bitmap is null)
                return;

            var row = new Grid { Margin = new Thickness(0, -4, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 占位对齐头像宽度（与圆形立绘头像一致）
            var spacer = new Border { Width = 42, Height = 1 };
            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 220,
                MaxHeight = 220,
                Cursor = Cursors.Hand,
                ToolTip = fileName,
            };
            // 点击放大：简单用新窗口看完整图
            image.MouseLeftButtonUp += (_, _) => ShowImagePreview(bitmap, fileName);

            var frame = new Border
            {
                Child = image,
                CornerRadius = new CornerRadius(4),
                BorderBrush = ColorBrush("#D9D9D9"),
                BorderThickness = new Thickness(1),
                Background = ColorBrush("#FFFFFF"),
                Padding = new Thickness(2),
            };

            if (isPet)
            {
                Grid.SetColumn(spacer, 0);
                Grid.SetColumn(frame, 1);
                spacer.Margin = new Thickness(0, 0, 8, 0);
                frame.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetColumn(spacer, 2);
                Grid.SetColumn(frame, 1);
                spacer.Margin = new Thickness(8, 0, 0, 0);
                frame.HorizontalAlignment = HorizontalAlignment.Right;
            }

            row.Children.Add(spacer);
            row.Children.Add(frame);
            ChatPanel.Children.Add(row);
            ScrollToEnd();
        }
        catch
        {
            // 预览失败不阻断发送
        }
    }

    private static void ShowImagePreview(BitmapSource source, string title)
    {
        var win = new Window
        {
            Title = title,
            Width = Math.Min(900, SystemParameters.WorkArea.Width * 0.8),
            Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.85),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = ColorBrush("#111111"),
            Content = new ScrollViewer
            {
                Content = new System.Windows.Controls.Image
                {
                    Source = source,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(8),
                },
            },
        };
        win.Show();
    }

    private Border CreateAvatar(string name, bool isPet)
    {
        // 圆形立绘头像（不再用「小」「宝」单字）
        var img = new System.Windows.Controls.Image
        {
            Source = LoadChatAvatar(isPet),
            Stretch = Stretch.UniformToFill,
            Width = 42,
            Height = 42,
        };

        var clip = new EllipseGeometry(new System.Windows.Point(21, 21), 21, 21);
        // 必须在布局后更新，避免裁剪错位
        img.Loaded += (_, _) =>
        {
            img.Clip = new EllipseGeometry(
                new System.Windows.Point(img.ActualWidth / 2, img.ActualHeight / 2),
                img.ActualWidth / 2,
                img.ActualHeight / 2);
        };
        img.Clip = clip;

        return new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = isPet ? ColorBrush("#FFE8F0") : ColorBrush("#E8FFE8"),
            BorderBrush = isPet ? ColorBrush("#FFB6C8") : ColorBrush("#07C160"),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = img,
            ToolTip = isPet
                ? (string.IsNullOrWhiteSpace(name) ? "小申" : name)
                : "我",
        };
    }

    private static readonly Dictionary<bool, BitmapImage> AvatarCache = new();

    private static BitmapImage LoadChatAvatar(bool isPet)
    {
        if (AvatarCache.TryGetValue(isPet, out var cached))
            return cached;

        try
        {
            var file = isPet ? "pet_avatar.png" : "user_avatar.png";
            var uri = new Uri(
                $"pack://application:,,,/BunnyCompanion;component/Assets/Avatars/{file}",
                UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128;
            bmp.EndInit();
            bmp.Freeze();
            AvatarCache[isPet] = bmp;
            return bmp;
        }
        catch
        {
            // 资源缺失时不要让整窗聊天崩溃：生成纯色占位图
            var pixels = new byte[4];
            if (isPet)
            {
                pixels[0] = 0xF0; // B
                pixels[1] = 0xE8; // G
                pixels[2] = 0xFF; // R
                pixels[3] = 0xFF;
            }
            else
            {
                pixels[0] = 0x60;
                pixels[1] = 0xC1;
                pixels[2] = 0x07;
                pixels[3] = 0xFF;
            }

            var placeholder = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(placeholder));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            AvatarCache[isPet] = bmp;
            return bmp;
        }
    }

    private Border CreateTextBubble(string text, bool isPet)
    {
        // 微信：对方白底，自己绿底；正文完整显示，可选中复制
        var body = new TextBox
        {
            Text = text ?? string.Empty,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsTabStop = false,
            FontSize = 14.5,
            Foreground = ColorBrush("#191919"),
            Padding = new Thickness(0),
            // 取消默认焦点虚线，更像聊天气泡
            Focusable = true,
            Cursor = Cursors.IBeam,
            // 不限制高度，长文完整展开；外层 ScrollViewer 负责滚动
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        return new Border
        {
            Background = isPet ? ColorBrush("#FFFFFF") : ColorBrush("#95EC69"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            // 轻微阴影感用边框模拟
            BorderBrush = isPet ? ColorBrush("#E5E5E5") : ColorBrush("#8DE06A"),
            BorderThickness = new Thickness(isPet ? 1 : 0),
            Child = body,
            // 限制最大宽度由外部设置
        };
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ChatAttachmentKind ClassifyAttachment(string ext) => ext switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => ChatAttachmentKind.Image,
        ".txt" or ".md" or ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx"
            or ".json" or ".xml" or ".csv" or ".log" or ".html" or ".htm" or ".css"
            or ".sql" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".hpp"
            or ".yaml" or ".yml" or ".ini" or ".conf" or ".sh" or ".bat" or ".ps1"
            or ".r" or ".m" or ".swift" or ".kt" or ".dart" or ".vue" or ".php"
            or ".rb" or ".pl" or ".lua" or ".toml" => ChatAttachmentKind.Text,
        _ => ChatAttachmentKind.Other,
    };

    private static string GuessMime(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "text/plain",
    };

    private static string ReadTextFileLimited(string path, int maxChars)
    {
        // 尝试 UTF-8，失败则默认编码
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch
        {
            raw = File.ReadAllText(path, System.Text.Encoding.Default);
        }

        raw = raw.Replace("\r\n", "\n");
        if (raw.Length <= maxChars)
            return raw;
        return raw[..maxChars] + "\n…（文件较长，已截取前部供阅读）";
    }

    private static byte[]? TryDownscaleImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var scale = Math.Min(1.0, 1280.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource source = frame;
            if (scale < 0.99)
            {
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                source.Freeze();
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadBitmap(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static SolidColorBrush ColorBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    // ---------- 语音输入 / TTS 朗读 ----------

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (!_settings.VoiceInputEnabled)
        {
            AppendSystemTip("语音输入未开启：设置 → 勾选「语音输入」。");
            return;
        }
        if (!VoiceService.IsRecognitionAvailable)
        {
            AppendSystemTip("未检测到 Windows 语音组件。请确认：麦克风可用、系统已安装中文语音识别，然后再试。");
            return;
        }

        // 在后台线程识别，避免阻塞 UI
        var oldContent = VoiceButton.Content;
        VoiceButton.Content = "…";
        VoiceButton.IsEnabled = false;
        StatusText.Text = "正在听…请对着麦克风说中文";
        AppendSystemTip("🎤 正在听你说（约 8 秒）…说完稍等一下。");
        _ = Task.Run(() =>
        {
            string text = string.Empty;
            try
            {
                text = VoiceService.RecognizeOnce(9000);
            }
            catch
            {
                text = string.Empty;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (!IsLoaded)
                        return;
                    VoiceButton.Content = oldContent;
                    VoiceButton.IsEnabled = !_busy;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        StatusText.Text = "没听清，再说一次或直接打字～";
                        AppendSystemTip("没听清～请靠近麦克风、说清楚一点，或改用打字。也可在 Windows 设置里安装「中文语音识别」。");
                        return;
                    }
                    InputBox.Text = text;
                    InputBox.Focus();
                    InputBox.CaretIndex = text.Length;
                    StatusText.Text = "已识别，正在发送…";
                    // 识别成功后自动发送，减少一步点击
                    _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);
                });
            }
            catch
            {
                // 窗口已关或调度失败：忽略
            }
        });
    }

    /// <summary>聊天窗声音开、且非安静时段时朗读回复。</summary>
    private void TrySpeakReply(string text)
    {
        try
        {
            // 右上角 🔇 或全局关闭时不读
            if (!_chatSoundOn || !_settings.TtsEnabled || string.IsNullOrWhiteSpace(text))
                return;
            // 安静时段不朗读
            if (IsQuietNow())
                return;
            // 长文截断：最多念前 280 字，避免念太久
            var spoken = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            // 去掉 markdown / 多余空白
            spoken = System.Text.RegularExpressions.Regex.Replace(spoken, @"[#*`>\|\[\]\(\)]", " ");
            spoken = System.Text.RegularExpressions.Regex.Replace(spoken, @"\s+", " ");
            if (spoken.Length > 280)
                spoken = spoken[..280] + "……";
            if (string.IsNullOrWhiteSpace(spoken))
                return;
            VoiceService.Speak(spoken);
        }
        catch
        {
            // TTS 失败不阻断
        }
    }

    private bool IsQuietNow()
    {
        if (_settings.QuietMode)
            return true;
        if (!TimeOnly.TryParse(_settings.QuietStart, out var start)
            || !TimeOnly.TryParse(_settings.QuietEnd, out var end))
            return false;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return start <= end ? now >= start && now < end : now >= start || now < end;
    }
}
