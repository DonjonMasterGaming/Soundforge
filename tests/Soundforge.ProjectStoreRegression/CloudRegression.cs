using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Soundforge.Cloud;
using Soundforge.Models;

internal static class CloudRegression
{
    public static async Task Run(string root)
    {
        var batchUrls = CloudImportInput.ParseUrls("https://example.com/one.mp3, https://drive.google.com/file/d/1234567890abc/view\r\n\r\nhttps://example.com/one.mp3");
        if (batchUrls.Count != 2)
            throw new Exception("Batch cloud input did not ignore blank lines and duplicate links.");
        try
        {
            CloudImportInput.ParseUrls("https://example.com/good.mp3\nnot-a-url");
            throw new Exception("Batch cloud input accepted an invalid line.");
        }
        catch (InvalidDataException)
        {
        }

        var drive = CloudSourceAddress.Parse("https://drive.google.com/file/d/AbCdEfGhIjK_123456/view?usp=sharing");
        if (drive.Kind != AudioSourceKind.GoogleDrive || drive.ProviderId != "AbCdEfGhIjK_123456" ||
            drive.DownloadUri.Host != "drive.usercontent.google.com")
            throw new Exception("Google Drive share-link recognition failed.");
        var queryDrive = CloudSourceAddress.Parse("https://drive.google.com/open?id=ZyXwVuTsRqP-987654");
        if (queryDrive.ProviderId != "ZyXwVuTsRqP-987654") throw new Exception("Google Drive query-link recognition failed.");
        var protectedDrive = CloudSourceAddress.Parse("https://drive.google.com/file/d/AbCdEfGhIjK_123456/view?resourcekey=0-example_KEY");
        if (!protectedDrive.DownloadUri.Query.Contains("resourcekey=0-example_KEY", StringComparison.Ordinal))
            throw new Exception("Google Drive resource key was not preserved for download.");

        var audioPath = Path.Combine(root, "cloud-fixture.wav");
        using (var writer = new WaveFileWriter(audioPath, new WaveFormat(44100, 16, 2)))
            writer.Write(new byte[44100 * 4 / 10], 0, 44100 * 4 / 10);
        var audio = File.ReadAllBytes(audioPath);
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/final") } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "cloud-test.wav" };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-1\"");
            return response;
        });
        var cacheFolder = Path.Combine(root, "cloud-cache");
        using (var cache = new CloudAudioCache(handler, new FakeAddresses()))
        {
            var first = await cache.GetOrDownloadAsync("https://example.com/start", "Cloud fixture", cacheFolder);
            if (first.ReusedCache || first.Kind != AudioSourceKind.HttpUrl || first.DisplayName != "Cloud fixture" ||
                first.ETag != "\"fixture-1\"" || !File.Exists(first.LocalPath))
                throw new Exception("Cloud download metadata or caching failed.");
            var calls = handler.CallCount;
            var second = await cache.GetOrDownloadAsync("https://example.com/start", "Cloud fixture", cacheFolder);
            if (!second.ReusedCache || handler.CallCount != calls || second.LocalPath != first.LocalPath)
                throw new Exception("Offline cache reuse made an unnecessary network request.");
        }

        var htmlHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!doctype html><html>login</html>", System.Text.Encoding.UTF8, "text/html")
        });
        using (var cache = new CloudAudioCache(htmlHandler, new FakeAddresses()))
        {
            try
            {
                await cache.GetOrDownloadAsync("https://example.com/not-audio", null, cacheFolder);
                throw new Exception("An HTML response was accepted as audio.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("web page")) { }
        }
        if (Directory.GetFiles(cacheFolder, "*.download").Length != 0)
            throw new Exception("Failed cloud download left a partial file.");
        foreach (var address in new[] { "10.1.2.3", "172.16.1.1", "192.168.1.2", "169.254.1.1", "::1", "fc00::1", "::ffff:192.168.1.2" })
        {
            using var privateCache = new CloudAudioCache(new FakeHandler(_ => throw new Exception("Private target reached HTTP.")), new FakeAddresses(address));
            try
            {
                await privateCache.GetOrDownloadAsync("https://private.example.invalid/source.wav", null, cacheFolder);
                throw new Exception("Private DNS resolution was accepted.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("private network")) { }
        }
        using (var redirectCache = new CloudAudioCache(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("http://example.invalid/audio.wav") } }), new FakeAddresses()))
        {
            try
            {
                await redirectCache.GetOrDownloadAsync("https://example.invalid/redirect", null, cacheFolder);
                throw new Exception("HTTPS downgrade was accepted.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("HTTPS")) { }
        }
        using (var cache = new CloudAudioCache(new FakeHandler(_ => throw new Exception("Network should not be reached.")), new FakeAddresses()))
        {
            try
            {
                await cache.GetOrDownloadAsync("https://127.0.0.1/audio.wav", null, cacheFolder);
                throw new Exception("A loopback source was accepted.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("private network")) { }
        }
        Console.WriteLine("Cloud source recognition, redirect, validation, offline reuse, HTML rejection, SSRF guard and partial-file cleanup tests passed.");
    }

    private sealed class FakeAddresses(string address = "93.184.215.14") : IAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { IPAddress.Parse(address) });
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }
}
