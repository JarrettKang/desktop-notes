using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopNotes;

public static class DataLocation
{
    // AppData can be redirected when a process is launched by a packaged app.
    // A directory directly under the user profile is shared by both launch contexts.
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desktop-notes");

    public static IEnumerable<string> LegacyDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "DesktopNotes");
        var packages = Path.Combine(local, "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.GetDirectories(packages))
        {
            // Only inspect our old app-specific file, never other apps' data.
            var directory = Path.Combine(package, "LocalCache", "Local", "DesktopNotes");
            if (File.Exists(Path.Combine(directory, "notes.json"))) yield return directory;
        }
    }
}
