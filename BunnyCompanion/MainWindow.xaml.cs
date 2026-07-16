using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using BunnyCompanion.Engine;
using BunnyCompanion.Models;
using BunnyCompanion.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace BunnyCompanion;

public partial class MainWindow : Window
{
    private const double BaseWidth = 320;
    private const double BaseHeight = 450;

    private readonly SettingsService _settingsService;
    private readonly string[] _arguments;
    private readonly AiAgentService _agent = new();
    private HotkeyService? _hotkeys;
    private readonly Dictionary<string, BitmapImage> _spriteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _alphaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _actionTimer = new();
    private readonly DispatcherTimer _movementTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _behaviorTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly DispatcherTimer _bubbleTimer = new();
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>闲置状态每 15 秒轻量检查；CPU/内存/电池仍限制为 2 分钟采样。</summary>
    private readonly DispatcherTimer _systemTriggerTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    /// <summary>定期自愈：穿透残留 / 拖拽卡死 / 透明度动画卡住导致点不上。</summary>
    private readonly DispatcherTimer _inputHealTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private PetSettings _settings;
    private PetActionDefinition _currentAction = PetActionCatalog.Get("idle");
    private string _currentActionKey = "idle";
    private int _frameIndex;
    private Action? _actionCompleted;
    private bool _isWalking;
    private int _walkDirection = 1;
    private DateTime _walkUntil;
    private bool _isDragging;
    private bool _dragMoved;
    private int _mouseDownClickCount;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;
    private double _dragDpiScaleX = 1;
    private double _dragDpiScaleY = 1;
    private bool _isExiting;
    private bool _isUninstalling;
    private bool _initialized;
    private bool _introDone;
    private bool _introPlaying;
    private string _currentSpriteName = "idle";
    private HwndSource? _windowSource;
    private bool _manuallyHidden;
    private bool _hiddenForFullscreen;
    private DateTime _lastWaterReminder = DateTime.Now;
    private DateTime _lastRestReminder = DateTime.Now;
    private DateOnly? _lastSpecialDate;
    private DateTime? _focusEnd;
    private DateTime _exclusiveUntil = DateTime.MinValue;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _showMenuItem;
    private Forms.ToolStripMenuItem? _autoWalkMenuItem;
    private Forms.ToolStripMenuItem? _topmostMenuItem;
    private Forms.ToolStripMenuItem? _startupMenuItem;
    private Forms.ToolStripMenuItem? _quietMenuItem;
    private Forms.ToolStripMenuItem? _clickThroughMenuItem;
    private Forms.ToolStripMenuItem? _fullscreenMenuItem;
    private Forms.ToolStripMenuItem? _focusMenuItem;
    private string? _hotkeyWarning;
    private ChatWindow? _chatWindow;
    private int _rapidClickCount;
    private DateTime _lastRapidClickAt = DateTime.MinValue;
    /// <summary>拖拽开始时间，超时仍未松手则强制结束，避免永久捕获鼠标。</summary>
    private DateTime _dragStartedAt = DateTime.MinValue;
    private double _dragLastDeltaX;
    private double _dragLastDeltaY;
    private string? _lastMouseReactionAction;
    private DateTime _lastPersonBubbleAt = DateTime.MinValue;
    private DateOnly? _lastWeatherBubbleDay;
    private DateTime _lastMemoCheck = DateTime.MinValue;
    /// <summary>系统触发器节流：记录上次触发时间，避免同类提醒刷屏。</summary>
    private DateTime _lastSystemTriggerAt = DateTime.MinValue;
    /// <summary>记录用户是否已长时间离开；欢迎提醒只在重新操作电脑后显示。</summary>
    private bool _wasUserAway;
    /// <summary>上次重型资源采样时间，避免频繁启动性能计数器和 PowerShell。</summary>
    private DateTime _lastSystemResourceSampleAt = DateTime.MinValue;

    private bool IsFocusActive => _focusEnd is { } end && end > DateTime.Now;
    private bool IsExclusiveBusy => DateTime.Now < _exclusiveUntil || _isDragging || _introPlaying;
    private bool IsPetVisibleActive =>
        !_isExiting && !_manuallyHidden && !_hiddenForFullscreen && IsVisible && Opacity > 0.05;

    public MainWindow(SettingsService settingsService, PetSettings settings, string[] arguments)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settings;
        _arguments = arguments;

        _actionTimer.Tick += ActionTimer_Tick;
        _movementTimer.Tick += MovementTimer_Tick;
        _behaviorTimer.Tick += BehaviorTimer_Tick;
        _bubbleTimer.Tick += BubbleTimer_Tick;
        _reminderTimer.Tick += ReminderTimer_Tick;
        _fullscreenTimer.Tick += FullscreenTimer_Tick;
        _focusTimer.Tick += FocusTimer_Tick;
        _inputHealTimer.Tick += InputHealTimer_Tick;
        _systemTriggerTimer.Tick += SystemTriggerTimer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;
        _initialized = true;

        ApplySettings(initial: true);
        RestoreOrPlaceWindow();
        CreateTrayIcon();
        AttachHotkeys();
        PlayAction("idle");

