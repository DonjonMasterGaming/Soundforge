using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using Soundforge.Cloud.Google;

namespace Soundforge;

public partial class GoogleConnectionWindow : Window
{
    private CancellationTokenSource? _operation;
    private readonly SessionCredentialStore _session = new();
    public GoogleConnectionWindow()
    {
        InitializeComponent();
        UpdateConfiguration();
        if (File.Exists(EncryptedCredentialStore.DefaultPath)) StatusText.Text = "Saved account locked — choose its storage method and reconnect.";
    }

    private void UpdateConfiguration() => ConfigurationText.Text = File.Exists(GoogleClientConfiguration.DefaultPath)
        ? "Desktop client configuration installed for this user." : "Import a Google Desktop client JSON before connecting.";

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Google Desktop client JSON|*.json", Title = "Select Google Desktop OAuth client configuration" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            GoogleClientConfiguration.Load(picker.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(GoogleClientConfiguration.DefaultPath)!);
            if (!Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(GoogleClientConfiguration.DefaultPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(picker.FileName, GoogleClientConfiguration.DefaultPath, true);
            UpdateConfiguration();
        }
        catch { StatusText.Text = "Could not import configuration. Select a valid Google Desktop app client JSON."; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(true);

    private async Task RunAsync(bool reconnect)
    {
        if (_operation is not null) return;
        var mode = StorageChoice.SelectedIndex;
        var passphrase = PassphraseBox.Password;
        if (mode == 0 && passphrase.Length < 12)
        { StatusText.Text = "Enter a credential passphrase of at least 12 characters."; return; }
        if (mode == 1 && IsWine())
        { StatusText.Text = "Windows user protection is disabled under Wine/Proton. Choose passphrase encryption or session-only storage."; return; }
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SetBusy(true);
        StatusText.Text = reconnect ? "Reconnecting saved account…" : "Waiting for browser sign-in… (five-minute timeout)";
        try
        {
            var config = GoogleClientConfiguration.Load(GoogleClientConfiguration.DefaultPath);
            ICredentialStore store = mode == 2 ? _session : new EncryptedCredentialStore(EncryptedCredentialStore.DefaultPath,
                mode == 0 ? CredentialProtection.Passphrase : CredentialProtection.WindowsUser, () => passphrase);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            var service = new GoogleOAuthService(http, config, new LoopbackAuthorizationBroker(), store);
            var token = _operation.Token;
            // Protect the WPF dispatcher from PBKDF work and filesystem encryption.
            var account = await Task.Run(async () => reconnect ? await service.ReconnectAsync(token) : await service.ConnectAsync(token), token);
            StatusText.Text = account is null ? "No saved account. Use Connect Google Drive." :
                $"Connected: {account.DisplayName}\n{(mode == 2 ? "Session only — tokens were not saved." : "Credentials saved encrypted. Reconnect after restarting to test persistence.")}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Connection cancelled or timed out. Existing saved credentials were retained."; }
        catch (CryptographicException) { StatusText.Text = "Could not unlock/protect credentials. Check your passphrase and storage method. No plaintext fallback was used."; }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        catch { StatusText.Text = "Connection failed. Check configuration, browser launch and internet access, then retry. Saved credentials were retained."; }
        finally
        {
            PassphraseBox.Clear();
            _operation.Dispose();
            _operation = null;
            SetBusy(false);
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.DeleteAsync(CancellationToken.None);
            await new EncryptedCredentialStore(EncryptedCredentialStore.DefaultPath, CredentialProtection.Passphrase).DeleteAsync(CancellationToken.None);
            PassphraseBox.Clear();
            StatusText.Text = "Disconnected. Local credentials removed; cached audio is unchanged. Google consent can be revoked in your Google account settings.";
        }
        catch { StatusText.Text = "Could not remove saved credentials. Check file permissions and retry."; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void SetBusy(bool busy)
    {
        ConfigureButton.IsEnabled = ConnectButton.IsEnabled = ReconnectButton.IsEnabled = DisconnectButton.IsEnabled =
            StorageChoice.IsEnabled = PassphraseBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operation is not null) { _operation.Cancel(); e.Cancel = true; }
        else { PassphraseBox.Clear(); _session.DeleteAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        base.OnClosing(e);
    }
    private static bool IsWine()
    {
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("ntdll.dll", out var library)) return false;
        try { return System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "wine_get_version", out _); }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(library); }
    }
}
