using Microsoft.Win32;
using Soundforge.Settings;
using System.IO;
using System.Windows;

namespace Soundforge;

public partial class AutosaveSettingsWindow : Window
{
    private readonly string _defaultFolder;

    public bool AutosaveEnabled { get; private set; }
    public int AutosaveIntervalMinutes { get; private set; }
    public string AutosaveFolder { get; private set; } = "";

    public AutosaveSettingsWindow(AppSettings settings, string defaultFolder)
    {
        InitializeComponent();
        _defaultFolder = defaultFolder;
        EnabledCheckBox.IsChecked = settings.AutosaveEnabled;
        IntervalText.Text = Math.Clamp(settings.AutosaveIntervalMinutes, 1, 60).ToString();
        FolderText.Text = string.IsNullOrWhiteSpace(settings.AutosaveFolder)
            ? defaultFolder
            : settings.AutosaveFolder;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Soundforge autosave folder",
            InitialDirectory = Directory.Exists(FolderText.Text) ? FolderText.Text : _defaultFolder
        };
        if (dialog.ShowDialog() == true)
            FolderText.Text = dialog.FolderName;
    }

    private void DefaultFolder_Click(object sender, RoutedEventArgs e) =>
        FolderText.Text = _defaultFolder;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalText.Text.Trim(), out var minutes) || minutes is < 1 or > 60)
        {
            ValidationText.Text = "Choose an autosave interval between 1 and 60 minutes.";
            return;
        }

        var folder = FolderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            ValidationText.Text = "Choose a complete autosave folder path.";
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ValidationText.Text = $"Soundforge cannot use that folder: {ex.Message}";
            return;
        }

        AutosaveEnabled = EnabledCheckBox.IsChecked == true;
        AutosaveIntervalMinutes = minutes;
        AutosaveFolder = folder;
        DialogResult = true;
    }
}
