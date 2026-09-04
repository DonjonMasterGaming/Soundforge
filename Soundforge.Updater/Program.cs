using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Soundforge.Updater;

internal static class Program
{
    private static readonly string InstallRecordPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundforge", "installation.json");

    [STAThread]
    public static void Main()
    {
        try
        {
            var packageRoot = AppContext.BaseDirectory;
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(packageRoot, "update.json")))
                ?? throw new InvalidDataException("The update information is invalid.");
            var payload = Path.Combine(packageRoot, manifest.PayloadFolder);
            var target = ResolveTarget(manifest);
            if (target is null) return;
            if (IsSoundforgeRunning(target))
            {
                MessageBox.Show("Save your project and close Soundforge, then run this update again.",
                    "Soundforge is open", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UpdateInstaller.Apply(payload, target);
            SaveInstallRecord(target);
            var launch = MessageBox.Show($"Soundforge {manifest.Version} is ready.\n\nYour settings, projects, caches and autosaves were not removed.\n\nLaunch Soundforge now?",
                "Update complete", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (launch == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "Soundforge.exe")) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Soundforge was not updated. Existing files were restored where necessary.\n\n{ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? ResolveTarget(UpdateManifest manifest)
    {
        var recorded = ReadInstallRecord();
        if (!string.IsNullOrWhiteSpace(recorded)) return recorded;
        if (!string.IsNullOrWhiteSpace(manifest.SuggestedExistingInstallPath) && Directory.Exists(manifest.SuggestedExistingInstallPath))
        {
            var keepPinned = MessageBox.Show("Soundforge found your existing installation. Update it in place so your pinned shortcut keeps working?",
                "Install Soundforge update", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (keepPinned == MessageBoxResult.Cancel) return null;
            if (keepPinned == MessageBoxResult.Yes) return Path.GetFullPath(manifest.SuggestedExistingInstallPath);
        }
        var defaultTarget = Directory.Exists("F:\\")
            ? @"F:\Folders\Useful Stuff\Visual Studio\Soundforge Installed"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Soundforge");
        var useDefault = MessageBox.Show($"Install Soundforge in this permanent location?\n\n{defaultTarget}\n\nFuture updates will reuse it automatically.",
            "Choose Soundforge location", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (useDefault == MessageBoxResult.Cancel) return null;
        if (useDefault == MessageBoxResult.Yes) return defaultTarget;
        var picker = new OpenFolderDialog { Title = "Choose the permanent Soundforge folder", InitialDirectory = Path.GetDirectoryName(defaultTarget) };
        return picker.ShowDialog() == true ? picker.FolderName : null;
    }

    private static bool IsSoundforgeRunning(string target)
    {
        var expected = Path.GetFullPath(Path.Combine(target, "Soundforge.exe"));
        foreach (var process in Process.GetProcessesByName("Soundforge"))
        {
            try { if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { return true; }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static string? ReadInstallRecord()
    {
        try { return File.Exists(InstallRecordPath) ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(InstallRecordPath))?.InstallPath : null; }
        catch { return null; }
    }
    private static void SaveInstallRecord(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstallRecordPath)!);
        var temporary = InstallRecordPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new InstallRecord(Path.GetFullPath(path)), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, InstallRecordPath, overwrite: true);
    }
    private sealed record UpdateManifest(string Version, string PayloadFolder, string? SuggestedExistingInstallPath);
    private sealed record InstallRecord(string InstallPath);
}
