using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopNotes;

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string Color { get; set; } = "yellow";
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 360;
    public bool Pinned { get; set; }
}

public sealed class NoteDocument
{
    public int Version { get; set; } = 1;
    public List<Note> Notes { get; set; } = new();
}

public sealed class NoteStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; }
    public NoteStore(string directory) => FilePath = Path.Combine(directory, "notes.json");

    public List<Note> Load()
    {
        if (!File.Exists(FilePath)) return new();
        var document = JsonSerializer.Deserialize<NoteDocument>(File.ReadAllText(FilePath), Options);
        if (document is null || document.Version != 1 || document.Notes is null)
            throw new InvalidDataException("便签文件格式不受支持。原文件已保留。");
        if (document.Notes.Any(n => n is null || n.Id == Guid.Empty || n.Text is null ||
                !double.IsFinite(n.Left) || !double.IsFinite(n.Top) ||
                !double.IsFinite(n.Width) || !double.IsFinite(n.Height)) ||
            document.Notes.Select(n => n.Id).Distinct().Count() != document.Notes.Count)
            throw new InvalidDataException("便签数据不完整。原文件已保留。");
        foreach (var note in document.Notes)
        {
            note.Width = Math.Clamp(note.Width, 280, 2000);
            note.Height = Math.Clamp(note.Height, 240, 2000);
            if (note.Color is not ("yellow" or "green" or "pink")) note.Color = "yellow";
        }
        return document.Notes;
    }

    public void Save(IReadOnlyCollection<Note> notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new NoteDocument { Notes = notes.ToList() }, Options);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
