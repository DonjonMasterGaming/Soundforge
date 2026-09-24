using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Soundforge.Cloud.Google;

public sealed class LoopbackAuthorizationBroker(Action<Uri>? launchBrowser = null) : IAuthorizationBroker
{
    public Uri? RedirectUri { get; private set; }

    public async Task<string> AuthorizeAsync(Func<Uri, Uri> authorizationUri, string state, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/oauth2callback");
        var target = authorizationUri(RedirectUri);
        if (launchBrowser is not null) launchBrowser(target);
        else Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            try
            {
                // Bound header size and time; don't use HttpListener URL reservations under Wine.
                var buffer = new byte[16384];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length, 1), requestTimeout.Token);
                    if (read == 0) break;
                    length += read;
                    if (length >= 4 && buffer[length - 4] == 13 && buffer[length - 3] == 10 &&
                        buffer[length - 2] == 13 && buffer[length - 1] == 10) break;
                }
                var header = Encoding.ASCII.GetString(buffer, 0, length);
                var lines = header.Split("\r\n");
                var parts = lines[0].Split(' ');
                var host = lines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
                if (!header.EndsWith("\r\n\r\n") || parts.Length != 3 || parts[0] != "GET" ||
                    !parts[1].StartsWith("/oauth2callback?", StringComparison.Ordinal) ||
                    host != RedirectUri.Authority)
                {
                    await RespondAsync(stream, "400 Bad Request", "Invalid callback request.", requestTimeout.Token);
                    continue;
                }
                var query = ParseQuery(new Uri(RedirectUri, parts[1]).Query);
                if (!query.TryGetValue("state", out var receivedState) || receivedState != state)
                {
                    await RespondAsync(stream, "400 Bad Request", "Invalid sign-in state. Return to Soundforge.", requestTimeout.Token);
                    continue;
                }
                if (query.ContainsKey("error"))
                {
                    await RespondAsync(stream, "200 OK", "Sign-in was declined. Return to Soundforge.", requestTimeout.Token);
                    throw new InvalidOperationException("Google sign-in was declined. No credentials were changed.");
                }
                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await RespondAsync(stream, "400 Bad Request", "Missing authorization code.", requestTimeout.Token);
                    continue;
                }
                await RespondAsync(stream, "200 OK", "Authorization received. Return to Soundforge to check connection status.", requestTimeout.Token);
                return code;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); }
            catch (FormatException) { }
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new FormatException("Duplicate callback parameter.");
        }
        return result;
    }

    private static async Task RespondAsync(Stream stream, string status, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
    }
}
