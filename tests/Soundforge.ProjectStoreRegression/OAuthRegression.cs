using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soundforge.Cloud.Google;

internal static class OAuthRegression
{
    public static async Task Run(string root)
    {
        Check(GoogleOAuthService.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") ==
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE RFC vector");
        var broker = new FakeBroker();
        var store = new SessionCredentialStore();
        var handler = new FakeGoogle(broker);
        using var http = new HttpClient(handler);
        var service = new GoogleOAuthService(http, new("fixture-client", "fixture-client-secret"), broker, store);
        var account = await service.ConnectAsync(CancellationToken.None);
        Check(account.Subject == "stable-subject", "verified UserInfo identity");
        var initial = await store.LoadAsync(CancellationToken.None);
        Check(initial?.Tokens.RefreshToken == "fixture-refresh", "refresh token captured");
        await service.ReconnectAsync(CancellationToken.None);
        Check(handler.Refreshes == 1 && (await store.LoadAsync(CancellationToken.None))?.Tokens.RefreshToken == "fixture-refresh",
            "refresh response without refresh_token preserves existing refresh token");
        handler.Fail = true;
        await Expect<InvalidOperationException>(() => service.ReconnectAsync(CancellationToken.None));
        Check((await store.LoadAsync(CancellationToken.None))?.Tokens.RefreshToken == "fixture-refresh", "failed refresh preserves store");
        handler.Fail = false;
        handler.Subject = "different-account";
        await Expect<InvalidOperationException>(() => service.ReconnectAsync(CancellationToken.None));
        Check((await store.LoadAsync(CancellationToken.None))?.Account.Subject == "stable-subject", "account mismatch preserves store");
        handler.Subject = "stable-subject";
        await service.ConnectAsync(CancellationToken.None);
        Check(broker.DifferentStateAndChallenge, "fresh random state and verifier");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => service.ConnectAsync(cancelled.Token));
        }
        await service.DisconnectAsync(CancellationToken.None);
        Check(await store.LoadAsync(CancellationToken.None) is null, "disconnect removes session credentials");
        var timeoutService = new GoogleOAuthService(http, new("fixture-client", null), new WaitingBroker(), store,
            authorizationTimeout: TimeSpan.FromMilliseconds(50));
        await Expect<OperationCanceledException>(() => timeoutService.ConnectAsync(CancellationToken.None));
        Check(await store.LoadAsync(CancellationToken.None) is null, "authorization timeout does not persist credentials");

