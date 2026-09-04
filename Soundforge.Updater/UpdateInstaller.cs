using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;

namespace Soundforge.Updater;

public static class UpdateInstaller
{
    public static void Apply(string payloadDirectory, string targetDirectory)
    {
        var payload = Path.GetFullPath(payloadDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(payload)) throw new DirectoryNotFoundException("The update payload is missing.");
        if (IsSameOrChild(target, payload) || IsSameOrChild(payload, target))
            throw new InvalidOperationException("The update package and installation folders must be separate.");
        Directory.CreateDirectory(target);
        var operation = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(target, $".soundforge-update-{operation}");
        var backup = Path.Combine(target, $".soundforge-backup-{operation}");
        Directory.CreateDirectory(staging);
        var installed = new List<(string Target, string? Backup)>();
        try
        {
            var newFiles = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                .Select(source => (Source: source, Relative: ValidateRelativePath(Path.GetRelativePath(payload, source))))
                .ToList();
            foreach (var source in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                var relative = ValidateRelativePath(Path.GetRelativePath(payload, source));
                var staged = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(source, staged, overwrite: false);
                using var sourceHashInput = File.OpenRead(source);
                using var stagedHashInput = File.OpenRead(staged);
                if (!SHA256.HashData(sourceHashInput).SequenceEqual(SHA256.HashData(stagedHashInput)))
                    throw new InvalidDataException($"Update verification failed for {relative}.");
            }

            const string installManifestName = ".soundforge-installed-files.json";
            var newManifestFiles = newFiles.Select(item => item.Relative).Append(installManifestName)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(Path.Combine(staging, installManifestName), JsonSerializer.Serialize(new InstallManifest(1, newManifestFiles)));
            var previousManifestPath = Path.Combine(target, installManifestName);
            var previousFiles = ReadInstallManifest(previousManifestPath);
            foreach (var relative in previousFiles.Except(newManifestFiles, StringComparer.OrdinalIgnoreCase))
            {
                var obsolete = Path.Combine(target, ValidateRelativePath(relative));
                if (!File.Exists(obsolete)) continue;
                var previous = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                File.Move(obsolete, previous);
                installed.Add((obsolete, previous));
            }

            foreach (var staged in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Order().ToList())
            {
                var relative = Path.GetRelativePath(staging, staged);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? previous = null;
                if (File.Exists(destination))
                {
                    previous = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    File.Move(destination, previous);
                }
                installed.Add((destination, previous));
                File.Move(staged, destination);
            }
        }
        catch
        {
            foreach (var item in installed.AsEnumerable().Reverse())
            {
                if (File.Exists(item.Target)) File.Delete(item.Target);
                if (item.Backup is not null && File.Exists(item.Backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                    File.Move(item.Backup, item.Target);
                }
            }
            throw;
        }
        finally
        {
            TryDeleteExactOperationFolder(staging, target, ".soundforge-update-");
            TryDeleteExactOperationFolder(backup, target, ".soundforge-backup-");
        }
    }

    private static string ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new InvalidDataException("Unsafe update payload path.");
        return relative;
    }

    private static IReadOnlyList<string> ReadInstallManifest(string path)
    {
        try
        {
            var manifest = File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path)) : null;
            return manifest is { Version: 1, Files: not null } ? manifest.Files : [];
        }
        catch (JsonException) { return []; }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative));
    }

    private static void TryDeleteExactOperationFolder(string path, string target, string prefix)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.Equals(parent, target, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record InstallManifest(int Version, List<string> Files);
}