        // 全屏检测始终运行；行为/提醒在 intro 结束后再启动，避免隐藏动画空转。
        _fullscreenTimer.Start();
        _inputHealTimer.Start();

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            ApplyClickThroughState(force: true);
            var startedWithWindows = _arguments.Any(argument =>
                argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            var quietStartup = startedWithWindows || IsQuietNow();
            // intro 动画若异常未回调，仍强制启动行为定时器，避免永不散步
            var introWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            introWatchdog.Tick += (_, _) =>
            {
                introWatchdog.Stop();
                if (!_introDone)
                {
                    _introPlaying = false;
                    _introDone = true;
                    Opacity = 1;
                }
                EnsureBehaviorTimersRunning();
            };
            introWatchdog.Start();

            PlayStartupAnimation(fancy: !quietStartup, () =>
            {
                introWatchdog.Stop();
                EnsureBehaviorTimersRunning();
                if (!_settings.HasCompletedFirstRun)
                {
                    _settings.HasCompletedFirstRun = true;
                    SaveSettings();
                    ShowMessage(_settings.StartWithWindows
                        ? "以后我会陪你一起开机。右键点我或托盘图标，可以打开全部功能。"
                        : "右键点我或托盘图标，可以打开全部功能。开机启动也能在设置中重新开启。", 6);
                    PlayAction("wave", exclusiveSeconds: 2.5);
                }
                else if (!startedWithWindows || !IsQuietNow())
                {
                    ShowMessage(GetTimeGreeting(), 4.5);
                    PlayAction("wave", exclusiveSeconds: 1.8);
                }
                CheckSpecialDate(force: true);
                // 启动后延迟检查更新，不挡 intro
                ScheduleAutoUpdateCheck();
            });
        }));
    }

    /// <summary>启动约 12 秒后静默检查 GitHub 更新（需开启 AutoCheckUpdate）。</summary>
    private void ScheduleAutoUpdateCheck()
    {
        if (!_settings.AutoCheckUpdate)
            return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            if (_isExiting)
                return;
            try
            {
                await CheckForUpdatesAsync(interactive: false).ConfigureAwait(true);
            }
            catch
            {
                // 静默失败
            }
        };
        t.Start();
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_isExiting)
            return;

        if (interactive)
            ShowMessage("正在检查更新…", 2.5);

        var check = await AppUpdateService.CheckAsync(
            minInterval: interactive ? TimeSpan.Zero : TimeSpan.FromHours(6),
            force: interactive).ConfigureAwait(true);

        if (_isExiting || !IsLoaded)
            return;

        if (!check.Success)
        {
            if (interactive)
                ShowMessage(check.Message, 5);
            return;
        }

        if (!check.UpdateAvailable)
        {
            if (interactive)
                ShowMessage(check.Message, 3.5);
            return;
        }

        var remote = check.RemoteVersion is null
            ? check.TagName ?? "?"
            : AppUpdateService.FormatVersion(check.RemoteVersion);
        var ask = MessageBox.Show(
            this,
            $"{check.Message}\n\n" +
            $"来源：GitHub {AppUpdateService.Owner}/{AppUpdateService.Repo}\n" +
            $"文件：{check.TargetFileName}\n" +
            $"SHA256：{check.ExpectedSha256}\n\n" +
            "将后台下载并用官方 checksums.txt 校验哈希；\n" +
            "不一致则拒绝安装。通过后会重启完成替换。\n\n" +
            "现在更新吗？",
            $"发现新版本 {remote}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (ask != MessageBoxResult.Yes)
            return;

        ShowMessage("正在下载并校验更新…", 4);
        var progress = new Progress<string>(msg =>
        {
            if (!_isExiting && IsLoaded)
                ShowMessage(msg, 3);
        });

        var apply = await AppUpdateService.DownloadVerifyAndScheduleReplaceAsync(check, progress)
            .ConfigureAwait(true);

        if (!apply.Success)
        {
            MessageBox.Show(this, apply.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ShowMessage("校验通过，即将重启…", 2);
        await Task.Delay(600).ConfigureAwait(true);
        // 正常退出，让脚本替换 EXE 并拉起新版本
        ExitApplication();
    }

    /// <summary>
    /// 入场动画：透明度 0→1，缩放与上浮。开机静默或安静时段只做快速淡入，不花哨打扰。
    /// </summary>
    private void PlayStartupAnimation(bool fancy, Action? completed)
    {
        if (_introDone)
        {
            Opacity = 1;
            IntroScale.ScaleX = IntroScale.ScaleY = 1;
            IntroOffset.Y = 0;
            completed?.Invoke();
            return;
        }

        _introPlaying = true;
        Opacity = 0;
        IntroScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IntroScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IntroOffset.BeginAnimation(TranslateTransform.YProperty, null);
        IntroScale.ScaleX = fancy ? 0.68 : 0.92;
        IntroScale.ScaleY = fancy ? 0.68 : 0.92;
        IntroOffset.Y = fancy ? 42 : 12;

        var duration = fancy ? TimeSpan.FromMilliseconds(560) : TimeSpan.FromMilliseconds(220);
        var fade = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var scale = new DoubleAnimation(IntroScale.ScaleX, 1, duration)
        {
            EasingFunction = fancy
                ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var rise = new DoubleAnimation(IntroOffset.Y, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        fade.Completed += (_, _) =>
        {
            _introPlaying = false;
            _introDone = true;
            // 清掉 Opacity 动画时钟，避免之后 Opacity=1 写不进去导致“看得见但点不上”
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            IntroScale.ScaleX = IntroScale.ScaleY = 1;
            IntroOffset.Y = 0;
            PetRoot.IsHitTestVisible = true;
            ApplyClickThroughState(force: true);
            if (fancy)
                PlayAction("jump", exclusiveSeconds: 1.2);
            completed?.Invoke();
        };

        BeginAnimation(OpacityProperty, fade);
        IntroScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        IntroScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        IntroOffset.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    private void ApplySettings(bool initial = false)
    {
        _settings.Normalize();
        Topmost = _settings.AlwaysOnTop;
        if (_chatWindow is { IsLoaded: true })
            _chatWindow.Topmost = _settings.AlwaysOnTop;

        var oldBottom = Top + Height;
        var oldCenter = Left + Width / 2;
        Width = BaseWidth * _settings.Scale;
        Height = BaseHeight * _settings.Scale;
        if (!initial && IsLoaded)
        {
            Left = oldCenter - Width / 2;
            Top = oldBottom - Height;
            ScreenService.ClampToWorkingArea(this);
        }

        if (IsLoaded)
            ApplyClickThroughState(force: true);
        UpdateTrayChecks();
    }

    /// <summary>
    /// 统一设置穿透：同时写 Win32 WS_EX_TRANSPARENT 与内部标志，避免只改设置未改 HWND。
    /// </summary>
    private void ApplyClickThroughState(bool force = false)
    {
        if (!IsLoaded || _isExiting)
            return;
        NativeWindowService.SetClickThrough(this, _settings.ClickThrough);
        // 非穿透时强制去掉可能残留的透明扩展样式
        if (!_settings.ClickThrough)
            NativeWindowService.EnsureClickThroughOff(this);
    }

    private void RestoreOrPlaceWindow()
    {
        if (_settings.LastLeft is { } left && _settings.LastTop is { } top
            && double.IsFinite(left) && double.IsFinite(top))
        {
            Left = left;
            Top = top;
            ScreenService.ClampToWorkingArea(this);
            return;
        }

        ScreenService.PlaceBottomRight(this);
    }

    private void PlayAction(string key, Action? completed = null, double exclusiveSeconds = 0)
    {
        if (_isWalking && !key.Equals("walk", StringComparison.OrdinalIgnoreCase))
            StopWalking(recover: false);

        _currentActionKey = key;
        _currentAction = PetActionCatalog.Get(key);
        _actionCompleted = completed;
        _frameIndex = 0;
        _actionTimer.Stop();

        // 自动互斥：非循环待机类动作按帧时长独占，避免走路/喝水/比心互相打断。
        var autoExclusive = 0.0;
        if (!_currentAction.Loop
            && !key.Equals("idle", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("walk", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("focus", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("dragged", StringComparison.OrdinalIgnoreCase))
        {
            autoExclusive = _currentAction.Frames.Sum(f => f.DurationMilliseconds) / 1000.0 + 0.2;
        }
        var exclusive = Math.Max(exclusiveSeconds, autoExclusive);
        if (exclusive > 0)
            _exclusiveUntil = DateTime.Now.AddSeconds(exclusive);

        DisplayCurrentFrame();
        if (IsPetVisibleActive)
            _actionTimer.Start();
    }

    private void DisplayCurrentFrame()
    {
        var frame = _currentAction.Frames[_frameIndex];
        _currentSpriteName = frame.Sprite;
        PetImage.Source = LoadSprite(frame.Sprite);
        _actionTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(60, frame.DurationMilliseconds));
        if (IsPetVisibleActive)
            AnimateSprite(frame.Sprite);
        else
            StopSpriteAnimations();
    }

    private void StopSpriteAnimations()
    {
        MotionTransform.BeginAnimation(TranslateTransform.YProperty, null);
        TiltTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        MotionTransform.Y = 0;
        TiltTransform.Angle = 0;
    }

    private BitmapImage LoadSprite(string name)
    {
        if (_spriteCache.TryGetValue(name, out var cached))
            return cached;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(
            $"pack://application:,,,/BunnyCompanion;component/Assets/Sprites/{name}.png",
            UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        _spriteCache[name] = bitmap;
        return bitmap;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HtTransparent = -1;
        if (message != WmNcHitTest)
            return IntPtr.Zero;

        // 拖拽中始终接收命中，避免松手前被当成穿透
        if (_isDragging)
            return IntPtr.Zero;

        if (_settings.ClickThrough || _hiddenForFullscreen || _manuallyHidden || !IsVisible)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        // 入场/全屏恢复后 Opacity 可能仍被动画时钟卡住读到极小值：用实际可见状态判断
        if (!PetRoot.IsHitTestVisible || (Opacity < 0.05 && !_introPlaying))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        var packed = lParam.ToInt64();
        var screenX = unchecked((short)(packed & 0xFFFF));
        var screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        Point windowPoint;
        try
        {
            windowPoint = PointFromScreen(new Point(screenX, screenY));
        }
        catch
        {
            // 多屏 DPI 偶发失败时不要整窗穿透，交给默认命中
            return IntPtr.Zero;
        }

        if (IsOpaqueSpritePoint(windowPoint) || IsSoftBodyHit(windowPoint))
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool IsOpaqueSpritePoint(Point windowPoint)
    {
        if (!IsLoaded || PetImage.Source is not BitmapSource source
                      || PetImage.ActualWidth <= 0 || PetImage.ActualHeight <= 0)
            return false;

        Point imagePoint;
        try
        {
            // TransformToVisual 会带上翻转、跳跃位移与旋转，比 TranslatePoint 更稳。
            var transform = TransformToVisual(PetImage);
            imagePoint = transform.Transform(windowPoint);
        }
        catch
        {
            // 变换失败时用 Image 布局矩形兜底，避免整只宠点不中
            try
            {
                imagePoint = PetImage.TranslatePoint(new Point(0, 0), this);
                imagePoint = new Point(windowPoint.X - imagePoint.X, windowPoint.Y - imagePoint.Y);
            }
            catch
            {
                return false;
            }
        }

        var localX = imagePoint.X;
        var localY = imagePoint.Y;
        if (localX < -4 || localY < -4
            || localX > PetImage.ActualWidth + 4
            || localY > PetImage.ActualHeight + 4)
            return false;

        var scale = Math.Min(PetImage.ActualWidth / source.PixelWidth,
            PetImage.ActualHeight / source.PixelHeight);
        if (!double.IsFinite(scale) || scale <= 0)
            return false;

        var renderedWidth = source.PixelWidth * scale;
        var renderedHeight = source.PixelHeight * scale;
        var offsetX = (PetImage.ActualWidth - renderedWidth) / 2;
        var offsetY = (PetImage.ActualHeight - renderedHeight) / 2;
        var pixelX = (int)Math.Round((localX - offsetX) / scale);
        var pixelY = (int)Math.Round((localY - offsetY) / scale);
        if (pixelX < 0 || pixelY < 0 || pixelX >= source.PixelWidth || pixelY >= source.PixelHeight)
            return false;

        var alpha = GetSpriteAlpha(_currentSpriteName, source);
        // 阈值放宽 + 更大邻域，动作帧半透明边缘也能点中
        return SampleAlpha(alpha, source.PixelWidth, source.PixelHeight, pixelX, pixelY) >= 12;
    }

    /// <summary>
    /// 角色躯干软命中区：像素 alpha 偶发失败时（翻转/跳跃/抗锯齿），中间主体仍可点。
    /// </summary>
    private bool IsSoftBodyHit(Point windowPoint)
    {
        if (!IsLoaded || PetImage.ActualWidth <= 0 || PetImage.ActualHeight <= 0)
            return false;
        try
        {
            var topLeft = PetImage.TransformToAncestor(this).Transform(new Point(0, 0));
            var w = PetImage.ActualWidth;
            var h = PetImage.ActualHeight;
            // 中间约 62% 宽、上方 12%～下方 92%：盖住身体，避开大片透明边
            var left = topLeft.X + w * 0.19;
            var right = topLeft.X + w * 0.81;
            var top = topLeft.Y + h * 0.12;
            var bottom = topLeft.Y + h * 0.92;
            return windowPoint.X >= left && windowPoint.X <= right
                   && windowPoint.Y >= top && windowPoint.Y <= bottom;
        }
        catch
        {
            return false;
        }
    }

    private static byte SampleAlpha(byte[] alpha, int width, int height, int x, int y)
    {
        byte best = alpha[y * width + x];
        if (best >= 12)
            return best;
        // 3px 邻域，抗锯齿/缩放采样更稳
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    continue;
                best = Math.Max(best, alpha[ny * width + nx]);
            }
        return best;
    }

    /// <summary>
    /// 输入自愈：修复「用着用着突然点不上」——穿透残留、拖拽捕获死锁、Opacity/HitTest 卡住。
    /// </summary>
    private void InputHealTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExiting || !IsLoaded || _manuallyHidden || !IsVisible)
            return;

        // 1) 拖拽超时或左键已松开但仍标记 dragging → 强制结束
        if (_isDragging)
        {
            var leftDown = System.Windows.Input.Mouse.LeftButton == MouseButtonState.Pressed;
            var tooLong = _dragStartedAt != DateTime.MinValue
                          && DateTime.Now - _dragStartedAt > TimeSpan.FromSeconds(12);
            if (!leftDown || tooLong)
                EndDrag(interrupted: true);
        }

        // 2) 非穿透模式确保 Win32 样式没有 WS_EX_TRANSPARENT 残留
        if (!_settings.ClickThrough && !_hiddenForFullscreen)
            NativeWindowService.EnsureClickThroughOff(this);

        // 3) 可见状态下 HitTest / Opacity 不应被卡住
        if (!_hiddenForFullscreen && _introDone)
        {
            if (!PetRoot.IsHitTestVisible)
                PetRoot.IsHitTestVisible = true;

            // Opacity 被动画或全屏逻辑弄到接近 0 但并非全屏隐藏
            if (Opacity < 0.5)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
            }
        }

        // 4) 始终置顶偶发掉层：定时轻量重申（不抢焦点）
        if (_settings.AlwaysOnTop && !Topmost)
            Topmost = true;
    }

    private byte[] GetSpriteAlpha(string name, BitmapSource source)
    {
        if (_alphaCache.TryGetValue(name, out var cached))
            return cached;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var alpha = new byte[converted.PixelWidth * converted.PixelHeight];
        for (var index = 0; index < alpha.Length; index++)
            alpha[index] = pixels[index * 4 + 3];
        _alphaCache[name] = alpha;
        return alpha;
    }

    private void AnimateSprite(string sprite)
    {
        if (!IsPetVisibleActive)
        {
            StopSpriteAnimations();
            return;
        }

        StopSpriteAnimations();

        if (sprite is "breathe" or "headpat")
        {
            MotionTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -4 * _settings.Scale, TimeSpan.FromMilliseconds(330))
                { AutoReverse = true });
        }
        else if (sprite == "jump")
        {
            MotionTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -13 * _settings.Scale, TimeSpan.FromMilliseconds(230))
                { AutoReverse = true, EasingFunction = new SineEase() });
        }
        else if (sprite is "dance" or "music")
        {
            TiltTransform.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(240))
                { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        }
        else if (sprite == "dragged")
        {
            TiltTransform.Angle = -7 * _walkDirection;
        }
    }

    private void ActionTimer_Tick(object? sender, EventArgs e)
    {
        _frameIndex++;
        if (_frameIndex >= _currentAction.Frames.Count)
        {
            if (_currentAction.Loop)
            {
                _frameIndex = 0;
            }
            else
            {
                _actionTimer.Stop();
                var callback = _actionCompleted;
                _actionCompleted = null;
                if (callback is not null)
                    callback();
                else
                    ReturnToAmbientAction();
                return;
            }
        }
        DisplayCurrentFrame();
    }

    private void ReturnToAmbientAction()
    {
        if (IsFocusActive)
            PlayAction("focus");
        else
            PlayAction("idle");
    }

    private void StartWalking()
    {
        // 专注/拖动/入场/隐藏中不散步；短时 exclusive（比心等）不拦截开步，避免永远走不起来。
        if (_isDragging || !_settings.AutoWalk || IsFocusActive || _introPlaying || !IsPetVisibleActive)
            return;
        if (_isWalking)
            return;

        var area = ScreenService.GetWorkingArea(this);
        var roomLeft = Left - area.Left;
        var roomRight = area.Right - (Left + Width);
        // 优先走向空间更大的一侧；素材默认朝左，见 PetFacing
        _walkDirection = roomRight < Width / 2 ? -1 : roomLeft < Width / 2 ? 1 : Random.Shared.Next(2) == 0 ? -1 : 1;
        FacingTransform.ScaleX = PetFacing.ScaleXForMove(_walkDirection);
        _walkUntil = DateTime.Now.AddSeconds(Random.Shared.Next(4, 9));
        _isWalking = true;
        // 清除短互斥，避免 walk 被后续 ambient 立刻顶掉观感
        if (DateTime.Now < _exclusiveUntil)
            _exclusiveUntil = DateTime.MinValue;
        _movementTimer.Start();
        PlayAction("walk");
    }

    /// <summary>保证行为/提醒定时器在 intro 完成后一定跑起来（全屏恢复、托盘显示也会调用）。</summary>
    private void EnsureBehaviorTimersRunning()
    {
        if (_isExiting || !_introDone)
            return;
        if (!_behaviorTimer.IsEnabled && IsPetVisibleActive)
            _behaviorTimer.Start();
        if (!_reminderTimer.IsEnabled)
            _reminderTimer.Start();
        if (!_systemTriggerTimer.IsEnabled && IsPetVisibleActive)
            _systemTriggerTimer.Start();
    }

    private void StopWalking(bool recover = true)
    {
        if (!_isWalking)
            return;
        _isWalking = false;
        _movementTimer.Stop();
        if (recover)
            PlayAction("recover");
    }

    private void MovementTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isWalking || _isDragging)
            return;

        var area = ScreenService.GetWorkingArea(this);
        Left += _walkDirection * 2.15;
        if (Left <= area.Left)
        {
            Left = area.Left;
            _walkDirection = 1; // 撞左墙 → 往右走
            FacingTransform.ScaleX = PetFacing.ScaleXForMove(_walkDirection);
        }
        else if (Left + Width >= area.Right)
        {
            Left = area.Right - Width;
            _walkDirection = -1; // 撞右墙 → 往左走
            FacingTransform.ScaleX = PetFacing.ScaleXForMove(_walkDirection);
        }

        if (DateTime.Now >= _walkUntil)
        {
            StopWalking();
            SavePosition();
        }
    }

    private void BehaviorTimer_Tick(object? sender, EventArgs e)
    {
        // 6～10 秒一拍，提高自动散步可见性
        _behaviorTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(6, 11));
        if (!IsPetVisibleActive || _isDragging || _isWalking || IsFocusActive || _introPlaying)
            return;
        // 短 exclusive 只挡「花活动作」，不挡散步尝试
        var exclusiveBlocksAmbient = DateTime.Now < _exclusiveUntil;

        if (IsQuietNow())
        {
            if (!exclusiveBlocksAmbient && Random.Shared.NextDouble() < 0.55)
            {
                PlayAction("sleep", exclusiveSeconds: 2);
                if (Random.Shared.NextDouble() < 0.3)
                    ShowMessage("安静陪着你，先眯一会儿……", 3.5);
            }
            return;
        }

        var roll = Random.Shared.NextDouble();
        // 提高散步权重（约 40%），解决「好像不会自动散步」
        if (_settings.AutoWalk && roll < 0.40)
        {
            StartWalking();
            if (_isWalking && Random.Shared.NextDouble() < 0.35)
                ShowMessage("我去旁边走走，马上回来～", 3.0);
            return;
        }

        if (exclusiveBlocksAmbient)
            return;

        if (roll < 0.55)
        {
            var life = new[] { "stretch", "drink", "read", "sit", "kneel" };
            PlayAmbientAction(life[Random.Shared.Next(life.Length)]);
            return;
        }

        string[] actions =
        [
            "wave", "jump", "shy", "curious", "music", "plush",
            "laugh", "flowers", "dance", "clap", "gift", "pout", "heart",
        ];
        PlayAmbientAction(actions[Random.Shared.Next(actions.Length)]);
    }

    /// <summary>
    /// 播放自动动作，并按概率弹出对应气泡或专属情话。
    /// </summary>
    private void PlayAmbientAction(string actionKey)
    {
        PlayAction(actionKey, exclusiveSeconds: 1.6);
        if (!_settings.ShowSpeechBubbles)
            return;

        // 偶尔：到期外备忘轻推 / 人物印象 / 偏好记忆（均非次次）
        if (TryShowMemoryAmbientBubble())
            return;

        var line = GetAmbientLineForAction(actionKey);
        if (line is not null && Random.Shared.NextDouble() < 0.72)
        {
            ShowMessage(line, 3.8);
            return;
        }

        if (Random.Shared.NextDouble() < 0.42)
            ShowRandomLoveMessage();
    }

    /// <summary>
    /// 记忆向环境气泡：备忘轻推 / 人物 / 偏好，共用 3.5 分钟节流。
    /// </summary>
    private bool TryShowMemoryAmbientBubble()
    {
        if (DateTime.Now - _lastPersonBubbleAt < TimeSpan.FromMinutes(3.5))
            return false;
        try
        {
            string? line = _agent.Memory.TryPickMemoNudgeBubble(_settings.PartnerName, probability: 0.22)
                           ?? _agent.Memory.TryPickPersonBubble(_settings.PartnerName, probability: 0.26)
                           ?? _agent.Memory.TryPickFactBubble(_settings.PartnerName, probability: 0.16);
            if (string.IsNullOrWhiteSpace(line))
                return false;
            _lastPersonBubbleAt = DateTime.Now;
            ShowMessage(line, 4.2);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? GetAmbientLineForAction(string actionKey) => actionKey switch
    {
        "wave" => $"嗨，{_settings.PartnerName}～",
        "jump" => "蹦一下！有没有被吓到？",
        "shy" => "被你看着会有点害羞……",
        "curious" => "你在忙什么呀？看起来好认真。",
        "music" or "dance" => "听，好像有一点小快乐。",
        "plush" => "抱抱我也可以哦。",
        "laugh" => "嘿嘿，今天心情不错。",
        "flowers" => "送你一束看不见的花。",
        "clap" => "你真的很棒，先夸你一下！",
        "gift" => "悄悄给你准备了一点小心意。",
        "pout" => "哼，再不理我我可要生气了……假的。",
        "heart" => $"比心给你，{_settings.PartnerName} ♥",
        "stretch" => "伸个懒腰，你也活动一下吧。",
        "drink" => "想起要喝水了吗？",
        "read" => "我先安静读一会儿书。",
        "sit" or "kneel" => "我就在这儿陪着你。",
        "sleep" => "困了就休息一下吧。",
        _ => null,
    };

    private void PetImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // 若上次拖拽异常未结束，先清掉，否则会一直吞点击
        if (_isDragging)
            EndDrag(interrupted: true);

        StopWalking(recover: false);
        _mouseDownClickCount = e.ClickCount;
        _dragMoved = false;
        _isDragging = true;
        _dragStartedAt = DateTime.Now;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        var startDpi = VisualTreeHelper.GetDpi(this);
        _dragDpiScaleX = Math.Max(0.1, startDpi.DpiScaleX);
        _dragDpiScaleY = Math.Max(0.1, startDpi.DpiScaleY);
        try
        {
            PetImage.CaptureMouse();
        }
        catch
        {
            // 捕获失败仍允许点击逻辑在 Up 时走完
        }
        e.Handled = true;
    }

    private void PetImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        // 左键已松开但没收到 Up（切窗口/UAC/多屏）：立刻结束拖拽，恢复可点
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(interrupted: true);
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(0.1, dpi.DpiScaleX);
        var scaleY = Math.Max(0.1, dpi.DpiScaleY);
        if (Math.Abs(scaleX - _dragDpiScaleX) > 0.001 || Math.Abs(scaleY - _dragDpiScaleY) > 0.001)
        {
            // 跨入不同缩放比例的显示器后重建锚点，避免把此前累计像素按新 DPI 全量重算而跳动。
            _dragStartScreen = current;
            _dragStartLeft = Left;
            _dragStartTop = Top;
            _dragDpiScaleX = scaleX;
            _dragDpiScaleY = scaleY;
            _dragLastDeltaX = 0;
            _dragLastDeltaY = 0;
            return;
        }
        var deltaX = (current.X - _dragStartScreen.X) / _dragDpiScaleX;
        var deltaY = (current.Y - _dragStartScreen.Y) / _dragDpiScaleY;
        _dragLastDeltaX = deltaX;
        _dragLastDeltaY = deltaY;
        if (!_dragMoved && Math.Abs(deltaX) + Math.Abs(deltaY) > 5)
        {
            _dragMoved = true;
            // 拖起瞬间：多样化开场动作 + 面向拖拽方向（素材默认朝左）
            if (Math.Abs(deltaX) > 2)
                FacingTransform.ScaleX = PetFacing.ScaleXForDragDelta(deltaX);
            var startRx = MouseReactionCatalog.PickDragStart(_settings.PartnerName);
            ApplyMouseReaction(startRx, exclusiveSeconds: 0);
        }
        if (!_dragMoved)
            return;

        if (Math.Abs(deltaX) > 8)
            FacingTransform.ScaleX = PetFacing.ScaleXForDragDelta(deltaX);
        Left = _dragStartLeft + deltaX;
        Top = _dragStartTop + deltaY;
    }

    private void PetImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        var localPoint = e.GetPosition(PetImage);
        var moved = _dragMoved;
        var clicks = _mouseDownClickCount;
        var dx = _dragLastDeltaX;
        var dy = _dragLastDeltaY;
        var dragDuration = _dragStartedAt == DateTime.MinValue
            ? 0.3
            : Math.Max(0.05, (DateTime.Now - _dragStartedAt).TotalSeconds);
        // 先释放捕获与拖拽标志，再播动画，避免动画期间仍吞事件
        _isDragging = false;
        _dragStartedAt = DateTime.MinValue;
        try
        {
            if (PetImage.IsMouseCaptured)
                PetImage.ReleaseMouseCapture();
        }
        catch { /* ignore */ }

        if (moved)
        {
            ScreenService.ClampToWorkingArea(this);
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var dir = MouseReactionCatalog.ResolveDragDirection(dx, dy);
            var intensity = MouseReactionCatalog.ResolveDragIntensity(dist, dragDuration);
            var rx = MouseReactionCatalog.PickDragRelease(dir, intensity, _settings.PartnerName, _lastMouseReactionAction);
            ApplyMouseReaction(rx, exclusiveSeconds: 1.8);
            SavePosition();
        }
        else if (clicks >= 2)
        {
            RegisterRapidClick();
            var rx = clicks >= 3
                ? MouseReactionCatalog.PickClick(
                    MouseReactionCatalog.ResolveZone(0.5, 0.5),
                    _rapidClickCount,
                    clicks,
                    _settings.PartnerName,
                    _lastMouseReactionAction)
                : MouseReactionCatalog.PickDoubleClick(_settings.PartnerName, _lastMouseReactionAction);
            ApplyMouseReaction(rx, exclusiveSeconds: 2.2);
            if (rx.PlaySound)
                PlaySound(SystemSounds.Asterisk);
            if (clicks >= 3)
                ResetRapidClicks();
        }
        else
        {
            HandleBodyZoneClick(localPoint, clicks);
        }
        _dragMoved = false;
        _mouseDownClickCount = 0;
        _dragLastDeltaX = 0;
        _dragLastDeltaY = 0;
        e.Handled = true;
    }

    /// <summary>
    /// 3×3 分区 + 连点档位 → 多样化动作（MouseReactionCatalog）。
    /// </summary>
    private void HandleBodyZoneClick(Point localPoint, int clickCount = 1)
    {
        var w = Math.Max(1, PetImage.ActualWidth);
        var h = Math.Max(1, PetImage.ActualHeight);
        var ratioX = localPoint.X / w;
        var ratioY = localPoint.Y / h;
        // 翻转时视觉左右对调，分区按视觉习惯校正
        if (FacingTransform.ScaleX < 0)
            ratioX = 1 - ratioX;

        RegisterRapidClick();
        var zone = MouseReactionCatalog.ResolveZone(ratioX, ratioY);
        var rx = MouseReactionCatalog.PickClick(
            zone, _rapidClickCount, clickCount, _settings.PartnerName, _lastMouseReactionAction);
        ApplyMouseReaction(rx, exclusiveSeconds: 1.8);
        if (rx.PlaySound)
            PlaySound(SystemSounds.Beep);
        if (_rapidClickCount >= 8)
            ResetRapidClicks();
    }

    private void ApplyMouseReaction(MouseReaction reaction, double exclusiveSeconds = 1.6)
    {
        _lastMouseReactionAction = reaction.ActionKey;
        AddAffection(reaction.Affection);
        // walk 作点击反馈时只播短动作，避免真走起来抢状态
        var key = reaction.ActionKey;
        if (key.Equals("walk", StringComparison.OrdinalIgnoreCase)
            || key.Equals("dizzy_spin", StringComparison.OrdinalIgnoreCase)
            || key.Equals("delighted", StringComparison.OrdinalIgnoreCase)
            || key.Equals("wink", StringComparison.OrdinalIgnoreCase)
            || key.Equals("bashful", StringComparison.OrdinalIgnoreCase)
            || key.Equals("look_back", StringComparison.OrdinalIgnoreCase)
            || key.Equals("tiptoe", StringComparison.OrdinalIgnoreCase)
            || key.Equals("land", StringComparison.OrdinalIgnoreCase)
            || key.Equals("annoyed", StringComparison.OrdinalIgnoreCase)
            || key.Equals("sleepy", StringComparison.OrdinalIgnoreCase)
            || key.Equals("celebrate", StringComparison.OrdinalIgnoreCase))
        {
            // 映射到目录中已有动作
            key = key switch
            {
                "walk" => "tiptoe",
                "dizzy_spin" => "surprised",
                "delighted" => "clap",
                "wink" => "shy",
                "bashful" => "shy",
                "look_back" => "curious",
                "tiptoe" => "dance",
                "land" => "recover",
                "annoyed" => "pout",
                "sleepy" => "sleep",
                "celebrate" => "birthday",
                _ => key,
            };
        }

        PlayAction(key, exclusiveSeconds: exclusiveSeconds);
        if (!string.IsNullOrWhiteSpace(reaction.Message))
            ShowMessage(reaction.Message, 3.4);
    }

    private void RegisterRapidClick()
    {
        var now = DateTime.Now;
        if (now - _lastRapidClickAt <= TimeSpan.FromSeconds(1.4))
            _rapidClickCount++;
        else
            _rapidClickCount = 1;
        _lastRapidClickAt = now;
    }

    private void ResetRapidClicks()
    {
        _rapidClickCount = 0;
        _lastRapidClickAt = DateTime.MinValue;
    }

    private void PetImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 中键：打开聊天窗口
        if (e.ChangedButton == MouseButton.Middle)
        {
            OpenChat();
            e.Handled = true;
        }
    }

    private void PetImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_settings.ClickThrough || !IsPetVisibleActive)
            return;
        var rx = MouseReactionCatalog.PickWheel(e.Delta > 0, _settings.PartnerName);
        ApplyMouseReaction(rx, exclusiveSeconds: 1.4);
        e.Handled = true;
    }

    private void OpenChat()
    {
        EnsureVisible();
        if (_chatWindow is { IsLoaded: true })
        {
            if (_chatWindow.WindowState == WindowState.Minimized)
                _chatWindow.WindowState = WindowState.Normal;
            if (!_chatWindow.IsVisible)
                _chatWindow.Show();
            _chatWindow.Topmost = _settings.AlwaysOnTop;
            _chatWindow.Activate();
            return;
        }

        _chatWindow = new ChatWindow(
            _settings,
            _agent,
            hostWindow: this,
            onPetReply: reply =>
            {
                if (reply.AffectionGain > 0)
                    AddAffection(reply.AffectionGain);
                if (!string.IsNullOrWhiteSpace(reply.ActionKey))
                    PlayAction(reply.ActionKey, exclusiveSeconds: 2.2);
                // 气泡只显示摘要，避免长文撑破布局。
                var bubble = reply.Text.Length > 60 ? reply.Text[..59] + "…" : reply.Text;
                ShowMessage(bubble, 4.8);
            },
            onUserSpoke: () =>
            {
                StopWalking(recover: false);
                _exclusiveUntil = DateTime.Now.AddSeconds(1.5);
                AddAffection(1);
            })
        {
            Topmost = _settings.AlwaysOnTop,
        };

        PositionChatWindow(_chatWindow);
        _chatWindow.Closed += (_, _) => _chatWindow = null;
        _chatWindow.Show();
        _chatWindow.Activate();
        PlayAction("curious", exclusiveSeconds: 1.5);
        ShowMessage("我们慢慢聊，不着急。中键或托盘也能再打开我。", 3.2);
    }

    private void PositionChatWindow(Window chat)
    {
        try
        {
            var area = ScreenService.GetWorkingArea(this);
            // 先按工作区给聊天窗合理尺寸，再定位（避免 Width 未布局时为 NaN）
            if (chat is ChatWindow)
            {
                var maxWidth = Math.Max(280, area.Width - 24);
                var maxHeight = Math.Max(360, area.Height - 24);
                chat.MinWidth = Math.Min(320, maxWidth);
                chat.MinHeight = Math.Min(420, maxHeight);
                chat.MaxWidth = maxWidth;
                chat.MaxHeight = maxHeight;
                chat.Width = Math.Min(Math.Clamp(area.Width * 0.34, 360, 560), maxWidth);
                chat.Height = Math.Min(Math.Clamp(area.Height * 0.78, 520, 860), maxHeight);
            }

            var chatW = chat.Width > 0 && !double.IsNaN(chat.Width) ? chat.Width : 420;
            var chatH = chat.Height > 0 && !double.IsNaN(chat.Height) ? chat.Height : 680;

            // 优先桌宠左侧；不够则右侧；再不够则贴工作区左边并垂直居中靠近桌宠
            var preferredLeft = Left - chatW - 16;
            if (preferredLeft < area.Left + 4)
                preferredLeft = Left + Width + 16;
            if (preferredLeft + chatW > area.Right - 4)
                preferredLeft = Math.Max(area.Left + 8, area.Right - chatW - 8);

            var preferredTop = Top + (Height - chatH) / 2;
            if (preferredTop < area.Top + 8)
                preferredTop = area.Top + 8;
            if (preferredTop + chatH > area.Bottom - 8)
                preferredTop = Math.Max(area.Top + 8, area.Bottom - chatH - 8);

            chat.Left = preferredLeft;
            chat.Top = preferredTop;
            chat.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        catch
        {
            chat.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void PetImage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // 拖动被系统打断时清理残留状态，避免一直卡在 dragged。
        if (!_isDragging)
            return;
        EndDrag(interrupted: true);
    }

    private void EndDrag(bool interrupted)
    {
        if (!_isDragging && !PetImage.IsMouseCaptured)
        {
            _dragMoved = false;
            _mouseDownClickCount = 0;
            _dragStartedAt = DateTime.MinValue;
            return;
        }

        var moved = _dragMoved;
        _isDragging = false;
        _dragStartedAt = DateTime.MinValue;
        try
        {
            if (PetImage.IsMouseCaptured)
                PetImage.ReleaseMouseCapture();
        }
        catch
        {
            // ignore
        }

        StopSpriteAnimations();
        if (moved)
        {
            ScreenService.ClampToWorkingArea(this);
            PlayAction("recover", exclusiveSeconds: 1.2);
            SavePosition();
        }
        else if (interrupted)
        {
            ReturnToAmbientAction();
        }

        _dragMoved = false;
        _mouseDownClickCount = 0;
    }

    private void AddAffection(int amount)
    {
        _settings.Affection = Math.Clamp(_settings.Affection + amount, 0, 999999);
        _settings.InteractionCount = Math.Clamp(_settings.InteractionCount + 1, 0, int.MaxValue);
        if (_settings.InteractionCount % 5 == 0)
            SaveSettings();
    }

    private void ShowRandomLoveMessage()
    {
        if (_settings.LoveMessages.Count == 0)
            return;
        ShowMessage(FormatMessage(_settings.LoveMessages[Random.Shared.Next(_settings.LoveMessages.Count)]), 4.2);
    }

    private string FormatMessage(string message) => message
        .Replace("{name}", _settings.PartnerName, StringComparison.OrdinalIgnoreCase)
        .Replace("{pet}", _settings.PetName, StringComparison.OrdinalIgnoreCase);

    private void ShowMessage(string message, double seconds = 4)
    {
        // 用户主动拖动或聊天互动时，即使安静模式也允许短暂显示气泡。
        if (!_settings.ShowSpeechBubbles)
            return;
        if (_settings.QuietMode && !_isDragging && _chatWindow is not { IsVisible: true })
            return;

        var text = FormatMessage(message).Trim();
        if (text.Length == 0)
            return;
        // 过长时截断，避免气泡撑破布局。
        if (text.Length > 72)
            text = text[..71] + "…";

        SpeechText.Text = text;
        SpeechBubble.Visibility = Visibility.Visible;
        SpeechTail.Visibility = Visibility.Visible;
        AnimateBubbleIn();

        _bubbleTimer.Stop();
        _bubbleTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1.5, 12));
        _bubbleTimer.Start();
    }

    private void AnimateBubbleIn()
    {
        SpeechHost.BeginAnimation(UIElement.OpacityProperty, null);
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        SpeechHost.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        // 轻微上浮，让气泡出现更有生命力。
        var transform = SpeechHost.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            SpeechHost.RenderTransform = transform;
        }
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void AnimateBubbleOut(Action? completed = null)
    {
        SpeechHost.BeginAnimation(UIElement.OpacityProperty, null);
        var fadeOut = new DoubleAnimation(SpeechHost.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            SpeechBubble.Visibility = Visibility.Collapsed;
            SpeechTail.Visibility = Visibility.Collapsed;
            completed?.Invoke();
        };
        SpeechHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void BubbleTimer_Tick(object? sender, EventArgs e)
    {
        _bubbleTimer.Stop();
        AnimateBubbleOut();
    }

    private void PlaySound(SystemSound sound)
    {
        if (_settings.SoundEnabled && !IsQuietNow())
            sound.Play();
    }

    private void ReminderTimer_Tick(object? sender, EventArgs e)
    {
        if (IsQuietNow() || !IsPetVisibleActive || _isDragging)
            return;
        // 专注中不打断；专属动作进行中稍后再提醒。
        if (IsFocusActive || (IsExclusiveBusy && !_isWalking))
            return;

        // 备忘到期优先（最多一条气泡，避免刷屏）
        if (DateTime.Now - _lastMemoCheck >= TimeSpan.FromSeconds(20))
        {
            _lastMemoCheck = DateTime.Now;
            try
            {
                var due = _agent.Memory.PopDueMemos();
                if (due.Count > 0)
                {
                    var m = due[0];
                    StopWalking(recover: false);
                    PlayAction("reminder", exclusiveSeconds: 3);
                    ShowMessage($"⏰ 提醒：{m.Text}", 6.5);
                    PlaySound(SystemSounds.Exclamation);
                    CheckSpecialDate(force: false);
                    return;
                }
            }
            catch { /* ignore */ }
        }

        if (_settings.WaterReminderMinutes > 0
            && DateTime.Now - _lastWaterReminder >= TimeSpan.FromMinutes(_settings.WaterReminderMinutes))
        {
            _lastWaterReminder = DateTime.Now;
            StopWalking(recover: false);
            PlayAction("drink", exclusiveSeconds: 3.5);
            ShowMessage($"{_settings.PartnerName}，到喝水时间啦。", 5);
        }
        else if (_settings.RestReminderMinutes > 0
                 && DateTime.Now - _lastRestReminder >= TimeSpan.FromMinutes(_settings.RestReminderMinutes))
        {
            _lastRestReminder = DateTime.Now;
            StopWalking(recover: false);
            PlayAction("stretch", exclusiveSeconds: 3.5);
            ShowMessage("眼睛离开屏幕一会儿，伸个懒腰吧。", 5);
        }
        else
        {
            // 上午一次轻量天气关怀（异步，不阻塞 UI）
            TryMorningWeatherBubble();
        }
        CheckSpecialDate(force: false);
    }

    /// <summary>系统监控触发器：CPU/内存/电池/久坐 → 桌宠动作与气泡。CPU 采样耗时，放后台线程。</summary>
    private void SystemTriggerTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExiting || !IsPetVisibleActive || _isDragging)
            return;
        var cfg = _settings.SystemTriggers;
        if (cfg is null || !cfg.Enabled)
        {
            _wasUserAway = false;
            _lastSystemResourceSampleAt = DateTime.MinValue;
            return;
        }

        // 先跟踪离开/返回状态。旧逻辑在人离开时立即弹“回来啦”，气泡会在用户返回前消失。
        var idle = SystemMonitorService.GetIdleSeconds();
        var returnedFromIdle = SystemTriggerConfig.HasReturnedFromIdle(
            _wasUserAway, idle, cfg.IdleTooLongSeconds);
        if (!returnedFromIdle && cfg.IdleTooLongSeconds > 0 && idle >= cfg.IdleTooLongSeconds)
        {
            _wasUserAway = true;
            return;
        }
        if (returnedFromIdle)
            _wasUserAway = false;
        else if (cfg.IdleTooLongSeconds <= 0)
            _wasUserAway = false;

        // 节流：同类触发冷却内不再弹
        if (DateTime.Now - _lastSystemTriggerAt < TimeSpan.FromSeconds(cfg.CooldownSeconds > 0 ? cfg.CooldownSeconds : 600))
            return;
        // 安静时段不打断
        if (IsQuietNow())
            return;

        if (returnedFromIdle)
        {
            _lastSystemTriggerAt = DateTime.Now;
            StopWalking(recover: false);
            PlayAction("stretch", exclusiveSeconds: 3);
            ShowMessage("回来啦～先喝口水、活动一下，再继续也不迟。", 6);
            return;
        }

        if (DateTime.Now - _lastSystemResourceSampleAt < TimeSpan.FromMinutes(2))
            return;
        _lastSystemResourceSampleAt = DateTime.Now;

        // CPU 采样约 0.5s，放后台避免卡 UI
        _ = Task.Run(() =>
        {
            var result = SystemMonitorService.EvaluateTriggers(cfg, idle);
            if (result is null)
                return;
            Dispatcher.Invoke(() =>
            {
                if (_isExiting || !IsPetVisibleActive || IsQuietNow())
                    return;
                _lastSystemTriggerAt = DateTime.Now;
                StopWalking(recover: false);
                PlayAction(result.ActionKey, exclusiveSeconds: 3);
                ShowMessage(result.Message, 6);
            });
        });
    }

    private void TryMorningWeatherBubble()
    {
        var hour = DateTime.Now.Hour;
        if (hour is < 8 or > 10) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_lastWeatherBubbleDay == today) return;
        if (IsQuietNow() || Random.Shared.NextDouble() > 0.35) return;
        _lastWeatherBubbleDay = today;
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await WindowsAgentToolkit.ExecuteAsync("get_weather", new System.Text.Json.Nodes.JsonObject(), CancellationToken.None)
                    .ConfigureAwait(false);
                // 只抽预警行做短气泡
                var alertLines = report.Split('\n')
                    .Where(l => l.Contains("高温", StringComparison.Ordinal) || l.Contains("降水", StringComparison.Ordinal)
                                                                            || l.Contains("雷电", StringComparison.Ordinal))
                    .Take(2)
                    .Select(l => l.TrimStart('·', ' ', '\t'))
                    .ToList();
                var msg = alertLines.Count > 0
                    ? "天气提醒：" + string.Join("；", alertLines)
                    : null;
                if (msg is null) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsPetVisibleActive || IsQuietNow()) return;
                    ShowMessage(msg.Length > 70 ? msg[..69] + "…" : msg, 5.5);
                    PlayAction("rain", exclusiveSeconds: 2);
                });
            }
            catch { /* 网络失败静默 */ }
        });
    }

    private void CheckSpecialDate(bool force)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!force && _lastSpecialDate == today)
            return;

        if (_settings.Birthday is { } birthday
            && birthday.Month == DateTime.Today.Month && birthday.Day == DateTime.Today.Day)
        {
            _lastSpecialDate = today;
            PlayAction("birthday");
            ShowMessage($"{_settings.PartnerName}，生日快乐！今天要收下双倍的喜欢。", 8);
            PlaySound(SystemSounds.Exclamation);
        }
        else if (_settings.Anniversary is { } anniversary
                 && anniversary.Month == DateTime.Today.Month && anniversary.Day == DateTime.Today.Day)
        {
            _lastSpecialDate = today;
            var years = Math.Max(0, DateTime.Today.Year - anniversary.Year);
            PlayAction("gift");
            ShowMessage(years > 0
                ? $"这是我们一起走过的第 {years} 年。以后也继续陪着彼此吧。"
                : "今天是属于我们的纪念日。以后也继续陪着彼此吧。", 8);
            PlaySound(SystemSounds.Exclamation);
        }
    }

    private string GetTimeGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            < 6 => $"这么晚还没睡呀，{_settings.PartnerName}要注意休息。",
            < 11 => $"早上好，{_settings.PartnerName}。今天也要开心呀。",
            < 14 => "中午好，记得按时吃饭。",
            < 18 => "下午辛苦啦，我来陪你一会儿。",
            < 23 => $"晚上好，{_settings.PartnerName}。今天过得怎么样？",
            _ => "夜深啦，忙完就早点休息吧。",
        };
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

    private void StartFocusSession()
    {
        if (IsFocusActive)
        {
            _focusEnd = null;
            _focusTimer.Stop();
            FocusBadge.Visibility = Visibility.Collapsed;
            if (_focusMenuItem is not null)
                _focusMenuItem.Text = "开始 25 分钟专注";
            ShowMessage("专注陪伴已结束。", 3);
            ReturnToAmbientAction();
            return;
        }

        StopWalking(recover: false);
        _focusEnd = DateTime.Now.AddMinutes(25);
        _focusTimer.Start();
        FocusBadge.Visibility = Visibility.Visible;
        if (_focusMenuItem is not null)
            _focusMenuItem.Text = "取消本次专注";
        PlayAction("focus", exclusiveSeconds: 2);
        ShowMessage("接下来的 25 分钟，我陪你专心做完这件事。", 5);
        UpdateFocusBadge();
    }

    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        if (_focusEnd is null)
            return;
        if (_focusEnd <= DateTime.Now)
        {
            _focusEnd = null;
            _focusTimer.Stop();
            FocusBadge.Visibility = Visibility.Collapsed;
            if (_focusMenuItem is not null)
                _focusMenuItem.Text = "开始 25 分钟专注";
            PlayAction("clap");
            ShowMessage("25 分钟完成！起来喝口水吧。", 6);
            PlaySound(SystemSounds.Exclamation);
            return;
        }
        UpdateFocusBadge();
    }

    private void UpdateFocusBadge()
    {
        if (_focusEnd is null)
            return;
        var remaining = _focusEnd.Value - DateTime.Now;
        FocusText.Text = $"专注陪伴  {Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}";
    }

    private void FullscreenTimer_Tick(object? sender, EventArgs e)
    {
        // 手动隐藏时不做全屏逻辑，避免托盘显示/全屏抢状态。
        if (_manuallyHidden || !IsVisible)
            return;

        bool shouldHide;
        if (!_settings.HideForFullscreen)
            shouldHide = false;
        else if (!NativeWindowService.TryGetForegroundFullscreen(this, out shouldHide))
            return;
        if (shouldHide == _hiddenForFullscreen)
            return;

        _hiddenForFullscreen = shouldHide;
        if (shouldHide)
        {
            // 全屏前若正在拖拽，先松手，否则恢复后捕获状态错乱
            if (_isDragging)
                EndDrag(interrupted: true);
            StopWalking(recover: false);
            _actionTimer.Stop();
            _movementTimer.Stop();
            _behaviorTimer.Stop();
            StopSpriteAnimations();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            PetRoot.IsHitTestVisible = false;
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = _introDone ? 1 : Math.Max(Opacity, 0.01);
            PetRoot.IsHitTestVisible = true;
            ApplyClickThroughState(force: true);
            EnsureBehaviorTimersRunning();
            DisplayCurrentFrame();
            if (IsPetVisibleActive)
                _actionTimer.Start();
        }
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip
        {
            Font = new Drawing.Font("Microsoft YaHei UI", 9F),
            ShowImageMargin = false,
        };

        _showMenuItem = AddMenuItem(_trayMenu.Items, "隐藏桌宠  (Ctrl+Shift+S)", (_, _) => ToggleVisibility());
        AddMenuItem(_trayMenu.Items, "和我说句话", (_, _) => { EnsureVisible(); ShowRandomLoveMessage(); PlayAction("wave"); });
        AddMenuItem(_trayMenu.Items, "和我聊聊…  (Ctrl+Shift+C)", (_, _) => OpenChat());

        var actionMenu = new Forms.ToolStripMenuItem("选择动作");
        AddMenuItem(actionMenu.DropDownItems, "挥手", (_, _) => RunTrayAction("wave", "嗨，我一直都在。"));
        AddMenuItem(actionMenu.DropDownItems, "比心", (_, _) => RunTrayAction("heart", $"给{_settings.PartnerName}一个大大的心。"));
        AddMenuItem(actionMenu.DropDownItems, "跳舞", (_, _) => RunTrayAction("dance", "这支小舞只跳给你看。"));
        AddMenuItem(actionMenu.DropDownItems, "读书", (_, _) => RunTrayAction("read", "一起安静读一会儿吧。"));
        AddMenuItem(actionMenu.DropDownItems, "睡觉", (_, _) => RunTrayAction("sleep", "我先靠在这里眯一会儿。"));
        AddMenuItem(actionMenu.DropDownItems, "生日惊喜", (_, _) => RunTrayAction("birthday", "今天也可以当作值得庆祝的一天。"));
        _trayMenu.Items.Add(actionMenu);

        _focusMenuItem = AddMenuItem(_trayMenu.Items, "开始 25 分钟专注", (_, _) => { EnsureVisible(); StartFocusSession(); });
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        _autoWalkMenuItem = AddCheckMenuItem(_trayMenu.Items, "自动散步", _settings.AutoWalk, (_, _) =>
        {
            _settings.AutoWalk = _autoWalkMenuItem!.Checked;
            if (!_settings.AutoWalk) StopWalking();
            SaveSettings();
        });
        _topmostMenuItem = AddCheckMenuItem(_trayMenu.Items, "始终置顶", _settings.AlwaysOnTop, (_, _) =>
        {
            _settings.AlwaysOnTop = _topmostMenuItem!.Checked;
            Topmost = _settings.AlwaysOnTop;
            if (_chatWindow is { IsLoaded: true })
                _chatWindow.Topmost = _settings.AlwaysOnTop;
            SaveSettings();
        });
        _startupMenuItem = AddCheckMenuItem(_trayMenu.Items, "开机自动启动", _settings.StartWithWindows, (_, _) =>
        {
            _settings.StartWithWindows = _startupMenuItem!.Checked;
            if (!StartupService.SetEnabled(_settings.StartWithWindows))
            {
                _settings.StartWithWindows = StartupService.IsEnabled();
                _startupMenuItem.Checked = _settings.StartWithWindows;
                MessageBox.Show("开机启动设置未能写入。请确认当前账户允许修改启动项。",
                    "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SaveSettings();
        });
        _quietMenuItem = AddCheckMenuItem(_trayMenu.Items, "安静模式", _settings.QuietMode, (_, _) =>
        {
            _settings.QuietMode = _quietMenuItem!.Checked;
            SaveSettings();
        });
        _fullscreenMenuItem = AddCheckMenuItem(_trayMenu.Items, "全屏时自动隐藏", _settings.HideForFullscreen, (_, _) =>
        {
            _settings.HideForFullscreen = _fullscreenMenuItem!.Checked;
            SaveSettings();
        });
        _clickThroughMenuItem = AddCheckMenuItem(_trayMenu.Items, "鼠标穿透  (Ctrl+Shift+P)", _settings.ClickThrough, (_, _) =>
        {
            _settings.ClickThrough = _clickThroughMenuItem!.Checked;
            ApplyClickThroughState(force: true);
            SaveSettings();
            if (_settings.ClickThrough)
                ShowMessage("已开启穿透：点不到我是正常的，托盘或 Ctrl+Shift+P 可关掉。", 3.5);
            else
                ShowMessage("已关闭穿透，可以点我啦～", 2.5);
        });

        var sizeMenu = new Forms.ToolStripMenuItem("角色大小");
        AddMenuItem(sizeMenu.DropDownItems, "70%", (_, _) => SetScale(0.70));
        AddMenuItem(sizeMenu.DropDownItems, "85%", (_, _) => SetScale(0.85));
        AddMenuItem(sizeMenu.DropDownItems, "100%", (_, _) => SetScale(1.00));
        AddMenuItem(sizeMenu.DropDownItems, "120%", (_, _) => SetScale(1.20));
        AddMenuItem(sizeMenu.DropDownItems, "140%", (_, _) => SetScale(1.40));
        _trayMenu.Items.Add(sizeMenu);

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddMenuItem(_trayMenu.Items, "个性化设置…  (Ctrl+Shift+,)", (_, _) => OpenSettings());
        AddMenuItem(_trayMenu.Items, "快捷键说明  (Ctrl+Shift+H)", (_, _) => ShowHotkeyHelp());
        AddMenuItem(_trayMenu.Items, "回到屏幕右下角", (_, _) => ResetPosition());
        AddMenuItem(_trayMenu.Items, "打开本地配置目录", (_, _) => OpenConfigDirectory());
        AddMenuItem(_trayMenu.Items, "打开长期记忆 agent.md", (_, _) => OpenAgentMd());
        AddMenuItem(_trayMenu.Items, "检查更新…", (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await CheckForUpdatesAsync(interactive: true).ConfigureAwait(true); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "检查更新失败：" + ex.Message, "更新",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        });
        AddMenuItem(_trayMenu.Items, "关于", (_, _) => ShowAbout());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddMenuItem(_trayMenu.Items, "完全退出", (_, _) => ExitApplication());
        // 一键卸载：清启动项 + 本地数据 + 尝试删除 EXE
        AddMenuItem(_trayMenu.Items, "一键卸载（清除全部数据）…", (_, _) => UninstallCompletely());

        Drawing.Icon icon;
        try
        {
            icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                   ?? Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = Drawing.SystemIcons.Application;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "小申陪伴",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Dispatcher.Invoke(ToggleVisibility);
        };
    }

    private static Forms.ToolStripMenuItem AddMenuItem(
        Forms.ToolStripItemCollection collection, string text, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += handler;
        collection.Add(item);
        return item;
    }

    private static Forms.ToolStripMenuItem AddCheckMenuItem(
        Forms.ToolStripItemCollection collection, string text, bool isChecked, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = isChecked,
        };
        item.Click += handler;
        collection.Add(item);
        return item;
    }

    private void UpdateTrayChecks()
    {
        if (_autoWalkMenuItem is not null) _autoWalkMenuItem.Checked = _settings.AutoWalk;
        if (_topmostMenuItem is not null) _topmostMenuItem.Checked = _settings.AlwaysOnTop;
        if (_startupMenuItem is not null) _startupMenuItem.Checked = _settings.StartWithWindows;
        if (_quietMenuItem is not null) _quietMenuItem.Checked = _settings.QuietMode;
        if (_clickThroughMenuItem is not null) _clickThroughMenuItem.Checked = _settings.ClickThrough;
        if (_fullscreenMenuItem is not null) _fullscreenMenuItem.Checked = _settings.HideForFullscreen;
    }

    private void RunTrayAction(string action, string message)
    {
        EnsureVisible();
        StopWalking(recover: false);
        PlayAction(action, exclusiveSeconds: 2.5);
        ShowMessage(message, 4.5);
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopWalking();
        _trayMenu?.Show(Forms.Cursor.Position);
        e.Handled = true;
    }

    private void ToggleVisibility()
    {
        if (_manuallyHidden || !IsVisible)
            EnsureVisible();
        else
        {
            _manuallyHidden = true;
            _hiddenForFullscreen = false;
            StopWalking(recover: false);
            _actionTimer.Stop();
            _movementTimer.Stop();
            _behaviorTimer.Stop();
            StopSpriteAnimations();
            Hide();
            if (_showMenuItem is not null) _showMenuItem.Text = "显示桌宠";
        }
    }

    private void EnsureVisible()
    {
        _manuallyHidden = false;
        if (!IsVisible) Show();
        // 用户从托盘主动显示时，优先可见，不被残留全屏标记挡住。
        _hiddenForFullscreen = false;
        if (_isDragging)
            EndDrag(interrupted: true);
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        PetRoot.IsHitTestVisible = true;
        ApplyClickThroughState(force: true);
        EnsureBehaviorTimersRunning();
        if (!_actionTimer.IsEnabled)
        {
            DisplayCurrentFrame();
            _actionTimer.Start();
        }
        if (_showMenuItem is not null) _showMenuItem.Text = "隐藏桌宠";
        Activate();
    }

    private void SetScale(double scale)
    {
        _settings.Scale = scale;
        ApplySettings();
        SaveSettings();
    }

    private void ResetPosition()
    {
        EnsureVisible();
        ScreenService.PlaceBottomRight(this);
        SavePosition();
        PlayAction("jump", exclusiveSeconds: 1.5);
    }

    /// <summary>
    /// 全局快捷键表（与托盘同一套命令）：
    /// Ctrl+Shift+S 显示/隐藏 · C 聊天 · P 穿透 · , 设置 · H 说明
    /// </summary>
    private void AttachHotkeys()
    {
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = new HotkeyService();
            _hotkeys.Attach(this, id =>
            {
                Dispatcher.Invoke(() =>
                {
                    switch (id)
                    {
                        case HotkeyService.IdToggleVisible:
                            ToggleVisibility();
                            break;
                        case HotkeyService.IdOpenChat:
                            OpenChat();
                            break;
                        case HotkeyService.IdClickThrough:
                            _settings.ClickThrough = !_settings.ClickThrough;
                            ApplyClickThroughState(force: true);
                            UpdateTrayChecks();
                            SaveSettings();
                            ShowMessage(_settings.ClickThrough
                                ? "已开启鼠标穿透（点不到我是正常的）。"
                                : "已关闭鼠标穿透，可以点我啦。", 2.8);
                            break;
                        case HotkeyService.IdSettings:
                            OpenSettings();
                            break;
                        case HotkeyService.IdHelp:
                            ShowHotkeyHelp();
                            break;
                    }
                });
            });
            if (_hotkeys.FailedHotkeyIds.Count > 0)
            {
                var names = _hotkeys.FailedHotkeyIds.Select(id => id switch
                {
                    HotkeyService.IdToggleVisible => "Ctrl+Shift+S",
                    HotkeyService.IdOpenChat => "Ctrl+Shift+C",
                    HotkeyService.IdClickThrough => "Ctrl+Shift+P",
                    HotkeyService.IdSettings => "Ctrl+Shift+,",
                    HotkeyService.IdHelp => "Ctrl+Shift+H",
                    _ => $"ID {id}",
                });
                _hotkeyWarning = "以下快捷键已被其他程序占用：" + string.Join("、", names)
                                 + "。仍可使用系统托盘完成相同操作。";
            }
            else
            {
                _hotkeyWarning = null;
            }
        }
        catch
        {
            // 热键注册失败不阻断桌宠本体。
        }
    }

    private void ShowHotkeyHelp()
    {
        EnsureVisible();
        ShowMessage("快捷键：Ctrl+Shift+S 显隐 · C 聊天 · P 穿透 · , 设置 · H 帮助", 6.5);
        PlayAction("point");
        MessageBox.Show(
            "小申陪伴 · 快捷键（对标经典桌宠可发现操作）\n\n" +
            "Ctrl+Shift+S　显示 / 隐藏桌宠\n" +
            "Ctrl+Shift+C　打开聊天（多模态 Agent）\n" +
            "Ctrl+Shift+P　切换鼠标穿透\n" +
            "Ctrl+Shift+,　个性化设置\n" +
            "Ctrl+Shift+H　显示本说明\n\n" +
            "鼠标：单击分区互动 · 双击比心 · 中键聊天 · 拖拽移动 · 右键菜单\n" +
            "托盘：与上述功能一致，隐藏后请从托盘找回。"
            + (string.IsNullOrWhiteSpace(_hotkeyWarning) ? "" : "\n\n注意：" + _hotkeyWarning),
            "快捷键说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenSettings()
    {
        StopWalking();
        var settingsWindow = new SettingsWindow(_settings)
        {
            Owner = this,
            Topmost = Topmost,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (settingsWindow.ShowDialog() == true)
        {
            var desiredStartup = _settings.StartWithWindows;
            if (!StartupService.SetEnabled(desiredStartup))
            {
                // 写入失败：回滚为注册表真实状态并持久化，避免界面与系统不一致。
                _settings.StartWithWindows = StartupService.IsEnabled();
                MessageBox.Show("开机启动设置未能写入，已恢复为系统当前状态。",
                    "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ApplySettings();
            SaveSettings();
            UpdateTrayChecks();
            ShowMessage("设置已经记住啦。", 3);
            PlayAction("clap", exclusiveSeconds: 2);
        }
    }

    private void OpenConfigDirectory()
    {
        try
        {
            Directory.CreateDirectory(_settingsService.ConfigDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _settingsService.ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show($"无法自动打开目录，请手动访问：\n{_settingsService.ConfigDirectory}",
                "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenAgentMd()
    {
        try
        {
            var path = _agent.AgentMd.FilePath;
            _agent.AgentMd.EnsureFileExists();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            ShowMessage("长期记忆 agent.md 已打开，可在「用户手写备注」区补充～", 3.5);
        }
        catch
        {
            MessageBox.Show(
                $"无法打开 agent.md，请手动访问：\n{_agent.AgentMd.FilePath}",
                "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowAbout()
    {
        var days = Math.Max(1, (DateTime.Today - _settings.FirstMetDate.Date).Days + 1);
        MessageBox.Show(
            AppCredits.AboutBody(days, _settings.InteractionCount, _settings.Affection),
            $"关于{AppCredits.ProductName} · {AppCredits.DevelopedByLine}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SavePosition()
    {
        _settings.LastLeft = Left;
        _settings.LastTop = Top;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch
        {
            // Settings are convenience data; a transient write failure should not stop the pet.
        }
    }

    private void ExitApplication()
    {
        PrepareForSessionEnd();
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 右键菜单：一键卸载——确认后清除启动项、%LocalAppData%\BunnyCompanion 全部数据，并安排删除 EXE。
    /// </summary>
    private void UninstallCompletely()
    {
        if (_isUninstalling || _isExiting)
            return;

        var confirm = MessageBox.Show(
            "确定要从这台电脑上彻底卸载「小申陪伴」吗？\n\n" +
            "将删除：\n" +
            "· 开机自动启动项\n" +
            "· 本地设置、爱心值、互动记录、日志\n" +
            "· 程序文件（退出后自动尝试删除 EXE）\n\n" +
            "此操作不可恢复。",
            "一键卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        var confirm2 = MessageBox.Show(
            "再确认一次：真的要卸载并清空所有数据吗？",
            "最后确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm2 != MessageBoxResult.Yes)
            return;

        _isUninstalling = true;

        // 先停 UI / 热键 / 托盘，再清数据，避免卸载过程中再写 settings
        try { _chatWindow?.Close(); } catch { /* ignore */ }
        _chatWindow = null;
        try { _hotkeys?.Dispose(); } catch { /* ignore */ }
        _hotkeys = null;

        var result = UninstallService.Run(_settingsService.ConfigDirectory);

        MessageBox.Show(
            result.Summary,
            "卸载完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        // 退出时不再保存设置
        PrepareForSessionEnd();
        try { Close(); } catch { /* ignore */ }
        Application.Current.Shutdown();
    }

    internal void PrepareForSessionEnd()
    {
        if (_isExiting)
            return;
        _isExiting = true;
        StopWalking(recover: false);
        StopSpriteAnimations();
        _actionTimer.Stop();
        _movementTimer.Stop();
        _behaviorTimer.Stop();
        _bubbleTimer.Stop();
        _reminderTimer.Stop();
        _fullscreenTimer.Stop();
        _focusTimer.Stop();
        _inputHealTimer.Stop();
        _systemTriggerTimer.Stop();
        if (_isDragging)
            EndDrag(interrupted: true);
        try
        {
            _chatWindow?.Close();
        }
        catch
        {
            // 退出阶段关闭聊天窗失败不应阻断清理。
        }
        _chatWindow = null;
        try { _hotkeys?.Dispose(); } catch { /* ignore */ }
        _hotkeys = null;

        // 卸载模式：禁止再写回 settings.json（目录可能已删）
        if (!_isUninstalling)
        {
            if (IsLoaded)
            {
                _settings.LastLeft = Left;
                _settings.LastTop = Top;
            }
            SaveSettings();
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _notifyIcon = null;
        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;
        e.Cancel = true;
        _manuallyHidden = true;
        _hiddenForFullscreen = false;
        StopWalking(recover: false);
        _actionTimer.Stop();
        _movementTimer.Stop();
        _behaviorTimer.Stop();
        StopSpriteAnimations();
        Hide();
        if (_showMenuItem is not null) _showMenuItem.Text = "显示桌宠";
    }
}