        var path = Path.Combine(root, "credentials.bin");
        var encrypted = new EncryptedCredentialStore(path, CredentialProtection.Passphrase, () => "long-fixture-passphrase");
        await encrypted.SaveAsync(initial!, CancellationToken.None);
        var raw = File.ReadAllText(path);
        Check(!raw.Contains("fixture-refresh") && !raw.Contains("fixture-access") && !raw.Contains("stable-subject"), "encrypted at rest");
        var reopened = new EncryptedCredentialStore(path, CredentialProtection.Passphrase, () => "long-fixture-passphrase");
        Check((await reopened.LoadAsync(CancellationToken.None)) == initial, "encrypted persistence across instances");
        await Expect<CryptographicException>(() => new EncryptedCredentialStore(path, CredentialProtection.Passphrase,
            () => "wrong-fixture-passphrase").LoadAsync(CancellationToken.None));
        await Expect<InvalidOperationException>(() => new EncryptedCredentialStore(path, CredentialProtection.WindowsUser).LoadAsync(CancellationToken.None));
        var previous = File.ReadAllBytes(path);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => encrypted.SaveAsync(initial!, cancelled.Token));
        }
        Check(Convert.ToHexString(previous) == Convert.ToHexString(File.ReadAllBytes(path)), "cancelled save preserves credentials");
        using (var envelope = JsonDocument.Parse(raw))
        {
            var data = Convert.FromBase64String(envelope.RootElement.GetProperty("Data").GetString()!);
            data[0] ^= 1;
            File.WriteAllText(path, raw.Replace(envelope.RootElement.GetProperty("Data").GetString()!, Convert.ToBase64String(data)));
        }
        await Expect<CryptographicException>(() => encrypted.LoadAsync(CancellationToken.None));
        await encrypted.DeleteAsync(CancellationToken.None);
        Check(!File.Exists(path), "disconnect deletes encrypted credentials");
        var protectedStore = new EncryptedCredentialStore(path, CredentialProtection.WindowsUser);
        await protectedStore.SaveAsync(initial!, CancellationToken.None);
        Check((await protectedStore.LoadAsync(CancellationToken.None)) == initial, "Windows protected roundtrip");
        await protectedStore.DeleteAsync(CancellationToken.None);

        await CheckLoopback();
        Console.WriteLine("OAuth PKCE, random state, callback, cancellation, refresh, account binding, encrypted persistence and failure-preservation regressions passed.");
    }

    private static async Task CheckLoopback()
    {
        var launched = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new LoopbackAuthorizationBroker(uri => launched.SetResult(uri));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = broker.AuthorizeAsync(redirect => new Uri("https://example.invalid/?redirect_uri=" + Uri.EscapeDataString(redirect.AbsoluteUri)), "expected-state", deadline.Token);
        await launched.Task;
        var redirect = broker.RedirectUri!;
        Check(redirect.Host == "127.0.0.1" && redirect.Port > 0, "IPv4 loopback callback");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        using var wrong = await http.GetAsync(redirect + "?state=wrong&code=bad", deadline.Token);
        Check(wrong.StatusCode == HttpStatusCode.BadRequest && !pending.IsCompleted, "wrong state ignored");
        using var good = await http.GetAsync(redirect + "?state=expected-state&code=fixture-code", deadline.Token);
        Check(await pending == "fixture-code", "valid callback accepted");
        using var closed = new TcpClient();
        await Expect<SocketException>(async () => await closed.ConnectAsync(IPAddress.Loopback, redirect.Port));

        var cancelling = new LoopbackAuthorizationBroker(_ => { });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Expect<OperationCanceledException>(() => cancelling.AuthorizeAsync(_ => new Uri("https://example.invalid"), "state", cancel.Token));

        var denyLaunch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var denied = new LoopbackAuthorizationBroker(_ => denyLaunch.SetResult(true));
        var deniedTask = denied.AuthorizeAsync(_ => new Uri("https://example.invalid"), "state", deadline.Token);
        await denyLaunch.Task;
        using var denialResponse = await http.GetAsync(denied.RedirectUri + "?state=state&error=access_denied", deadline.Token);
        await Expect<InvalidOperationException>(() => deniedTask);
        try { LoopbackAuthorizationBroker.ParseQuery("state=a&state=b"); throw new Exception("duplicate accepted"); }
        catch (FormatException) { }
    }

    private static void Check(bool value, string name) { if (!value) throw new Exception("OAuth regression: " + name); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class WaitingBroker : IAuthorizationBroker
    {
        public Uri? RedirectUri => null;
        public async Task<string> AuthorizeAsync(Func<Uri, Uri> build, string state, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return "unreachable"; }
    }

    private sealed class FakeBroker : IAuthorizationBroker
    {
        public Uri? RedirectUri { get; } = new("http://127.0.0.1:54321/oauth2callback");
        public string? Challenge;
        private string? _state;
        public bool DifferentStateAndChallenge;
        public Task<string> AuthorizeAsync(Func<Uri, Uri> build, string state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = build(RedirectUri!);
            var query = LoopbackAuthorizationBroker.ParseQuery(uri.Query);
            Check(uri.Host == "accounts.google.com" && query["code_challenge_method"] == "S256" && query["state"] == state &&
                query["access_type"] == "offline" && query["scope"].Contains(GoogleOAuthService.DriveScope), "authorization request");
            DifferentStateAndChallenge = _state is not null && _state != state && Challenge != query["code_challenge"];
            _state = state; Challenge = query["code_challenge"];
            return Task.FromResult("fixture-code");
        }
    }
    private sealed class FakeGoogle(FakeBroker broker) : HttpMessageHandler
    {
        public int Refreshes;
        public bool Fail;
        public string Subject = "stable-subject";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("sensitive error must not be surfaced") };
            if (request.RequestUri!.Host == "openidconnect.googleapis.com")
            {
                Check(request.Headers.Authorization?.Parameter == "fixture-access", "bearer authorization");
                return Json(new { sub = Subject, email = "fixture@example.invalid" });
            }
            Check(request.RequestUri.AbsoluteUri == "https://oauth2.googleapis.com/token", "fixed token endpoint");
            var form = LoopbackAuthorizationBroker.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form["grant_type"] == "authorization_code")
            {
                Check(form["code"] == "fixture-code" && GoogleOAuthService.Challenge(form["code_verifier"]) == broker.Challenge, "code exchange binds PKCE");
                return Json(new { access_token = "fixture-access", refresh_token = "fixture-refresh", token_type = "Bearer", expires_in = 3600, scope = "openid email " + GoogleOAuthService.DriveScope });
            }
            Check(form["refresh_token"] == "fixture-refresh", "refresh request");
            Refreshes++;
            return Json(new { access_token = "fixture-access", token_type = "Bearer", expires_in = 3600 });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
