using System.Windows;
using BunnyCompanion.Models;
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
        if (!int.TryParse(WaterReminderBox.Text, out var waterMinutes)
            || !int.TryParse(RestReminderBox.Text, out var restMinutes))
        {
            MessageBox.Show("提醒间隔需要填写 0 至 240 之间的整数。",
                "请检查设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
        _settings.WaterReminderMinutes = Math.Clamp(waterMinutes, 0, 240);
        _settings.RestReminderMinutes = Math.Clamp(restMinutes, 0, 240);
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
