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

namespace BunnyCompanion;

public partial class ChatWindow : Window
{
    private readonly PetSettings _settings;
    private readonly AiAgentService _agent;
    private readonly Window? _hostWindow;
    private readonly Action<AgentResult> _onPetReply;
    private readonly Action _onUserSpoke;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private readonly List<PendingAttachment> _pending = [];
    private DateTime _lastTimeStampShown = DateTime.MinValue;

    private sealed class PendingAttachment
    {
        public required string Path { get; init; }
        public required string FileName { get; init; }
        public required ChatAttachmentKind Kind { get; init; }
        public string? MimeType { get; init; }
        public string? TextPreview { get; set; }
        public byte[]? ImageBytes { get; set; }
        public long SizeBytes { get; init; }
    }

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
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        AppendTimeIfNeeded(force: true);
        AppendBubble(
            _settings.PetName,
            $"嗨，{_settings.PartnerName}～\n像微信一样随便聊就行。可以发图片、代码文件，也可以点「看桌面」让我瞅一眼屏幕。\n断网也没关系，我会继续陪你。",
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

            // 约 1/3 宽、3/4 高，限制在合理区间
            Width = Math.Clamp(area.Width * 0.34, 360, 560);
            Height = Math.Clamp(area.Height * 0.78, 520, 860);
            MinWidth = Math.Min(320, area.Width * 0.4);
            MinHeight = Math.Min(420, area.Height * 0.5);
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
                var area = SystemParameters.WorkArea;
                if (Math.Abs(Height - area.Height) < 8)
                    FitToScreen();
                else
                {
                    Height = area.Height;
                    Top = area.Top;
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

    private void SendButton_Click(object sender, RoutedEventArgs e) =>
        _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);

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

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
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

        foreach (var path in dialog.FileNames)
            TryAddAttachment(path);

        RefreshAttachBar();
    }

    private void TryAddAttachment(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            if (_pending.Count >= 6)
            {
                StatusText.Text = "一次最多 6 个附件";
                return;
            }

            var info = new FileInfo(path);
            // 单文件上限 8MB，避免撑爆 API
            if (info.Length > 8 * 1024 * 1024)
            {
                AppendSystemTip($"「{info.Name}」超过 8MB，已跳过。");
                return;
            }

            var ext = info.Extension.ToLowerInvariant();
            var kind = ClassifyAttachment(ext);
            var item = new PendingAttachment
            {
                Path = path,
                FileName = info.Name,
                Kind = kind,
                MimeType = GuessMime(ext),
                SizeBytes = info.Length,
            };

            if (kind == ChatAttachmentKind.Image)
            {
                item.ImageBytes = File.ReadAllBytes(path);
                // 过大图压缩到 JPEG 再发（视觉接口更稳）
                if (item.ImageBytes.Length > 1_200_000)
                    item.ImageBytes = TryDownscaleImage(item.ImageBytes) ?? item.ImageBytes;
            }
            else if (kind == ChatAttachmentKind.Text)
            {
                // 文本最多读 80KB，保证完整可读又不爆上下文
                item.TextPreview = ReadTextFileLimited(path, maxChars: 80_000);
            }
            else
            {
                AppendSystemTip($"「{info.Name}」类型暂不支持解析，可改发图片或文本/代码。");
                return;
            }

            _pending.Add(item);
        }
        catch
        {
            AppendSystemTip("读取文件失败，请换一个文件试试。");
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
            var label = item.Kind == ChatAttachmentKind.Image
                ? $"🖼 {item.FileName} · {sizeKb}KB"
                : $"📄 {item.FileName} · {sizeKb}KB";
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
        // Enter 发送；Shift+Enter 换行（更接近微信习惯）
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync(includeDesktop: DesktopCheck.IsChecked == true);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
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
        if (_busy)
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
            AppendBubble(_settings.PartnerName, display, isPet: false, attachments: attachments);
            // 图片气泡额外展示缩略图
            foreach (var a in attachments.Where(x => x.Kind == ChatAttachmentKind.Image && x.ImageBytes is { Length: > 0 }))
                AppendImageBubble(isPet: false, a.ImageBytes!, a.FileName);
        }

        _onUserSpoke();
        SetBusy(true, includeDesktop ? "正在看你的桌面…" : hasAttach ? "正在看你发的文件…" : "小申 Agent 思考中…");
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // UI 线程进度：工具执行状态显示在状态栏
        var progress = new Progress<string>(msg =>
        {
            if (!IsLoaded) return;
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
                    _cts.Token)
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
            _onPetReply(result);
        }
        catch (OperationCanceledException)
        {
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
            _onPetReply(new AgentResult(offline.Text, offline.ActionKey, offline.AffectionGain, "本地", false));
        }
        finally
        {
            if (IsLoaded)
            {
                SetBusy(false, "在线");
                InputBox.Focus();
            }
        }
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
                ImageBytes: p.ImageBytes));
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
        SendButton.IsEnabled = !busy;
        InputBox.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;
        StatusText.Text = status;
        TypingHint.Text = busy ? "正在输入…" : string.Empty;
        SendButton.Content = busy ? "…" : "发送";
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

        var avatar = CreateAvatar(isPet ? _settings.PetName : _settings.PartnerName, isPet);
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

            // 占位对齐头像宽度
            var spacer = new Border { Width = 40, Height = 1 };
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
        var initial = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[0].ToString();
        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            Background = isPet ? ColorBrush("#07C160") : ColorBrush("#576B95"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = initial,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
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
}
