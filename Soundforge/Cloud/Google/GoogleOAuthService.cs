using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soundforge.Cloud.Google;

// No dependency on project models, audio, WPF or the Drive download cache.
public sealed class GoogleOAuthService(HttpClient http, GoogleClientConfiguration configuration,
    IAuthorizationBroker broker, ICredentialStore store, TimeProvider? timeProvider = null,
    TimeSpan? authorizationTimeout = null) : IGoogleAccountProvider
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.readonly";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task<GoogleAccount> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(authorizationTimeout ?? TimeSpan.FromMinutes(5));
            var token = timeout.Token;
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var code = await broker.AuthorizeAsync(redirect => new Uri("https://accounts.google.com/o/oauth2/v2/auth?" +
                Query(new Dictionary<string, string> {
                    ["client_id"] = configuration.ClientId, ["redirect_uri"] = redirect.AbsoluteUri,
                    ["response_type"] = "code", ["scope"] = "openid email " + DriveScope,
                    ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256",
                    ["state"] = state, ["access_type"] = "offline", ["prompt"] = "consent select_account"
                })), state, token);
            var tokens = await ExchangeAsync(new() {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = broker.RedirectUri!.AbsoluteUri, ["code_verifier"] = verifier
            }, null, token);
            var account = await GetAccountAsync(tokens.AccessToken, token);
            await store.SaveAsync(new(configuration.ClientId, account, tokens), token);
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task<GoogleAccount?> ReconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var saved = await store.LoadAsync(cancellationToken);
            if (saved is null) return null;
            if (saved.ClientId != configuration.ClientId)
                throw new InvalidOperationException("Saved credentials belong to a different OAuth client. Connect again.");
            // An explicit reconnect always exercises refresh, including the real-device persistence test.
            var tokens = await ExchangeAsync(new() { ["grant_type"] = "refresh_token",
                ["refresh_token"] = saved.Tokens.RefreshToken }, saved.Tokens.RefreshToken, cancellationToken);
            var account = await GetAccountAsync(tokens.AccessToken, cancellationToken);
            if (account.Subject != saved.Account.Subject)
                throw new InvalidOperationException("The refreshed account did not match the saved account. Connect again.");
            await store.SaveAsync(saved with { Account = account, Tokens = tokens }, cancellationToken);
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await store.DeleteAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<GoogleTokens> ExchangeAsync(Dictionary<string, string> fields, string? previousRefresh,
        CancellationToken cancellationToken)
    {
        fields["client_id"] = configuration.ClientId;
        if (!string.IsNullOrEmpty(configuration.ClientSecret)) fields["client_secret"] = configuration.ClientSecret;
        using var body = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google token request failed (HTTP {(int)response.StatusCode}). Reconnect or sign in again.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : previousRefresh;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ||
            !root.GetProperty("token_type").GetString()!.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Google did not supply usable offline credentials. Connect again and grant access.");
        if (root.TryGetProperty("scope", out var scope) && !scope.GetString()!.Split(' ').Contains(DriveScope))
            throw new InvalidOperationException("Google Drive read access was not granted. Connect again and enable Drive access.");
        var seconds = root.GetProperty("expires_in").GetInt32();
        if (seconds <= 0) throw new InvalidOperationException("Google returned expired credentials.");
        return new(access, refresh, _time.GetUtcNow().AddSeconds(seconds));
    }

    private async Task<GoogleAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Identity comes from Google's HTTPS UserInfo endpoint, never from an unverified decoded ID token.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google account verification failed. Please reconnect.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var subject = json.RootElement.GetProperty("sub").GetString();
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("Google returned no account identity.");
        var name = json.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        return new(subject, name ?? "Google account");
    }

    private static string Query(Dictionary<string, string> fields) => string.Join("&",
        fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
}
