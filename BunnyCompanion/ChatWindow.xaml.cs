using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BunnyCompanion.Models;
using BunnyCompanion.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

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
        TitleText.Text = $"和{_settings.PetName}聊聊";
        Title = $"和{_settings.PetName}聊聊 · Agent";
        Loaded += (_, _) =>
        {
            AppendBubble(
                _settings.PetName,
                $"嗨，{_settings.PartnerName}～我可以陪你聊天、看桌面、写代码、写文章。点「看桌面」我就帮你瞅一眼屏幕。断网也会用中文本地陪伴。",
                isPet: true,
                meta: "Agent 已就绪");
            InputBox.Focus();
        };
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
        };
    }

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
        AppendBubble(_settings.PetName, "对话已清空，我们重新开始吧。", isPet: true, meta: "本地");
        StatusText.Text = "对话已重置 · 多模态 Agent 待命中";
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
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
        if (text.Length == 0 && !includeDesktop)
            return;

        InputBox.Clear();
        if (IsLoaded)
            AppendBubble(_settings.PartnerName, text.Length == 0 ? "（请看桌面）" : text, isPet: false);
        _onUserSpoke();

        SetBusy(true, includeDesktop ? "正在截取桌面并思考…" : "小申思考中…");
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _agent.ChatAsync(text, _settings, includeDesktop, _hostWindow, _cts.Token)
                .ConfigureAwait(true);
            if (!IsLoaded)
                return;
            AppendBubble(
                _settings.PetName,
                result.Text,
                isPet: true,
                meta: $"{result.Provider}{(result.UsedDesktopImage ? " · 已看桌面" : string.Empty)}");
            StatusText.Text = $"上次回复来自：{result.Provider}" +
                              (result.UsedDesktopImage ? "（含桌面截图）" : string.Empty);
            _onPetReply(result);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                AppendBubble(_settings.PetName, "好，这次先不说了。", isPet: true, meta: "已取消");
        }
        catch (Exception)
        {
            var offline = ChatReplyService.Reply(text, _settings, offlineMode: true, desktopRequested: includeDesktop);
            if (IsLoaded)
            {
                AppendBubble(_settings.PetName, offline.Text + "\n（网络异常，已切换本地中文陪伴）", isPet: true, meta: "本地兜底");
                StatusText.Text = "网络异常 · 已自动切换本地模式";
            }
            _onPetReply(new AgentResult(offline.Text, offline.ActionKey, offline.AffectionGain, "本地", false));
        }
        finally
        {
            if (IsLoaded)
            {
                SetBusy(false, "多模态 Agent · 阶跃主链 · Groq/本地自动兜底 · 强制中文");
                InputBox.Focus();
            }
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        InputBox.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        StatusText.Text = status;
        SendButton.Content = busy ? "…" : "发送";
        // 禁用快捷芯片与操作按钮，避免并发发送。
        foreach (var child in LogicalTreeHelper.GetChildren(this))
        {
            // 仅遍历一层不够；直接用 FindName 列表
        }
        SetNamedEnabled("LookDesktopBtn", !busy);
        // chips are anonymous — walk visual tree of wrap panels via Content
        if (Content is DependencyObject root)
            SetButtonsEnabled(root, !busy, exceptSendWhenBusy: busy);
    }

    private void SetNamedEnabled(string name, bool enabled)
    {
        if (FindName(name) is UIElement element)
            element.IsEnabled = enabled;
    }

    private void SetButtonsEnabled(DependencyObject root, bool enabled, bool exceptSendWhenBusy)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && !ReferenceEquals(button, SendButton))
                button.IsEnabled = enabled;
            SetButtonsEnabled(child, enabled, exceptSendWhenBusy);
        }
    }

    private void AppendBubble(string speaker, string text, bool isPet, string? meta = null)
    {
        var row = new Border
        {
            Background = isPet ? ColorBrush("#FFF5F8") : ColorBrush("#F3F8FF"),
            BorderBrush = isPet ? ColorBrush("#F7D7E3") : ColorBrush("#D7E4F7"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(isPet ? 0 : 24, 0, isPet ? 24 : 0, 8),
            HorizontalAlignment = isPet ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };

        var panel = new StackPanel();
        var header = new DockPanel();
        header.Children.Add(new TextBlock
        {
            Text = speaker,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = isPet ? ColorBrush("#B65076") : ColorBrush("#4E6FA8"),
        });
        if (!string.IsNullOrWhiteSpace(meta))
        {
            var metaBlock = new TextBlock
            {
                Text = meta,
                FontSize = 10.5,
                Foreground = ColorBrush("#A08A94"),
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            DockPanel.SetDock(metaBlock, Dock.Right);
            header.Children.Add(metaBlock);
        }

        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = ColorBrush("#513A45"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        row.Child = panel;
        ChatPanel.Children.Add(row);

        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static SolidColorBrush ColorBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
