using System.IO;
using System.Text.Json;

namespace Soundforge.Cloud.Google;

public sealed record GoogleAccount(string Subject, string DisplayName);
public sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record GoogleCredentials(string ClientId, GoogleAccount Account, GoogleTokens Tokens);

public interface ICredentialStore
{
    Task<GoogleCredentials?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(GoogleCredentials credentials, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}

public interface IGoogleAccountProvider
{
    Task<GoogleAccount> ConnectAsync(CancellationToken cancellationToken);
    Task<GoogleAccount?> ReconnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public interface IAuthorizationBroker
{
    Task<string> AuthorizeAsync(Func<Uri, Uri> authorizationUri, string state, CancellationToken cancellationToken);
    Uri? RedirectUri { get; }
}

public sealed record GoogleClientConfiguration(string ClientId, string? ClientSecret)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Soundforge", "auth", "oauth-client.json");

    public static GoogleClientConfiguration Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("installed", out var installed))
            throw new InvalidDataException("Select Google's Desktop app client JSON (not a Web client).");
        var id = installed.GetProperty("client_id").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("The OAuth client ID is missing.");
        return new(id, installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() : null);
    }
}
