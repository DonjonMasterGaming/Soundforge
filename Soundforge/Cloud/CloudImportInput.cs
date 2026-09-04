using System.IO;

namespace Soundforge.Cloud;

public static class CloudImportInput
{
    public static IReadOnlyList<string> ParseUrls(string text)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        for (var index = 0; index < lines.Length; index++)
        {
            var value = lines[index].Trim();
            if (value.Length == 0) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Line {index + 1} is not a complete HTTPS address.");
            if (seen.Add(uri.AbsoluteUri)) urls.Add(uri.AbsoluteUri);
        }

        return urls;
    }
}
