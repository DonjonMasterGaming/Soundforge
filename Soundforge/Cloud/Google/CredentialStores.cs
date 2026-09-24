using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soundforge.Cloud.Google;

public sealed class SessionCredentialStore : ICredentialStore
{
    private GoogleCredentials? _credentials;
    public Task<GoogleCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_credentials);
    public Task SaveAsync(GoogleCredentials credentials, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); _credentials = credentials; return Task.CompletedTask; }
    public Task DeleteAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); _credentials = null; return Task.CompletedTask; }
}

public enum CredentialProtection { WindowsUser, Passphrase }

public sealed class EncryptedCredentialStore(string path, CredentialProtection protection, Func<string>? passphrase = null) : ICredentialStore
{
    private const int Iterations = 600_000;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Soundforge", "auth", "credentials.bin");

    public async Task<GoogleCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Invalid credential store.");
        if (envelope.Version != 1 || envelope.Protection != protection)
            throw new InvalidOperationException("Select the storage method used for the saved credentials, or disconnect to remove them.");
        byte[] plain;
        if (protection == CredentialProtection.WindowsUser)
            plain = ProtectedData.Unprotect(envelope.Data, null, DataProtectionScope.CurrentUser);
        else
        {
            if (envelope.Salt?.Length != 16 || envelope.Nonce?.Length != 12 || envelope.Tag?.Length != 16)
                throw new InvalidDataException("Invalid encrypted credential store.");
            var key = DeriveKey(envelope.Salt);
            try
            {
                plain = new byte[envelope.Data.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(envelope.Nonce, envelope.Data, envelope.Tag, plain, "Soundforge OAuth v1"u8);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        try { return JsonSerializer.Deserialize<GoogleCredentials>(plain) ?? throw new InvalidDataException("Invalid saved credentials."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(GoogleCredentials credentials, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        Envelope envelope;
        try
        {
            if (protection == CredentialProtection.WindowsUser)
                envelope = new(1, protection, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser), null, null, null);
            else
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var encrypted = new byte[plain.Length];
                var key = DeriveKey(salt);
                try
                {
                    using var aes = new AesGcm(key, 16);
                    aes.Encrypt(nonce, plain, encrypted, tag, "Soundforge OAuth v1"u8);
                }
                finally { CryptographicOperations.ZeroMemory(key); }
                envelope = new(1, protection, encrypted, salt, nonce, tag);
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, envelope, cancellationToken: cancellationToken);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); File.Delete(path); return Task.CompletedTask; }

    private byte[] DeriveKey(byte[] salt)
    {
        var secret = passphrase?.Invoke();
        if (string.IsNullOrEmpty(secret) || secret.Length < 12)
            throw new InvalidOperationException("Enter a credential passphrase of at least 12 characters. It is not your Google password.");
        return Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
    }

    private sealed record Envelope(int Version, CredentialProtection Protection, byte[] Data, byte[]? Salt, byte[]? Nonce, byte[]? Tag);
}
