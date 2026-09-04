using System.IO;
using System.Text.RegularExpressions;

namespace Soundforge.Cloud;

public static class CloudImportInput
{
    public static IReadOnlyList<string> ParseUrls(string text)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = Regex.Split(text, @"(?:\r\n|\r|\n)|,\s*(?=https://)", RegexOptions.IgnoreCase);

        for (var index = 0; index < entries.Length; index++)
        {
            var value = entries[index].Trim().Trim('"');
            if (value.Length == 0) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Link {index + 1} is not a complete HTTPS address.");
            if (seen.Add(uri.AbsoluteUri)) urls.Add(uri.AbsoluteUri);
        }

        return urls;
    }
}
