using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Soundforge.Audio;
using Soundforge.Models;

namespace Soundforge.Cloud;

public sealed class CloudAudioCache : IDisposable
{
    private const long MaximumDownloadBytes = 20L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".aiff", ".aif", ".ogg" };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly IAddressResolver _addresses;

    public CloudAudioCache(HttpMessageHandler? handler = null, IAddressResolver? addresses = null)
    {
        _addresses = addresses ?? new SystemAddressResolver();
        _ownsClient = true;
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Soundforge/0.9a.3");
    }
    internal CloudAudioCache(HttpClient client) { _client = client; _ownsClient = false; _addresses = new SystemAddressResolver(); }
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "data", "CloudSources");

    public async Task<CachedCloudSource> GetOrDownloadAsync(string sourceUrl, string? displayName, string cacheFolder,
        IProgress<CloudDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = CloudSourceAddress.Parse(sourceUrl);
        Directory.CreateDirectory(cacheFolder);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.OriginalUri.AbsoluteUri)));
        var existing = Directory.EnumerateFiles(cacheFolder, $"{key}.*").FirstOrDefault(path => AudioExtensions.Contains(Path.GetExtension(path)));
        if (existing is not null)
        {
            ValidateAudio(existing);
            return new(existing, ChooseName(displayName, Path.GetFileName(existing)), source.Kind, source.OriginalUri.AbsoluteUri, source.ProviderId, null, null, true);
        }

        var temporaryPath = Path.Combine(cacheFolder, $"{key}.{Guid.NewGuid():N}.download");
        string? validationPath = null;
        try
        {
            using var response = await SendFollowingRedirectsAsync(source.DownloadUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException(source.Kind == AudioSourceKind.GoogleDrive
                    ? "Google returned a web page instead of audio. Make the file publicly downloadable, or wait for Google account support."
                    : "The address returned a web page rather than an audio file. Use a direct downloadable audio URL.");
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumDownloadBytes) throw new InvalidDataException("This audio file exceeds Soundforge's 20 GB per-source safety limit.");
            if (length is > 0 && new DriveInfo(Path.GetPathRoot(cacheFolder)!).AvailableFreeSpace < length + 512L * 1024 * 1024)
                throw new IOException("There is not enough free space to cache this source safely.");
            var extension = ChooseExtension(response.Content.Headers, source.OriginalUri);
            var suggestedName = ChooseName(displayName, GetResponseFileName(response.Content.Headers) ?? Path.GetFileName(Uri.UnescapeDataString(source.OriginalUri.AbsolutePath)));

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > MaximumDownloadBytes) throw new InvalidDataException("This audio file exceeds Soundforge's 20 GB per-source safety limit.");
                    if (copied == read && LooksLikeHtml(buffer.AsSpan(0, Math.Min(read, 512))))
                        throw new InvalidDataException(source.Kind == AudioSourceKind.GoogleDrive
                            ? "Google returned a web page instead of audio. Make the file publicly downloadable, or wait for Google account support."
                            : "The address returned a web page rather than an audio file. Use a direct downloadable audio URL.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new("Downloading audio…", suggestedName, copied, length));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (copied == 0) throw new InvalidDataException("The download contained no audio data.");
                if (length.HasValue && copied != length.Value) throw new InvalidDataException("The download ended before the complete file arrived.");
            }

            var finalPath = Path.Combine(cacheFolder, key + extension);
            validationPath = temporaryPath + extension;
            File.Move(temporaryPath, validationPath);
            ValidateAudio(validationPath);
            File.Move(validationPath, finalPath, overwrite: true);
            progress?.Report(new("Available offline", suggestedName, length ?? 1, length ?? 1));
            return new(finalPath, suggestedName, source.Kind, source.OriginalUri.AbsoluteUri, source.ProviderId,
                response.Headers.ETag?.Tag, response.Content.Headers.LastModified, false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (validationPath is not null && File.Exists(validationPath)) File.Delete(validationPath);
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            await EnsurePublicHttpsAddressAsync(current, cancellationToken).ConfigureAwait(false);
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, current), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                response.Dispose(); current = next; continue;
            }
            return response;
        }
        throw new HttpRequestException("The download redirected too many times.");
    }

    private async Task EnsurePublicHttpsAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Soundforge only downloads HTTPS audio addresses.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local and private network addresses cannot be used as audio URLs.");
        var addresses = await _addresses.ResolveAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
            throw new InvalidDataException("Local and private network addresses cannot be used as audio URLs.");
    }
    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
        }
        if (address.IsIPv4MappedToIPv6) return IsPrivateAddress(address.MapToIPv4());
        var ipv6 = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Any) ||
               address.Equals(IPAddress.IPv6None) || ipv6[0] == 0xFF || (ipv6[0] & 0xFE) == 0xFC;
    }

    private static string ChooseExtension(HttpContentHeaders headers, Uri source)
    {
        var extension = Path.GetExtension(GetResponseFileName(headers) ?? source.AbsolutePath);
        if (AudioExtensions.Contains(extension)) return extension.ToLowerInvariant();
        return headers.ContentType?.MediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/aiff" or "audio/x-aiff" => ".aiff",
            "audio/ogg" or "application/ogg" => ".ogg",
            _ => throw new InvalidDataException("Soundforge could not identify a supported audio format from this download.")
        };
    }
    private static string? GetResponseFileName(HttpContentHeaders headers) => headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"');
    private static string ChooseName(string? requested, string? suggested) => !string.IsNullOrWhiteSpace(requested) ? requested.Trim() : !string.IsNullOrWhiteSpace(suggested) ? suggested : "Cloud audio";
    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart();
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateAudio(string path)
    {
        using var reader = new LocalAudioReader(path);
        if (reader.TotalTime <= TimeSpan.Zero) throw new InvalidDataException("The downloaded file contains no playable audio.");
        var buffer = new float[Math.Max(1, reader.WaveFormat.Channels * 32)];
        if (reader.Read(buffer, 0, buffer.Length) == 0) throw new InvalidDataException("The downloaded file contains no playable audio.");
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

public sealed record CachedCloudSource(string LocalPath, string DisplayName, AudioSourceKind Kind, string SourceUri,
    string? ProviderId, string? ETag, DateTimeOffset? ModifiedUtc, bool ReusedCache);
public sealed record CloudDownloadProgress(string Stage, string Name, long DownloadedBytes, long? TotalBytes);

public sealed record CloudSourceAddress(Uri OriginalUri, Uri DownloadUri, AudioSourceKind Kind, string? ProviderId)
{
    public static CloudSourceAddress Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Enter a complete HTTPS audio address.");
        if (TryGetGoogleDriveId(uri, out var id, out var resourceKey))
        {
            var resourceKeyQuery = string.IsNullOrWhiteSpace(resourceKey)
                ? string.Empty
                : $"&resourcekey={Uri.EscapeDataString(resourceKey)}";
            return new(uri, new Uri($"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t{resourceKeyQuery}"), AudioSourceKind.GoogleDrive, id);
        }
        return new(uri, uri, AudioSourceKind.HttpUrl, null);
    }
    private static bool TryGetGoogleDriveId(Uri uri, out string id, out string resourceKey)
    {
        id = "";
        resourceKey = "";
        if (!uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".drive.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(segments, item => item.Equals("d", StringComparison.OrdinalIgnoreCase));
        if (marker >= 0 && marker + 1 < segments.Length) id = segments[marker + 1];
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (string.IsNullOrWhiteSpace(id) && pair[0].Equals("id", StringComparison.OrdinalIgnoreCase))
                id = Uri.UnescapeDataString(pair[1]);
            if (pair[0].Equals("resourcekey", StringComparison.OrdinalIgnoreCase))
                resourceKey = Uri.UnescapeDataString(pair[1]);
        }
        return id.Length is >= 10 and <= 200 && id.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    }
}
