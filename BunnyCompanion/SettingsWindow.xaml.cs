using System.Windows;
using BunnyCompanion.Models;
using BunnyCompanion.Services;
using MessageBox = System.Windows.MessageBox;

namespace BunnyCompanion;

public partial class SettingsWindow : Window
{
    private readonly PetSettings _settings;

    public SettingsWindow(PetSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Populate(settings);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var area = ScreenService.GetWorkingArea(Owner ?? this);
            var maxWidth = Math.Max(360, area.Width - 32);
            var maxHeight = Math.Max(360, area.Height - 32);
            MinWidth = Math.Min(MinWidth, maxWidth);
            MinHeight = Math.Min(MinHeight, maxHeight);
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
            Width = Math.Min(Width, maxWidth);
            Height = Math.Min(Height, maxHeight);
            ScreenService.ClampToWorkingArea(this);
        }
        catch
        {
            // 尺寸适配失败不影响设置读写。
        }
    }

    private void Populate(PetSettings settings)
    {
        PetNameBox.Text = settings.PetName;
        PartnerNameBox.Text = settings.PartnerName;
        ScaleSlider.Value = settings.Scale;
        StartupCheck.IsChecked = settings.StartWithWindows;
        AutoWalkCheck.IsChecked = settings.AutoWalk;
        TopmostCheck.IsChecked = settings.AlwaysOnTop;
        SoundCheck.IsChecked = settings.SoundEnabled;
        BubbleCheck.IsChecked = settings.ShowSpeechBubbles;
        QuietModeCheck.IsChecked = settings.QuietMode;
        FullscreenCheck.IsChecked = settings.HideForFullscreen;
        ClickThroughCheck.IsChecked = settings.ClickThrough;
        TtsCheck.IsChecked = settings.TtsEnabled;
        VoiceInputCheck.IsChecked = settings.VoiceInputEnabled;
        AutoUpdateCheck.IsChecked = settings.AutoCheckUpdate;
        var triggers = settings.SystemTriggers ?? new SystemTriggerConfig();
        SystemTriggerCheck.IsChecked = triggers.Enabled;
        CpuThresholdBox.Text = Math.Round(triggers.HighCpuThreshold).ToString();
        MemoryThresholdBox.Text = Math.Round(triggers.HighMemoryThreshold).ToString();
        BatteryThresholdBox.Text = triggers.LowBatteryThreshold.ToString();
        IdleMinutesBox.Text = Math.Round(triggers.IdleTooLongSeconds / 60).ToString();
        TriggerCooldownBox.Text = Math.Max(1, Math.Round(triggers.CooldownSeconds / 60)).ToString();
        WaterReminderBox.Text = settings.WaterReminderMinutes.ToString();
        RestReminderBox.Text = settings.RestReminderMinutes.ToString();
        QuietStartBox.Text = settings.QuietStart;
        QuietEndBox.Text = settings.QuietEnd;
        BirthdayPicker.SelectedDate = settings.Birthday;
        AnniversaryPicker.SelectedDate = settings.Anniversary;
        MessagesBox.Text = string.Join(Environment.NewLine, settings.LoveMessages);
        UpdateScaleText();
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateScaleText();

    private void UpdateScaleText()
    {
        if (ScaleValueText is not null)
            ScaleValueText.Text = $"{ScaleSlider.Value:P0}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRange(WaterReminderBox, "喝水提醒", 0, 240, out var waterMinutes)
            || !TryReadRange(RestReminderBox, "休息提醒", 0, 240, out var restMinutes)
            || !TryReadRange(CpuThresholdBox, "CPU 提醒阈值", 0, 100, out var cpuThreshold)
            || !TryReadRange(MemoryThresholdBox, "内存提醒阈值", 0, 100, out var memoryThreshold)
            || !TryReadRange(BatteryThresholdBox, "低电量提醒阈值", 0, 100, out var batteryThreshold)
            || !TryReadRange(IdleMinutesBox, "久离时间", 0, 1440, out var idleMinutes)
            || !TryReadRange(TriggerCooldownBox, "提醒冷却", 1, 1440, out var cooldownMinutes))
            return;

        if (!TimeOnly.TryParse(QuietStartBox.Text, out _)
            || !TimeOnly.TryParse(QuietEndBox.Text, out _))
        {
            MessageBox.Show("安静时段请使用 24 小时格式，例如 23:00 和 08:00。",
                "请检查设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.PetName = PetNameBox.Text;
        _settings.PartnerName = PartnerNameBox.Text;
        _settings.Scale = ScaleSlider.Value;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
        _settings.AutoWalk = AutoWalkCheck.IsChecked == true;
        _settings.AlwaysOnTop = TopmostCheck.IsChecked == true;
        _settings.SoundEnabled = SoundCheck.IsChecked == true;
        _settings.ShowSpeechBubbles = BubbleCheck.IsChecked == true;
        _settings.QuietMode = QuietModeCheck.IsChecked == true;
        _settings.HideForFullscreen = FullscreenCheck.IsChecked == true;
        _settings.ClickThrough = ClickThroughCheck.IsChecked == true;
        _settings.TtsEnabled = TtsCheck.IsChecked == true;
        _settings.VoiceInputEnabled = VoiceInputCheck.IsChecked == true;
        _settings.AutoCheckUpdate = AutoUpdateCheck.IsChecked == true;
        _settings.SystemTriggers ??= new SystemTriggerConfig();
        _settings.SystemTriggers.Enabled = SystemTriggerCheck.IsChecked == true;
        _settings.SystemTriggers.HighCpuThreshold = cpuThreshold;
        _settings.SystemTriggers.HighMemoryThreshold = memoryThreshold;
        _settings.SystemTriggers.LowBatteryThreshold = batteryThreshold;
        _settings.SystemTriggers.IdleTooLongSeconds = idleMinutes * 60.0;
        _settings.SystemTriggers.CooldownSeconds = cooldownMinutes * 60.0;
        _settings.WaterReminderMinutes = waterMinutes;
        _settings.RestReminderMinutes = restMinutes;
        _settings.QuietStart = QuietStartBox.Text.Trim();
        _settings.QuietEnd = QuietEndBox.Text.Trim();
        _settings.Birthday = BirthdayPicker.SelectedDate;
        _settings.Anniversary = AnniversaryPicker.SelectedDate;
        _settings.LoveMessages = MessagesBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(80)
            .ToList();
        _settings.Normalize();

        DialogResult = true;
    }

    private static bool TryReadRange(
        System.Windows.Controls.TextBox box,
        string label,
        int minimum,
        int maximum,
        out int value)
    {
        if (int.TryParse(box.Text, out value) && value >= minimum && value <= maximum)
            return true;
        MessageBox.Show($"{label}需要填写 {minimum} 至 {maximum} 之间的整数。",
            "请检查设置", MessageBoxButton.OK, MessageBoxImage.Information);
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new PetSettings
        {
            PetName = PetNameBox.Text,
            PartnerName = PartnerNameBox.Text,
            Birthday = BirthdayPicker.SelectedDate,
            Anniversary = AnniversaryPicker.SelectedDate,
        };
        Populate(defaults);
    }
}
