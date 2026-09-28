using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DesktopNotes;

public static class LegacyMigration
{
    public static int Import(NoteStore destination, IEnumerable<string> legacyDirectories)
    {
        var directory = Path.GetDirectoryName(destination.FilePath)!;
        var markerPath = Path.Combine(directory, "migrated-sources.json");
        var processed = File.Exists(markerPath)
            ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(markerPath)) ?? throw new InvalidDataException("迁移记录无法读取。")
            : new List<string>();
        var known = new HashSet<string>(processed, StringComparer.OrdinalIgnoreCase);
        var sources = new List<(string Path, string Identity, DateTime Modified, List<Note> Notes)>();
        foreach (var legacyDirectory in legacyDirectories)
        {
            var source = new NoteStore(legacyDirectory);
            if (!File.Exists(source.FilePath)) continue;
            var identity = ActualPath(source.FilePath);
            if (!known.Add(identity)) continue;
            sources.Add((source.FilePath, identity, File.GetLastWriteTimeUtc(source.FilePath), source.Load()));
        }
        if (sources.Count == 0) return 0;

        // Validate every source before changing the destination. Existing target data
        // is authoritative; differing legacy text is kept as a separate recovered note.
        var notes = destination.Load();
        var imported = 0;
        foreach (var source in sources.OrderByDescending(s => s.Modified))
        {
            foreach (var original in source.Notes)
            {
                // Empty placeholders created by the broken launch path have no
                // content to recover and should not cover the recovered notes.
                if (string.IsNullOrWhiteSpace(original.Text)) continue;
                var existing = notes.FirstOrDefault(n => n.Id == original.Id);
                if (existing is not null)
                {
                    if (existing.Text == original.Text) continue;
                    // Stable recovery IDs make interrupted migration safe to retry.
                    var recoveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                        source.Identity.ToUpperInvariant() + original.Id))[..16]);
                    if (notes.Any(n => n.Id == recoveryId)) continue;
                    original.Id = recoveryId;
                }
                notes.Add(original);
                imported++;
            }
        }

        var backup = Path.Combine(directory, "legacy-backup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        if (File.Exists(destination.FilePath)) File.Copy(destination.FilePath, Path.Combine(backup, "notes-before.json"));
        for (var i = 0; i < sources.Count; i++)
            File.Copy(sources[i].Path, Path.Combine(backup, $"source-{i + 1}.json"));

        if (imported > 0 || !File.Exists(destination.FilePath)) destination.Save(notes);
        processed.AddRange(sources.Select(s => s.Identity));
        var temporary = markerPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(processed, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, markerPath, overwrite: true);
        return imported;
    }

    private static string ActualPath(string path)
    {
        // Resolve the backing file, since an AppData alias may point to a package copy.
        using var stream = File.OpenRead(path);
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("无法确认旧便签的实际路径：" + path);
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
