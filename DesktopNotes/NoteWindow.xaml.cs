using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopNotes;

public partial class NoteWindow : Window
{
    private bool ready;
    private bool destroying;
    private bool checkboxRefreshPending;
    public Note Note { get; }
    public event Action? Changed;
    public event Action? NewRequested;
    public event Action? DeleteRequested;
    public Func<bool>? SaveBeforeHide { get; set; }

    public NoteWindow(Note note)
    {
        Note = note;
        InitializeComponent();
        Width = note.Width;
        Height = note.Height;
        Left = note.Left;
        Top = note.Top;
        Topmost = note.Pinned;
        Editor.Text = note.Text;
        UpdatePlaceholder();
        ApplyColor();
        UpdatePin();
        Loaded += (_, _) =>
        {
            KeepOnScreen();
            ready = true;
            CaptureBounds();
            Editor.Focus();
            RefreshCheckboxes();
        };
        Editor.SizeChanged += (_, _) => QueueCheckboxRefresh();
        Editor.IsVisibleChanged += (_, _) => QueueCheckboxRefresh();
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueCheckboxRefresh()));
        CreateEditorMenu();
        LocationChanged += (_, _) => CaptureBounds();
        SizeChanged += (_, _) => CaptureBounds();
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
            {
                ToggleLineCheckboxes();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
            {
                ToggleCheckedAt(ChecklistText.LineStartAt(Editor.Text, Editor.CaretIndex));
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
            {
                NewRequested?.Invoke();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                SaveBeforeHide?.Invoke();
                e.Handled = true;
            }
        };
    }

    public void SetSaveStatus(string text, string? detail = null)
    {
        SaveStatus.Text = text;
        SaveStatus.ToolTip = detail;
    }

    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        KeepOnScreen();
        Activate();
    }

    public void Destroy()
    {
        destroying = true;
        Close();
    }

    private void KeepOnScreen()
    {
        // Convert each real monitor's working area from physical pixels to this window's DIPs.
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var header = new Rect(Left, Top, Width, 49);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var work = new Rect(fromDevice.Transform(new Point(area.Left, area.Top)),
                                fromDevice.Transform(new Point(area.Right, area.Bottom)));
            var intersection = Rect.Intersect(header, work);
            if (!intersection.IsEmpty && intersection.Width >= 100 && intersection.Height >= 30) return;
        }
        var primary = SystemParameters.WorkArea;
        Left = primary.Left + 40;
        Top = primary.Top + 40;
    }

    private void CaptureBounds()
    {
        if (!ready || WindowState != WindowState.Normal) return;
        if (Note.Left == Left && Note.Top == Top && Note.Width == Width && Note.Height == Height) return;
        Note.Left = Left;
        Note.Top = Top;
        Note.Width = Width;
        Note.Height = Height;
        Changed?.Invoke();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        Note.Text = Editor.Text;
        UpdatePlaceholder();
        QueueCheckboxRefresh();
        Changed?.Invoke();
    }

    private void Checklist_Click(object sender, RoutedEventArgs e) => ToggleLineCheckboxes();

    private void ToggleLineCheckboxes()
    {
        var edit = ChecklistText.ToggleBoxes(Editor.Text, Editor.SelectionStart, Editor.SelectionLength);
        Editor.BeginChange();
        try
        {
            Editor.Select(edit.Start, edit.Length);
            Editor.SelectedText = edit.Replacement;
            Editor.Select(edit.SelectionStart, edit.SelectionLength);
        }
        finally { Editor.EndChange(); }
        Editor.Focus();
    }

    private void ToggleCheckedAt(int lineStart)
    {
        if (!ChecklistText.HasBox(Editor.Text, lineStart)) return;
        var selectionStart = Editor.SelectionStart;
        var selectionLength = Editor.SelectionLength;
        var replacement = Editor.Text[lineStart] == ChecklistText.Checked ? ChecklistText.Unchecked : ChecklistText.Checked;
        Editor.BeginChange();
        try
        {
            Editor.Select(lineStart, 1);
            Editor.SelectedText = replacement.ToString();
            Editor.Select(selectionStart, selectionLength);
        }
        finally { Editor.EndChange(); }
        Editor.Focus();
    }

    private void CreateEditorMenu()
    {
        var menu = new ContextMenu();
        var toggle = new MenuItem { Header = "添加 / 移除行勾选框", InputGestureText = "Ctrl+Shift+C" };
        toggle.Click += (_, _) => ToggleLineCheckboxes();
        menu.Items.Add(toggle);
        var complete = new MenuItem { Header = "勾选 / 取消勾选当前行", InputGestureText = "Ctrl+Enter" };
        complete.Click += (_, _) => ToggleCheckedAt(ChecklistText.LineStartAt(Editor.Text, Editor.CaretIndex));
        menu.Items.Add(complete);
        menu.Opened += (_, _) => complete.IsEnabled = ChecklistText.HasBox(Editor.Text, ChecklistText.LineStartAt(Editor.Text, Editor.CaretIndex));
        menu.Items.Add(new Separator());
        foreach (var (label, command) in new[] { ("撤销", ApplicationCommands.Undo), ("重做", ApplicationCommands.Redo),
            ("剪切", ApplicationCommands.Cut), ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = Editor });
        Editor.ContextMenu = menu;
    }

    private void QueueCheckboxRefresh()
    {
        if (!ready || destroying || checkboxRefreshPending) return;
        checkboxRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            checkboxRefreshPending = false;
            if (!destroying) RefreshCheckboxes();
        }));
    }

    private void RefreshCheckboxes()
    {
        CheckboxLayer.Children.Clear();
        CompletionLayer.Children.Clear();
        if (!Editor.IsVisible || Editor.ActualHeight <= 0) return;
        var text = Editor.Text;
        RefreshCompletedLines(text);
        foreach (var start in ChecklistText.LineStarts(text))
        {
            if (!ChecklistText.HasBox(text, start)) continue;
            var rect = Editor.GetRectFromCharacterIndex(start);
            if (rect.IsEmpty || rect.Bottom <= 0 || rect.Top >= Editor.ActualHeight) continue;
            var next = Editor.GetRectFromCharacterIndex(start + 1);
            if (next.IsEmpty) continue;
            var box = new CheckBox
            {
                Style = (Style)FindResource("LineCheckBox"),
                Width = Math.Max(16, next.X - rect.X), Height = rect.Height,
                Background = Background, IsChecked = text[start] == ChecklistText.Checked,
                ToolTip = "勾选 / 取消勾选 (Ctrl+Enter)", Tag = start
            };
            var lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, start);
            var lineText = text.Substring(start + 2, (lineEnd < 0 ? text.Length : lineEnd) - start - 2);
            System.Windows.Automation.AutomationProperties.SetName(box, "完成状态：" + lineText);
            box.Click += (_, _) => ToggleCheckedAt(start);
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            CheckboxLayer.Children.Add(box);
        }
    }

    private void RefreshCompletedLines(string text)
    {
        var firstVisible = Editor.GetFirstVisibleLineIndex();
        var lastVisible = Editor.GetLastVisibleLineIndex();
        if (firstVisible < 0 || lastVisible < 0) return;
        var strikeBrush = Editor.Foreground.Clone();
        strikeBrush.Opacity = 0.48;
        foreach (var start in ChecklistText.LineStarts(text))
        {
            if (!ChecklistText.HasBox(text, start) || text[start] != ChecklistText.Checked) continue;
            var contentStart = start + 2;
            var contentEnd = text.IndexOfAny(new[] { '\r', '\n' }, contentStart);
            if (contentEnd < 0) contentEnd = text.Length;
            if (contentStart == contentEnd) continue;
            var firstLine = Math.Max(firstVisible, Editor.GetLineIndexFromCharacterIndex(contentStart));
            var lastLine = Math.Min(lastVisible, Editor.GetLineIndexFromCharacterIndex(contentEnd - 1));
            for (var line = firstLine; line <= lastLine; line++)
            {
                var lineStart = Editor.GetCharacterIndexFromLineIndex(line);
                var segmentStart = Math.Max(contentStart, lineStart);
                var segmentEnd = Math.Min(contentEnd, lineStart + Editor.GetLineLength(line));
                if (segmentStart >= segmentEnd) continue;
                var leading = Editor.GetRectFromCharacterIndex(segmentStart);
                var trailing = Editor.GetRectFromCharacterIndex(segmentEnd - 1, trailingEdge: true);
                if (leading.IsEmpty || trailing.IsEmpty) continue;
                var left = Math.Min(leading.X, trailing.X);
                var right = Math.Max(leading.X, trailing.X);
                var width = right - left;
                if (width <= 0) continue;

                // Blend the paper over the native text to lighten it without replacing
                // the editor, its IME, selection or undo history. This layer passes input
                // through, and uses the editor's actual wrapped/scrolled line geometry.
                var fade = new Rectangle
                {
                    Width = width, Height = leading.Height, Fill = Background,
                    Opacity = 0.56, Tag = segmentStart
                };
                Canvas.SetLeft(fade, left);
                Canvas.SetTop(fade, leading.Top);
                CompletionLayer.Children.Add(fade);
                var strike = new Line
                {
                    X1 = left, X2 = right,
                    Y1 = leading.Top + leading.Height * 0.52,
                    Y2 = leading.Top + leading.Height * 0.52,
                    Stroke = strikeBrush, StrokeThickness = 1, Tag = segmentStart
                };
                CompletionLayer.Children.Add(strike);
            }
        }
    }

    private void UpdatePlaceholder() => Placeholder.Visibility =
        string.IsNullOrEmpty(Editor.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is ButtonBase) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke();
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Note.Pinned = !Note.Pinned;
        Topmost = Note.Pinned;
        UpdatePin();
        Changed?.Invoke();
    }

    private void UpdatePin()
    {
        PinButton.Background = Note.Pinned ? Brush("#28000000") : Brushes.Transparent;
        PinButton.ToolTip = Note.Pinned ? "取消置顶" : "置顶（显示在其他窗口上方）";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, Note.Pinned ? "取消置顶" : "置顶");
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        foreach (var (key, label) in new[] { ("yellow", "奶油黄"), ("green", "薄荷绿"), ("pink", "浅樱粉") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Note.Color == key };
            item.Click += (_, _) => { Note.Color = key; ApplyColor(); Changed?.Invoke(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "删除这张便签…" };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "确定删除这张便签吗？删除后无法撤销。", "删除便签",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                DeleteRequested?.Invoke();
        };
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void ApplyColor()
    {
        var (paper, header, edge) = Note.Color switch
        {
            "green" => ("#E7F2DF", "#D6E7CC", "#C3D6B8"),
            "pink" => ("#FBE7E7", "#F4D6D6", "#E4BFC0"),
            _ => ("#FFF4C2", "#F8E8A8", "#DFD19D")
        };
        Background = Brush(paper);
        Frame.Background = Brush(paper);
        Header.Background = Brush(header);
        Frame.BorderBrush = Brush(edge);
        QueueCheckboxRefresh();
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (destroying) return;
        e.Cancel = true;
        if (SaveBeforeHide?.Invoke() != false) WindowState = WindowState.Minimized;
    }
}
