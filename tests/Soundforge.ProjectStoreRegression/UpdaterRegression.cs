using System;
using System.IO;
using Soundforge.Updater;

internal static class UpdaterRegression
{
    public static void Run(string root)
    {
        var payload = Path.Combine(root, "update-payload");
        var target = Path.Combine(root, "update-target");
        Directory.CreateDirectory(Path.Combine(payload, "sub"));
        Directory.CreateDirectory(Path.Combine(target, "data", "CloudSources"));
        File.WriteAllText(Path.Combine(payload, "a.txt"), "new-a");
        File.WriteAllText(Path.Combine(payload, "sub", "b.txt"), "new-b");
        File.WriteAllText(Path.Combine(target, "a.txt"), "old-a");
        File.WriteAllText(Path.Combine(target, "sub-user-file.txt"), "preserve me");
        File.WriteAllText(Path.Combine(target, "data", "CloudSources", "cached.wav"), "user cache");
        File.WriteAllText(Path.Combine(target, "obsolete.dll"), "managed old binary");
        File.WriteAllText(Path.Combine(target, ".soundforge-installed-files.json"),
            "{\"Version\":1,\"Files\":[\"a.txt\",\"obsolete.dll\",\".soundforge-installed-files.json\"]}");
        UpdateInstaller.Apply(payload, target);
        if (File.ReadAllText(Path.Combine(target, "a.txt")) != "new-a" ||
            File.ReadAllText(Path.Combine(target, "sub", "b.txt")) != "new-b" ||
            File.ReadAllText(Path.Combine(target, "sub-user-file.txt")) != "preserve me" ||
            File.ReadAllText(Path.Combine(target, "data", "CloudSources", "cached.wav")) != "user cache" ||
            File.Exists(Path.Combine(target, "obsolete.dll")))
            throw new Exception("Updater replaced or lost the wrong files.");
        if (Directory.GetDirectories(target, ".soundforge-*", SearchOption.TopDirectoryOnly).Length != 0)
            throw new Exception("Successful update left staging or backup folders.");

        File.WriteAllText(Path.Combine(target, "a.txt"), "rollback-a");
        File.WriteAllText(Path.Combine(target, "sub", "b.txt"), "rollback-b");
        using (File.Open(Path.Combine(target, "sub", "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                UpdateInstaller.Apply(payload, target);
                throw new Exception("Locked update target unexpectedly succeeded.");
            }
            catch (IOException) { }
        }
        if (File.ReadAllText(Path.Combine(target, "a.txt")) != "rollback-a" ||
            File.ReadAllText(Path.Combine(target, "sub", "b.txt")) != "rollback-b" ||
            Directory.GetDirectories(target, ".soundforge-*", SearchOption.TopDirectoryOnly).Length != 0)
            throw new Exception("Failed update did not roll back cleanly.");
        Console.WriteLine("Updater overwrite, user-data preservation, verification, cleanup and rollback tests passed.");
    }
}
