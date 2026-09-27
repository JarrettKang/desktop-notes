using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using DesktopNotes;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/verification");
        Directory.CreateDirectory(directory);
        var runDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            VerifyStorage(runDirectory);
            VerifyChecklistText();
            VerifyWindow(runDirectory, directory);
            Console.WriteLine($"PASS: {checks} checks. Rendered previews: {directory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VerifyStorage(string directory)
    {
        var store = new NoteStore(Path.Combine(directory, "storage"));
        Check(store.Load().Count == 0, "first launch is empty");
        var note = new Note { Text = "今天的小事\r\n买牛奶 🥛\r\n想法：慢慢来。", Left = 180, Top = 120, Width = 380, Height = 420, Color = "green", Pinned = true };
        var second = new Note { Text = "第二张便签\t独立内容" };
        store.Save(new[] { note, second });
        var loaded = store.Load();
        Check(loaded.Count == 2 && loaded[0].Text == note.Text && loaded[1].Text == second.Text, "Chinese, emoji, tabs, multiline, multiple notes survive reload");
        Check(loaded[0].Id == note.Id && loaded[0].Left == 180 && loaded[0].Height == 420 && loaded[0].Pinned && loaded[0].Color == "green", "identity, bounds and preferences survive reload");
        note.Text = "修改后的内容";
        store.Save(new[] { note });
        Check(store.Load().Single().Text == note.Text, "atomic replacement persists edits and deletion");
        Check(JsonSerializer.Deserialize<NoteDocument>(File.ReadAllText(store.FilePath + ".bak"))!.Notes.Count == 2, "previous complete document remains as backup");
        var before = File.ReadAllText(store.FilePath);
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rejected = false;
            try { store.Save(new[] { new Note { Text = "must not replace locked data" } }); }
            catch (IOException) { rejected = true; }
            Check(rejected, "write failure is surfaced");
        }
        Check(File.ReadAllText(store.FilePath) == before, "failed save preserves original data");
        Check(!File.Exists(store.FilePath + ".tmp"), "failed save cleans temporary file");
        File.WriteAllText(store.FilePath, "{broken");
        try { store.Load(); throw new Exception("Corrupt data was accepted"); }
        catch (JsonException) { Check(File.ReadAllText(store.FilePath) == "{broken", "corrupt source is not overwritten"); }
        File.WriteAllText(store.FilePath, "{\"Version\":99,\"Notes\":[]}");
        try { store.Load(); throw new Exception("Unknown format was accepted"); }
        catch (InvalidDataException) { Check(true, "future format is rejected safely"); }
        store.Save(Array.Empty<Note>());
        Check(store.Load().Count == 0, "deleting last note persists an empty collection");
    }

    private static void VerifyWindow(string directory, string previews)
    {
        // Load real UI resources without starting the tray or touching user data.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = new ResourceDictionary { Source = new Uri("/DesktopNotes;component/Styles.xaml", UriKind.Relative) };
        var store = new NoteStore(Path.Combine(directory, "window"));
        var note = new Note { Left = 80, Top = 80 };
        var window = new NoteWindow(note) { ShowActivated = false };
        Check(window.ShowInTaskbar && window.Icon is not null, "note window has its own taskbar entry and yellow note icon");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var saveCount = 0;
        timer.Tick += (_, _) => { timer.Stop(); store.Save(new[] { note }); saveCount++; };
        window.Changed += () => { timer.Stop(); timer.Start(); };
        window.SaveBeforeHide = () => { timer.Stop(); store.Save(new[] { note }); return true; };
        window.Show();
        Pump();
        var editor = (TextBox)window.FindName("Editor");
        Check(((TextBlock)window.FindName("Placeholder")).Visibility == Visibility.Visible, "empty note shows placeholder");
        Render(window, Path.Combine(previews, "empty-note.png"));
        editor.Text = "今天的小事\r\n\r\n买一束花，放在桌上。\r\n记下突然想到的好点子。\r\n\r\n慢慢来，一件一件完成。";
        PumpFor(120);
        Check(store.Load().Single().Text == editor.Text && saveCount > 0, "text changes trigger debounced persistence");
        Check(((TextBlock)window.FindName("Placeholder")).Visibility == Visibility.Collapsed, "placeholder disappears when typing");
        var pin = (Button)window.FindName("PinButton");
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(note.Pinned && window.Topmost, "pin button changes actual window topmost state");
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!note.Pinned && !window.Topmost, "pin can be turned off");
        window.WindowState = WindowState.Normal;
        window.Width = 380;
        window.Height = 390;
        window.Left = 120;
        window.Top = 110;
        PumpFor(120);
        Check(Math.Abs(note.Width - 380) < 1 && Math.Abs(note.Height - 390) < 1 && Math.Abs(note.Left - 120) < 1 && Math.Abs(note.Top - 110) < 1,
            "moving and resizing update persisted model");
        var newRequests = 0;
        window.NewRequested += () => newRequests++;
        Descendants<Button>(window).Single(b => Equals(b.Content, "+")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(newRequests == 1, "new note button dispatches creation");
        Render(window, Path.Combine(previews, "written-note.png"));
        editor.Text = "关闭前最后一次编辑，立即保存 ✅";
        window.Close();
        Pump();
        Check(window.IsVisible && window.ShowInTaskbar && window.WindowState == WindowState.Minimized && store.Load().Single().Text == editor.Text,
            "close preserves taskbar entry, minimizes and flushes latest edit immediately");
        window.Reveal();
        Pump();
        Check(window.IsVisible && window.WindowState == WindowState.Normal && editor.Text == note.Text, "minimized note can be restored with content intact");
        window.SaveBeforeHide = () => false;
        window.Close();
        Check(window.IsVisible && window.WindowState == WindowState.Normal, "save failure keeps note open without minimizing");
        timer.Stop();
        window.Destroy();
        var restored = new NoteWindow(store.Load().Single()) { ShowActivated = false, ShowInTaskbar = false };
        restored.Show();
        Pump();
        Check(((TextBox)restored.FindName("Editor")).Text == "关闭前最后一次编辑，立即保存 ✅", "fresh window restores saved text");
        restored.Destroy();
        VerifyChecklistWindow(store, previews);
        VerifyCompletedAppearance(previews);
        foreach (var color in new[] { "green", "pink" })
        {
            var colored = new NoteWindow(new Note { Color = color, Text = "留一点空间\r\n给今天的灵感。", Width = 280, Height = 240 }) { ShowActivated = false, ShowInTaskbar = false };
            colored.Show();
            Pump();
            Render(colored, Path.Combine(previews, color + "-minimum.png"));
            Check(colored.ActualWidth == 280 && colored.ActualHeight == 240, color + " minimum size renders");
            colored.Destroy();
        }
        app.Shutdown();
    }

    private static void VerifyChecklistText()
    {
        string Apply(string text, int start, int length)
        {
            var edit = ChecklistText.ToggleBoxes(text, start, length);
            return text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }
        Check(Apply("第一行\r\n第二行\r\n第三行", 6, 0) == "第一行\r\n☐ 第二行\r\n第三行", "checkbox targets current logical line only");
        Check(Apply("☑ 已完成\r\n普通文字", 3, 0) == "已完成\r\n普通文字", "removing a checked box preserves text");
        Check(Apply("甲\r\n乙", 0, 3) == "☐ 甲\r\n乙", "selection ending at next line start excludes that line");
        Check(Apply("☑ 甲\n乙\n☐ 丙", 0, 9) == "☑ 甲\n☐ 乙\n☐ 丙", "mixed selection adds missing boxes and preserves completed state");
        Check(Apply("☑ 甲\n☐ 乙", 0, 7) == "甲\n乙", "batch removal preserves newlines");
        Check(Apply("", 0, 0) == "☐ ", "empty note can become a checklist");
        Check(Apply("甲\r\n", 3, 0) == "甲\r\n☐ ", "trailing empty line can have a checkbox");
        Check(Apply("甲\r乙\n丙", 2, 1) == "甲\r☐ 乙\n丙", "mixed newline styles remain intact");
        Check(Apply("正文里的 ☐ 符号", 7, 0) == "☐ 正文里的 ☐ 符号", "inline symbols are not mistaken for line checkboxes");
        var edit = ChecklistText.ToggleBoxes("文字", 1, 0);
        Check(edit.SelectionStart == 3 && edit.SelectionLength == 0, "adding a checkbox preserves caret position in text");
        edit = ChecklistText.ToggleBoxes("☑ 文字", 3, 0);
        Check(edit.SelectionStart == 1, "removing a checkbox preserves caret position in text");
    }

    private static void VerifyChecklistWindow(NoteStore store, string previews)
    {
        var note = new Note { Text = "今天的小事\r\n买一束花，放在桌上。\r\n记下突然想到的好点子。\r\n慢慢来，一件一件完成。", Width = 380, Height = 390 };
        var window = new NoteWindow(note) { ShowActivated = false, ShowInTaskbar = false };
        window.Changed += () => store.Save(new[] { note });
        window.Show();
        Pump();
        var editor = (TextBox)window.FindName("Editor");
        var button = (Button)window.FindName("ChecklistButton");
        var layer = (Canvas)window.FindName("CheckboxLayer");
        var completionLayer = (Canvas)window.FindName("CompletionLayer");
        editor.CaretIndex = editor.Text.IndexOf("买", StringComparison.Ordinal);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(note.Text.Contains("\r\n☐ 买") && layer.Children.Count == 1, "toolbar adds a clickable checkbox on the chosen line");
        var checkbox = layer.Children.OfType<CheckBox>().Single();
        checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Pump();
        Check(note.Text.Contains("☑ 买") && layer.Children.OfType<CheckBox>().Single().IsChecked == true, "click marks a line complete");
        Check(completionLayer.Children.OfType<Line>().Count() == 1 && completionLayer.Children.OfType<Rectangle>().Count() == 1,
            "checking immediately fades and strikes the completed text");
        Check(store.Load().Single().Text == note.Text, "checked state is persisted without changing the existing data format");
        layer.Children.OfType<CheckBox>().Single().RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Pump();
        Check(note.Text.Contains("☐ 买"), "click again clears completion");
        Check(completionLayer.Children.Count == 0, "unchecking removes all completed styling");
        editor.Undo();
        Pump();
        Check(note.Text.Contains("☑ 买"), "undo restores checkbox state");
        Check(completionLayer.Children.OfType<Line>().Count() == 1, "undo restores completed styling");
        editor.Redo();
        Pump();
        Check(note.Text.Contains("☐ 买"), "redo replays checkbox state");
        Check(completionLayer.Children.Count == 0, "redo clears completed styling");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(!note.Text.Contains("☐") && layer.Children.Count == 0, "toolbar removes checkbox without removing content");
        editor.Undo();
        Pump();
        Check(layer.Children.Count == 1, "undo restores a removed checkbox control");
        editor.CaretIndex = editor.Text.IndexOf("记下", StringComparison.Ordinal);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        layer.Children.OfType<CheckBox>().First().RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Pump();
        Render(window, Path.Combine(previews, "checklist-note.png"));
        window.Width = 280;
        window.Height = 240;
        Pump();
        Check(layer.Children.OfType<CheckBox>().Count() == 2, "wrapped text has one checkbox per logical line");
        editor.SelectAll();
        editor.SelectedText = "☐ " + new string('长', 45) + "\r\n☑ 短行";
        Pump();
        Check(editor.LineCount > 2 && layer.Children.Count == 2, "soft wrapping does not create extra checkboxes");
        Render(window, Path.Combine(previews, "checklist-minimum.png"));
        editor.SelectAll();
        editor.SelectedText = string.Join("\r\n", Enumerable.Range(0, 35).Select(i => "☐ 第 " + i + " 行：检查滚动定位"));
        editor.ScrollToEnd();
        Pump();
        Check(layer.Children.OfType<CheckBox>().Any() && layer.Children.OfType<CheckBox>().All(c => (int)c.Tag > 0), "checkbox controls follow scrolled lines");
        var scrolledBox = layer.Children.OfType<CheckBox>().Last();
        var target = (int)scrolledBox.Tag;
        scrolledBox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Pump();
        Check(note.Text[target] == ChecklistText.Checked && note.Text[0] == ChecklistText.Unchecked, "click after scrolling updates the correct line");
        editor.Select(0, 0);
        editor.SelectedText = "新插入的普通行\r\n";
        Pump();
        editor.ScrollToEnd();
        Pump();
        Check(layer.Children.OfType<CheckBox>().Any(c => c.IsChecked == true && note.Text[(int)c.Tag] == ChecklistText.Checked), "inserting earlier text preserves checkbox identity");
        window.Destroy();
        var restored = new NoteWindow(store.Load().Single()) { ShowActivated = false, ShowInTaskbar = false };
        restored.Show();
        Pump();
        var restoredEditor = (TextBox)restored.FindName("Editor");
        restoredEditor.ScrollToEnd();
        Pump();
        Check(((Canvas)restored.FindName("CheckboxLayer")).Children.OfType<CheckBox>().Any(c => c.IsChecked == true), "reopening restores interactive completed checkboxes");
        Check(((Canvas)restored.FindName("CompletionLayer")).Children.OfType<Line>().Any(), "reopening restores completed styling");
        restored.Destroy();
    }

    private static void VerifyCompletedAppearance(string previews)
    {
        foreach (var color in new[] { "yellow", "green", "pink" })
        {
            var text = "☑ " + string.Concat(Enumerable.Repeat("完成的长事项继续显示删除线。", 12)) + "\r\n☐ 还没完成\r\n普通文字";
            var note = new Note { Text = text, Width = 280, Height = 240, Color = color };
            var window = new NoteWindow(note) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            Pump();
            var editor = (TextBox)window.FindName("Editor");
            var completed = (Canvas)window.FindName("CompletionLayer");
            var boxes = (Canvas)window.FindName("CheckboxLayer");
            Check(completed.Children.OfType<Line>().Count() > 1, color + ": wrapped completed text has a strike on each visible line");
            Check(completed.Children.OfType<Rectangle>().All(r => r.Fill == window.Background && r.Opacity > 0 && r.Opacity < 1),
                color + ": completion lightens text using the actual paper color");
            Check(!completed.IsHitTestVisible && note.Text == text, color + ": appearance preserves editable text and passes pointer input through");
            editor.ScrollToEnd();
            Pump();
            var nextItem = editor.Text.IndexOf("\r\n☐", StringComparison.Ordinal);
            Check(completed.Children.OfType<Line>().Any() && completed.Children.OfType<Line>().All(l => (int)l.Tag < nextItem),
                color + ": scrolled continuation remains styled without styling the following items");
            Check(!boxes.Children.OfType<CheckBox>().Any(c => (int)c.Tag == 0),
                color + ": completed continuation renders while its checkbox is offscreen");
            foreach (var strike in completed.Children.OfType<Line>())
            {
                var caret = editor.GetRectFromCharacterIndex((int)strike.Tag);
                Check(Math.Abs(strike.Y1 - (caret.Top + caret.Height * 0.52)) < 1 && strike.X2 > strike.X1,
                    color + ": strike tracks actual scrolled text geometry");
            }
            Render(window, System.IO.Path.Combine(previews, "completed-scrolled-" + color + ".png"));
            editor.Select(2, 2);
            editor.SelectedText = "修改";
            Pump();
            Check(note.Text.StartsWith("☑ 修改", StringComparison.Ordinal) && completed.Children.OfType<Line>().Any(),
                color + ": completed items remain editable and styled");
            editor.Text = "☑ \r\n☐ 未完成\r\n普通文字";
            Pump();
            Check(completed.Children.Count == 0, color + ": empty completed item does not strike the next line");
            window.Destroy();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }
}
