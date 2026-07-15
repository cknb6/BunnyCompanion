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
    private bool _isExiting;
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
    private ChatWindow? _chatWindow;
    private int _rapidClickCount;
    private DateTime _lastRapidClickAt = DateTime.MinValue;

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

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            NativeWindowService.SetClickThrough(this, _settings.ClickThrough);
            var startedWithWindows = _arguments.Any(argument =>
                argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            var quietStartup = startedWithWindows || IsQuietNow();
            PlayStartupAnimation(fancy: !quietStartup, () =>
            {
                _reminderTimer.Start();
                _behaviorTimer.Start();
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
            });
        }));
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
            Opacity = 1;
            IntroScale.ScaleX = IntroScale.ScaleY = 1;
            IntroOffset.Y = 0;
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
            NativeWindowService.SetClickThrough(this, _settings.ClickThrough);
        UpdateTrayChecks();
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

        if (_settings.ClickThrough || _hiddenForFullscreen || Opacity < 0.01)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        var packed = lParam.ToInt64();
        var screenX = unchecked((short)(packed & 0xFFFF));
        var screenY = unchecked((short)((packed >> 16) & 0xFFFF));
        var windowPoint = PointFromScreen(new Point(screenX, screenY));
        if (IsOpaqueSpritePoint(windowPoint))
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
            return false;
        }

        var localX = imagePoint.X;
        var localY = imagePoint.Y;
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
        // 轻微邻域采样，减少翻转/抗锯齿边缘点不中。
        return SampleAlpha(alpha, source.PixelWidth, source.PixelHeight, pixelX, pixelY) >= 22;
    }

    private static byte SampleAlpha(byte[] alpha, int width, int height, int x, int y)
    {
        byte best = alpha[y * width + x];
        if (best >= 22)
            return best;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                continue;
            best = Math.Max(best, alpha[ny * width + nx]);
        }
        return best;
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
        // 与喝水/比心/专注等互斥：专注中、专属动作中、拖动中不散步。
        if (_isDragging || !_settings.AutoWalk || IsFocusActive || IsExclusiveBusy || !IsPetVisibleActive)
            return;

        var area = ScreenService.GetWorkingArea(this);
        var roomLeft = Left - area.Left;
        var roomRight = area.Right - (Left + Width);
        _walkDirection = roomRight < Width / 2 ? -1 : roomLeft < Width / 2 ? 1 : Random.Shared.Next(2) == 0 ? -1 : 1;
        FacingTransform.ScaleX = _walkDirection;
        _walkUntil = DateTime.Now.AddSeconds(Random.Shared.Next(3, 7));
        _isWalking = true;
        _movementTimer.Start();
        PlayAction("walk");
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
            _walkDirection = 1;
            FacingTransform.ScaleX = 1;
        }
        else if (Left + Width >= area.Right)
        {
            Left = area.Right - Width;
            _walkDirection = -1;
            FacingTransform.ScaleX = -1;
        }

        if (DateTime.Now >= _walkUntil)
        {
            StopWalking();
            SavePosition();
        }
    }

    private void BehaviorTimer_Tick(object? sender, EventArgs e)
    {
        _behaviorTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(6, 13));
        if (!IsPetVisibleActive || _isDragging || _isWalking || IsFocusActive || IsExclusiveBusy)
            return;

        if (IsQuietNow())
        {
            if (Random.Shared.NextDouble() < 0.6)
            {
                PlayAction("sleep", exclusiveSeconds: 2);
                if (Random.Shared.NextDouble() < 0.35)
                    ShowMessage("安静陪着你，先眯一会儿……", 3.5);
            }
            return;
        }

        var roll = Random.Shared.NextDouble();
        if (_settings.AutoWalk && roll < 0.24)
        {
            StartWalking();
            if (Random.Shared.NextDouble() < 0.4)
                ShowMessage("我去旁边走走，马上回来～", 3.0);
            return;
        }

        if (roll < 0.40)
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

        var line = GetAmbientLineForAction(actionKey);
        if (line is not null && Random.Shared.NextDouble() < 0.72)
        {
            ShowMessage(line, 3.8);
            return;
        }

        if (Random.Shared.NextDouble() < 0.42)
            ShowRandomLoveMessage();
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

        StopWalking(recover: false);
        _mouseDownClickCount = e.ClickCount;
        _dragMoved = false;
        _isDragging = true;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        PetImage.CaptureMouse();
        e.Handled = true;
    }

    private void PetImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (current.X - _dragStartScreen.X) / dpi.DpiScaleX;
        var deltaY = (current.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
        if (!_dragMoved && Math.Abs(deltaX) + Math.Abs(deltaY) > 5)
        {
            _dragMoved = true;
            PlayAction("dragged");
        }
        if (!_dragMoved)
            return;

        Left = _dragStartLeft + deltaX;
        Top = _dragStartTop + deltaY;
    }

    private void PetImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        var localPoint = e.GetPosition(PetImage);
        _isDragging = false;
        PetImage.ReleaseMouseCapture();

        if (_dragMoved)
        {
            ScreenService.ClampToWorkingArea(this);
            AddAffection(1);
            PlayAction("recover");
            SavePosition();
        }
        else if (_mouseDownClickCount >= 2)
        {
            // 双击：比心亲亲
            AddAffection(4);
            PlayAction("heart");
            ShowMessage($"最喜欢{_settings.PartnerName}啦 ♥", 3.2);
            PlaySound(SystemSounds.Asterisk);
            ResetRapidClicks();
        }
        else
        {
            HandleBodyZoneClick(localPoint);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 按角色区域反馈：头部摸头、脚底跳脚、身体随机互动；短时间连点会触发大笑彩蛋。
    /// </summary>
    private void HandleBodyZoneClick(Point localPoint)
    {
        var height = Math.Max(1, PetImage.ActualHeight);
        var ratioY = localPoint.Y / height;
        RegisterRapidClick();

        if (_rapidClickCount >= 5)
        {
            ResetRapidClicks();
            AddAffection(5);
            PlayAction("laugh");
            ShowMessage("哈哈哈哈，你点得好认真！", 3.6);
            PlaySound(SystemSounds.Asterisk);
            return;
        }

        if (ratioY < 0.42)
        {
            // 头部区域：摸头
            AddAffection(3);
            PlayAction("headpat");
            ShowMessage("再摸一下嘛，我很乖的。", 3.2);
            PlaySound(SystemSounds.Beep);
        }
        else if (ratioY > 0.78)
        {
            // 脚底区域：跳脚 / 惊讶
            AddAffection(2);
            var footActions = new[] { "surprised", "jump", "pout" };
            PlayAction(footActions[Random.Shared.Next(footActions.Length)]);
            ShowMessage("嘿！脚底很痒啦～", 3.0);
            PlaySound(SystemSounds.Beep);
        }
        else
        {
            // 身体区域：随机互动
            AddAffection(1);
            var actions = new[] { "wave", "shy", "jump", "curious", "clap", "kneel", "plush" };
            PlayAction(actions[Random.Shared.Next(actions.Length)]);
            ShowRandomLoveMessage();
        }
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
        // 中键：打开离线聊天窗口
        if (e.ChangedButton == MouseButton.Middle)
        {
            OpenChat();
            e.Handled = true;
        }
    }

    private void OpenChat()
    {
        EnsureVisible();
        if (_chatWindow is { IsLoaded: true, IsVisible: true })
        {
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
            });

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
            // 优先放在桌宠左侧，空间不够则放到右侧。
            var preferredLeft = Left - chat.Width - 12;
            if (preferredLeft < area.Left)
                preferredLeft = Left + Width + 12;
            chat.Left = Math.Clamp(preferredLeft, area.Left, Math.Max(area.Left, area.Right - chat.Width));
            chat.Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - chat.Height));
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
        if (!_isDragging)
            return;
        _isDragging = false;
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
        if (_dragMoved)
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
        CheckSpecialDate(force: false);
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
            StopWalking(recover: false);
            _actionTimer.Stop();
            _movementTimer.Stop();
            _behaviorTimer.Stop();
            StopSpriteAnimations();
            Opacity = 0;
            PetRoot.IsHitTestVisible = false;
        }
        else
        {
            Opacity = _introDone ? 1 : Opacity;
            PetRoot.IsHitTestVisible = true;
            if (!_behaviorTimer.IsEnabled && _introDone)
                _behaviorTimer.Start();
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
            NativeWindowService.SetClickThrough(this, _settings.ClickThrough);
            SaveSettings();
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
        AddMenuItem(_trayMenu.Items, "关于", (_, _) => ShowAbout());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        AddMenuItem(_trayMenu.Items, "完全退出", (_, _) => ExitApplication());

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
        Opacity = 1;
        PetRoot.IsHitTestVisible = true;
        if (!_behaviorTimer.IsEnabled && _introDone)
            _behaviorTimer.Start();
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
                            NativeWindowService.SetClickThrough(this, _settings.ClickThrough);
                            UpdateTrayChecks();
                            SaveSettings();
                            ShowMessage(_settings.ClickThrough ? "已开启鼠标穿透。" : "已关闭鼠标穿透，可以点我啦。", 2.8);
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
            "托盘：与上述功能一致，隐藏后请从托盘找回。",
            "快捷键说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenSettings()
    {
        StopWalking();
        var settingsWindow = new SettingsWindow(_settings)
        {
            Topmost = Topmost,
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

    private void ShowAbout()
    {
        var days = Math.Max(1, (DateTime.Today - _settings.FirstMetDate.Date).Days + 1);
        MessageBox.Show(
            $"小申陪伴 1.1\n\n已经陪伴 {days} 天\n互动 {_settings.InteractionCount} 次\n爱心值 {_settings.Affection}\n\n中键或托盘「和我聊聊」打开多模态 Agent。\n在线不可用时自动切换本地中文陪伴。\n纪念日与设置保存在本机。",
            "关于小申陪伴", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (IsLoaded)
        {
            _settings.LastLeft = Left;
            _settings.LastTop = Top;
        }
        SaveSettings();
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
