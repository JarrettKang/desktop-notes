using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DesktopNotes;

public partial class App : Application
{
    private readonly List<NoteWindow> noteWindows = new();
    private List<Note> notes = new();
    private NoteStore store = null!;
    private DispatcherTimer saveTimer = null!;
    private Forms.NotifyIcon? tray;
    private Drawing.Icon? trayIcon;
    private Mutex? instanceMutex;
    private EventWaitHandle? activateEvent;
    private RegisteredWaitHandle? activationWait;
    private bool ownsMutex;
    private bool dirty;
    private bool exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var directory = DataLocation.DefaultDirectory;
            var customDirectory = e.Args.Length == 2 && e.Args[0] == "--data-dir";
            if (customDirectory) directory = Path.GetFullPath(e.Args[1]);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + directory.ToUpperInvariant())))[..24];
            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DesktopNotes.Show." + identity);
            instanceMutex = new Mutex(true, @"Local\DesktopNotes.Instance." + identity, out ownsMutex);
            if (!ownsMutex)
            {
                activateEvent.Set();
                Shutdown();
                return;
            }
            store = new NoteStore(directory);
            if (!customDirectory) LegacyMigration.Import(store, DataLocation.LegacyDirectories());
            notes = store.Load();
            WriteStartupDiagnostic(directory);
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            saveTimer.Tick += (_, _) => Save();
            CreateTray();
            if (notes.Count == 0) NewNote();
            else foreach (var note in notes) CreateWindow(note);
            activationWait = ThreadPool.RegisterWaitForSingleObject(activateEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!exiting) ShowAll(); })), null, Timeout.Infinite, false);
            SessionEnding += (_, args) => { if (!Save()) args.Cancel = true; };
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开便签，已有内容不会被覆盖。\n\n" + ex.Message +
                (store is null ? "" : "\n\n数据位置：" + store.FilePath), "桌面便签", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void WriteStartupDiagnostic(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-startup.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                StartedAt = DateTimeOffset.Now,
                ProcessId = Environment.ProcessId,
                DataFile = store.FilePath,
                Notes = notes.Select(n => new { n.Id, CharacterCount = n.Text.Length }).ToArray()
            }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void CreateWindow(Note note)
    {
        var window = new NoteWindow(note);
        window.Changed += ScheduleSave;
        window.NewRequested += NewNote;
        window.DeleteRequested += () => DeleteNote(window);
        window.SaveBeforeHide = () => Save(showError: true);
        noteWindows.Add(window);
        window.Show();
    }

    private void NewNote()
    {
        var area = SystemParameters.WorkArea;
        var offset = (notes.Count % 8) * 26;
        var note = new Note { Left = Math.Max(area.Left + 20, area.Right - 390 - offset), Top = area.Top + 70 + offset };
        notes.Add(note);
        CreateWindow(note);
        ScheduleSave();
        Save();
    }

    private void DeleteNote(NoteWindow window)
    {
        var index = notes.IndexOf(window.Note);
        notes.RemoveAt(index);
        dirty = true;
        if (!Save(showError: true))
        {
            notes.Insert(index, window.Note);
            ScheduleSave();
            return;
        }
        noteWindows.Remove(window);
        window.Destroy();
    }

    private void ScheduleSave()
    {
        dirty = true;
        foreach (var window in noteWindows) window.SetSaveStatus("正在保存…");
        saveTimer.Stop();
        saveTimer.Start();
    }

    private bool Save(bool showError = false)
    {
        saveTimer.Stop();
        if (!dirty) return true;
        try
        {
            store.Save(notes);
            dirty = false;
            foreach (var window in noteWindows) window.SetSaveStatus("已保存到本机");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            foreach (var window in noteWindows) window.SetSaveStatus("保存失败 · Ctrl+S 重试", ex.Message);
            if (showError) MessageBox.Show("内容尚未保存，请保留便签窗口并重试。\n\n" + ex.Message,
                "暂时无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ShowAll()
    {
        if (noteWindows.Count == 0) NewNote();
        foreach (var window in noteWindows) window.Reveal();
    }

    private void CreateTray()
    {
        trayIcon = CreateNoteIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("新建便签", null, (_, _) => Dispatcher.Invoke(NewNote));
        menu.Items.Add("显示所有便签", null, (_, _) => Dispatcher.Invoke(ShowAll));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));
        tray = new Forms.NotifyIcon { Text = "桌面便签 · 双击显示所有便签", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAll);
    }

    private void ExitApp()
    {
        if (!Save(showError: true)) return;
        exiting = true;
        foreach (var window in noteWindows.ToArray()) window.Destroy();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        activationWait?.Unregister(null);
        activateEvent?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static Drawing.Icon CreateNoteIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/DesktopNotes;component/Assets/Note.ico")).Stream;
        using var icon = new Drawing.Icon(stream, new Drawing.Size(32, 32));
        return (Drawing.Icon)icon.Clone();
    }
}
