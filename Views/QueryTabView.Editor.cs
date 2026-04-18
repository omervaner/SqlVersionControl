using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using SqlVersionControl.Rendering;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    private OccurrenceHighlighter? _occurrenceHighlighter;
    private BracketHighlighter? _bracketHighlighter;
    private AvaloniaEdit.Folding.FoldingManager? _foldingManager;
    private Timer? _foldingTimer;
    private ExecutionFlashHighlighter? _flashHighlighter;
    private TextBox? _goToLineBox;

    // Column (rectangle) selection via Option+Drag
    private bool _isColumnSelecting;
    private AvaloniaEdit.TextViewPosition _columnSelectStart;
    private AvaloniaEdit.TextViewPosition? _lastColumnSelectPos;

    private void ApplyGridRowHeight()
    {
        var height = _settings?.Settings.GridRowHeight ?? 22;
        ResultsGrid.RowHeight = height;
        ResultsGrid.ColumnHeaderHeight = height + 4;
    }

    private void UpdateEmptyStateText()
    {
        if (_viewModel == null) return;

        if (string.IsNullOrEmpty(_viewModel.TabConnectionString))
            EmptyState.Text = "Not connected — use File → Manage Connections to connect";
        else if (string.IsNullOrEmpty(_viewModel.SelectedDatabase))
            EmptyState.Text = "Select a database from the dropdown above";
        else
            EmptyState.Text = "Run a query to see results here";
    }

    private void ApplyEditorFontSize()
    {
        var size = _settings?.Settings.FontSize ?? 12;
        SqlEditor.FontSize = size;
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;

        var delta = e.Delta.Y > 0 ? 1 : -1;
        var currentSize = _settings?.Settings.FontSize ?? 12;
        var newSize = Math.Clamp(currentSize + delta, 8, 32);

        if (newSize == currentSize) return;

        if (_settings != null)
        {
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
        }

        e.Handled = true;
    }

    private void ApplyEditorSelectionColors()
    {
        if (Application.Current?.Resources.TryGetResource("EditorSelectionBrush", null, out var selBrush) == true
            && selBrush is Avalonia.Media.IBrush brush)
            SqlEditor.TextArea.SelectionBrush = brush;
        if (Application.Current?.Resources.TryGetResource("EditorSelectionForeground", null, out var selFg) == true
            && selFg is Avalonia.Media.IBrush fgBrush)
            SqlEditor.TextArea.SelectionForeground = fgBrush;
    }

    private void LoadSyntaxHighlighting()
    {
        IHighlightingDefinition? definition = null;

        try
        {
            var uri = new Uri("avares://SqlVersionControl/Assets/SQL.xshd");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            using var reader = new System.Xml.XmlTextReader(stream);
            definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            // Fall through to built-in
        }

        definition ??= HighlightingManager.Instance.GetDefinition("TSQL");

        // Apply ThemeManager colors to the highlighting definition
        if (definition != null)
        {
            ApplyThemeColors(definition);
            SqlEditor.SyntaxHighlighting = definition;
        }

        // Set editor background/foreground from theme
        if (Application.Current?.Resources.TryGetResource("EditorBackground", null, out var edBg) == true && edBg is IBrush edBrush)
            SqlEditor.Background = edBrush;
        if (Application.Current?.Resources.TryGetResource("TextPrimary", null, out var edFg) == true && edFg is IBrush fgBrush)
            SqlEditor.Foreground = fgBrush;
    }

    private static void ApplyThemeColors(IHighlightingDefinition definition)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            switch (color.Name)
            {
                case "Keyword":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetKeywordColor());
                    break;
                case "String":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetStringColor());
                    break;
                case "Comment":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetCommentColor());
                    break;
                case "Number":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetNumberColor());
                    break;
                case "Variable":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetVariableColor());
                    break;
                case "SystemFunction":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetSystemFunctionColor());
                    break;
                case "Identifier":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetIdentifierColor());
                    break;
            }
        }
    }

    private static readonly string[] EditorHints =
    [
        "Write your SQL query here \u2014 press F5 to execute",
        "Select text and press F5 to run just the selection",
        "Cmd+Click on a proc or view name to peek its definition",
        "Cmd+R to expand/collapse the results panel",
        "Ctrl+Space to trigger autocomplete",
        "Cmd+K to comment lines, Cmd+L to uncomment",
        "Drag a table from Object Explorer to insert its name",
        "Ctrl+Shift+F to format SQL, Ctrl+Shift+Q to quick-quote",
        "Alt+Up/Down to move lines up or down",
        "Cmd+G to go to a specific line number",
        "Double-click a word to highlight all occurrences",
        "Cmd+Shift+U to uppercase, Cmd+Shift+L to lowercase",
        "Mouse wheel + Cmd to zoom the editor font",
        "Type 2+ characters in the OE filter to search across all databases",
    ];
    private static int _hintIndex = Random.Shared.Next(EditorHints.Length);

    private void UpdatePlaceholder()
    {
        var show = string.IsNullOrEmpty(SqlEditor.Text) && !SqlEditor.TextArea.IsFocused;
        if (show)
        {
            EditorPlaceholder.Text = EditorHints[_hintIndex % EditorHints.Length];
            _hintIndex++;
        }
        EditorPlaceholder.IsVisible = show;
    }

    private static void WidenLineNumberGutter(AvaloniaEdit.TextEditor editor)
    {
        Avalonia.Controls.Control? lineNumMargin = null;
        foreach (var margin in editor.TextArea.LeftMargins)
        {
            if (margin.GetType().Name == "LineNumberMargin")
            {
                lineNumMargin = (Avalonia.Controls.Control)margin;
                lineNumMargin.Width = 34;
                lineNumMargin.Margin = new Avalonia.Thickness(0, 0, 2, 0);

                break;
            }
        }

        if (lineNumMargin == null) return;

        int gutterDragAnchorLine = -1;

        int GetLineAtY(Avalonia.Point posInTextView)
        {
            var vl = editor.TextArea.TextView.GetVisualLineFromVisualTop(
                posInTextView.Y + editor.TextArea.TextView.ScrollOffset.Y);
            return vl?.FirstDocumentLine.LineNumber ?? -1;
        }

        void SelectLineRange(int fromLine, int toLine)
        {
            int startLine = Math.Min(fromLine, toLine);
            int endLine = Math.Max(fromLine, toLine);
            var startDoc = editor.Document.GetLineByNumber(startLine);
            var endDoc = editor.Document.GetLineByNumber(endLine);
            editor.TextArea.Caret.Line = toLine;
            editor.TextArea.Caret.Column = 1;
            editor.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(
                editor.TextArea, startDoc.Offset,
                endDoc.Offset + endDoc.TotalLength);
        }

        bool IsInGutter(Avalonia.Input.PointerEventArgs e)
        {
            var pos = e.GetPosition(editor.TextArea);
            return pos.X <= lineNumMargin!.Bounds.Right + lineNumMargin.Margin.Right;
        }

        // Click in gutter: select whole line
        editor.TextArea.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (s, e) =>
        {
            if (!IsInGutter(e)) return;

            var line = GetLineAtY(e.GetPosition(editor.TextArea.TextView));
            if (line < 1) return;

            gutterDragAnchorLine = line;
            SelectLineRange(line, line);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Drag in gutter: extend selection across lines
        editor.TextArea.AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, (s, e) =>
        {
            if (gutterDragAnchorLine < 1) return;
            if (!e.GetCurrentPoint(editor.TextArea).Properties.IsLeftButtonPressed)
            {
                gutterDragAnchorLine = -1;
                return;
            }

            var line = GetLineAtY(e.GetPosition(editor.TextArea.TextView));
            if (line < 1) return;

            SelectLineRange(gutterDragAnchorLine, line);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Release: end drag
        editor.TextArea.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (s, e) =>
        {
            gutterDragAnchorLine = -1;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void ConfigureEditor()
    {
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;

        // Block Option+Shift+drag: triggers an upstream AvaloniaEdit race in
        // SelectionLayer.Render() vs TextView.VisualLines. The gesture doesn't
        // produce rectangle selection anyway — just flaky SimpleSelection.
        SqlEditor.TextArea.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Column (rectangle) selection via Option+Drag
        SqlEditor.TextArea.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            if (!e.GetCurrentPoint(SqlEditor.TextArea).Properties.IsLeftButtonPressed) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

            var tv = SqlEditor.TextArea.TextView;
            var pos = tv.GetPosition(e.GetPosition(tv));
            if (pos == null) return;

            _isColumnSelecting = true;
            _columnSelectStart = pos.Value;
            _lastColumnSelectPos = null;
            e.Pointer.Capture(SqlEditor.TextArea);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        SqlEditor.TextArea.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (!_isColumnSelecting) return;

            var tv = SqlEditor.TextArea.TextView;
            var pos = tv.GetPosition(e.GetPosition(tv));
            if (pos == null) return;
            var current = pos.Value;

            // Throttle: only update when character cell changes
            if (_lastColumnSelectPos.HasValue &&
                _lastColumnSelectPos.Value.Line == current.Line &&
                _lastColumnSelectPos.Value.VisualColumn == current.VisualColumn)
                return;

            _lastColumnSelectPos = current;
            SqlEditor.TextArea.Selection = new AvaloniaEdit.Editing.RectangleSelection(
                SqlEditor.TextArea, _columnSelectStart, current);
            SqlEditor.TextArea.Caret.Position = current;
            SqlEditor.TextArea.Caret.BringCaretToView();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        SqlEditor.TextArea.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (!_isColumnSelecting) return;
            _isColumnSelecting = false;
            e.Pointer.Capture(null);

            // Alt+click with no drag: clear stale selection
            if (!_lastColumnSelectPos.HasValue)
                SqlEditor.TextArea.ClearSelection();

            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        WidenLineNumberGutter(SqlEditor);

        SqlEditor.Text = "";
        _viewModel?.SetInitialText("");
        UpdatePlaceholder();

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_viewModel != null)
                _viewModel.SqlText = SqlEditor.Text;
            UpdatePlaceholder();
        };

        SqlEditor.TextArea.GotFocus += (_, _) => UpdatePlaceholder();
        SqlEditor.TextArea.LostFocus += (_, _) => UpdatePlaceholder();

        SqlEditor.TextArea.TextEntering += OnTextEntering;
        SqlEditor.TextArea.TextEntered += OnTextEntered;

        // Section 11: Highlight all occurrences of selected word
        _occurrenceHighlighter = new OccurrenceHighlighter();
        SqlEditor.TextArea.TextView.LineTransformers.Add(_occurrenceHighlighter);
        SqlEditor.TextArea.SelectionChanged += (_, _) => UpdateOccurrenceHighlight();
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateOccurrenceHighlight();

        // Bracket matching
        _bracketHighlighter = new BracketHighlighter();
        SqlEditor.TextArea.TextView.LineTransformers.Add(_bracketHighlighter);
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateBracketHighlight();

        // Code folding
        _foldingManager = AvaloniaEdit.Folding.FoldingManager.Install(SqlEditor.TextArea);
        var foldingStrategy = new SqlFoldingStrategy();
        SqlEditor.TextChanged += (_, _) =>
        {
            _foldingTimer?.Dispose();
            _foldingTimer = new Timer(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_foldingManager == null) return;
                    var foldings = foldingStrategy.CreateNewFoldings(SqlEditor.Document);
                    _foldingManager.UpdateFoldings(foldings, -1);
                });
            }, null, 500, Timeout.Infinite);
        };

    }

    // ── Section 11: Highlight All Occurrences ────────────────────────

    private void UpdateOccurrenceHighlight()
    {
        if (_occurrenceHighlighter == null) return;

        var selection = SqlEditor.SelectedText?.Trim();

        // Determine new word: null if not a valid single word
        string? newWord = null;
        if (!string.IsNullOrWhiteSpace(selection) && !selection.Contains(' ') && !selection.Contains('\n')
            && selection.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@'))
        {
            newWord = selection;
        }

        // Skip redraw if the highlighted word hasn't changed
        if (_occurrenceHighlighter.SelectedWord == newWord) return;

        _occurrenceHighlighter.SelectedWord = newWord;
        if (newWord != null)
            _occurrenceHighlighter.HighlightColor = GetWordHighlightColor();
        SqlEditor.TextArea.TextView.Redraw();
    }

    private void UpdateBracketHighlight()
    {
        if (_bracketHighlighter == null) return;

        var offset = SqlEditor.CaretOffset;
        var text = SqlEditor.Text;
        var match = FindMatchingBracket(text, offset);

        var newOpen = match?.openOffset ?? -1;
        var newClose = match?.closeOffset ?? -1;

        // Skip redraw if bracket positions haven't changed
        if (_bracketHighlighter.OpenOffset == newOpen && _bracketHighlighter.CloseOffset == newClose) return;

        // Targeted line redraws: only invalidate the affected bracket lines
        var doc = SqlEditor.Document;
        var tv = SqlEditor.TextArea.TextView;
        if (_bracketHighlighter.OpenOffset >= 0 && _bracketHighlighter.OpenOffset <= doc.TextLength)
            tv.Redraw(doc.GetLineByOffset(_bracketHighlighter.OpenOffset));
        if (_bracketHighlighter.CloseOffset >= 0 && _bracketHighlighter.CloseOffset <= doc.TextLength)
            tv.Redraw(doc.GetLineByOffset(_bracketHighlighter.CloseOffset));

        _bracketHighlighter.OpenOffset = newOpen;
        _bracketHighlighter.CloseOffset = newClose;

        if (newOpen >= 0 && newOpen <= doc.TextLength)
            tv.Redraw(doc.GetLineByOffset(newOpen));
        if (newClose >= 0 && newClose <= doc.TextLength)
            tv.Redraw(doc.GetLineByOffset(newClose));
    }

    private static (int openOffset, int closeOffset)? FindMatchingBracket(string text, int offset)
    {
        if (offset > 0)
        {
            var ch = text[offset - 1];
            if (ch == '(') return FindClosingBracket(text, offset - 1, '(', ')');
            if (ch == ')') return FindOpeningBracket(text, offset - 1, '(', ')');
        }
        if (offset < text.Length)
        {
            var ch = text[offset];
            if (ch == '(') return FindClosingBracket(text, offset, '(', ')');
            if (ch == ')') return FindOpeningBracket(text, offset, '(', ')');
        }
        return null;
    }

    private static (int openOffset, int closeOffset)? FindClosingBracket(string text, int openPos, char open, char close)
    {
        int depth = 1;
        for (int i = openPos + 1; i < text.Length && depth > 0; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) { depth--; if (depth == 0) return (openPos, i); }
        }
        return null;
    }

    private static (int openOffset, int closeOffset)? FindOpeningBracket(string text, int closePos, char open, char close)
    {
        int depth = 1;
        for (int i = closePos - 1; i >= 0 && depth > 0; i--)
        {
            if (text[i] == close) depth++;
            else if (text[i] == open) { depth--; if (depth == 0) return (i, closePos); }
        }
        return null;
    }

    private void OnEditorDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Check if the double-clicked word is BEGIN or END
        var doc = SqlEditor.Document;
        var offset = SqlEditor.CaretOffset;
        if (offset < 0 || offset > doc.TextLength) return;

        var text = doc.Text;

        // Find the word under cursor (AvaloniaEdit already selected it by now)
        int wordStart = offset, wordEnd = offset;
        while (wordStart > 0 && char.IsLetterOrDigit(text[wordStart - 1])) wordStart--;
        while (wordEnd < text.Length && char.IsLetterOrDigit(text[wordEnd])) wordEnd++;
        var word = text[wordStart..wordEnd];

        if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            // Skip BEGIN TRAN / BEGIN TRANSACTION
            var afterBegin = wordEnd;
            while (afterBegin < text.Length && char.IsWhiteSpace(text[afterBegin])) afterBegin++;
            if (IsKeywordAtPosition(text, afterBegin, "TRAN") || IsKeywordAtPosition(text, afterBegin, "TRANSACTION"))
                return;

            var match = FindMatchingEnd(text, wordStart);
            if (match >= 0)
            {
                var selStart = wordStart;
                var selLen = match + 3 - wordStart;
                // Defer to override AvaloniaEdit's built-in word selection
                Dispatcher.UIThread.Post(() => SqlEditor.Select(selStart, selLen),
                    DispatcherPriority.Background);
                e.Handled = true;
            }
        }
        else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            var match = FindMatchingBegin(text, wordStart);
            if (match >= 0)
            {
                var selStart = match;
                var selLen = wordEnd - match;
                Dispatcher.UIThread.Post(() => SqlEditor.Select(selStart, selLen),
                    DispatcherPriority.Background);
                e.Handled = true;
            }
        }
    }

    private static int FindMatchingEnd(string text, int beginPos)
    {
        int depth = 1;
        int i = beginPos + 5;
        while (i < text.Length && depth > 0)
        {
            if (text[i] == '\'') { i++; while (i < text.Length && text[i] != '\'') i++; i++; continue; }
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++; i += 2; continue; }

            if (IsKeywordAtPosition(text, i, "BEGIN"))
            {
                var after = i + 5;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (!IsKeywordAtPosition(text, after, "TRAN") && !IsKeywordAtPosition(text, after, "TRANSACTION"))
                    depth++;
                i += 5;
                continue;
            }
            if (IsKeywordAtPosition(text, i, "END"))
            {
                depth--;
                if (depth == 0) return i;
                i += 3;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int FindMatchingBegin(string text, int endPos)
    {
        // Scan backwards — simpler approach: collect all BEGIN/END positions forward, then match
        var pairs = new List<(int pos, bool isBegin)>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\'') { i++; while (i < text.Length && text[i] != '\'') i++; i++; continue; }
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++; i += 2; continue; }

            if (IsKeywordAtPosition(text, i, "BEGIN"))
            {
                var after = i + 5;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (!IsKeywordAtPosition(text, after, "TRAN") && !IsKeywordAtPosition(text, after, "TRANSACTION"))
                    pairs.Add((i, true));
                i += 5;
                continue;
            }
            if (IsKeywordAtPosition(text, i, "END"))
            {
                pairs.Add((i, false));
                i += 3;
                continue;
            }
            i++;
        }

        // Walk backwards from the target END to find its matching BEGIN
        int depth = 0;
        for (int j = pairs.Count - 1; j >= 0; j--)
        {
            if (pairs[j].pos == endPos) { depth = 1; continue; }
            if (depth == 0) continue;
            if (!pairs[j].isBegin) depth++;
            else { depth--; if (depth == 0) return pairs[j].pos; }
        }
        return -1;
    }

    private static bool IsKeywordAtPosition(string text, int pos, string keyword)
    {
        if (pos + keyword.Length > text.Length) return false;
        if (pos > 0 && char.IsLetterOrDigit(text[pos - 1])) return false;
        if (!text.AsSpan(pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        var after = pos + keyword.Length;
        return after >= text.Length || !char.IsLetterOrDigit(text[after]);
    }

    private static Color GetWordHighlightColor()
    {
        if (Application.Current?.Resources.TryGetResource("WordHighlight", null, out var res) == true
            && res is SolidColorBrush brush)
            return brush.Color;
        return Color.FromRgb(61, 53, 32); // fallback dark amber
    }

    // ── Executed Selection Flash ─────────────────────────────────────

    /// <summary>
    /// Briefly highlights the executed selection range (300ms) to confirm what was run.
    /// </summary>
    private void FlashExecutedSelection()
    {
        var selection = SqlEditor.TextArea.Selection;
        if (selection.IsEmpty) return;

        var startOffset = SqlEditor.SelectionStart;
        var endOffset = startOffset + SqlEditor.SelectionLength;
        if (startOffset >= endOffset) return;

        // Remove previous flash if still active
        if (_flashHighlighter != null)
        {
            SqlEditor.TextArea.TextView.LineTransformers.Remove(_flashHighlighter);
            _flashHighlighter = null;
        }

        var isDark = ThemeManager.IsDarkTheme;
        _flashHighlighter = new ExecutionFlashHighlighter
        {
            StartOffset = startOffset,
            EndOffset = endOffset,
            FlashColor = isDark
                ? Color.FromArgb(50, 80, 160, 255)   // subtle blue on dark
                : Color.FromArgb(50, 30, 100, 200)    // subtle blue on light
        };

        SqlEditor.TextArea.TextView.LineTransformers.Add(_flashHighlighter);
        SqlEditor.TextArea.TextView.Redraw();

        // Remove after 300ms
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_flashHighlighter != null)
            {
                SqlEditor.TextArea.TextView.LineTransformers.Remove(_flashHighlighter);
                _flashHighlighter = null;
                SqlEditor.TextArea.TextView.Redraw();
            }
        };
        timer.Start();
    }

    // ── Section 12: Move Line Up/Down ────────────────────────────────

    /// <summary>Handle Alt+Up/Down to move lines, Cmd/Ctrl+G for Go to Line.</summary>
    public bool HandleEditorKeyDown(KeyEventArgs e)
    {
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (alt && e.Key == Key.Up)
        {
            MoveLines(-1);
            e.Handled = true;
            return true;
        }
        if (alt && e.Key == Key.Down)
        {
            MoveLines(1);
            e.Handled = true;
            return true;
        }

        // Word Wrap Toggle: Alt+Z
        if (alt && e.Key == Key.Z)
        {
            SqlEditor.WordWrap = !SqlEditor.WordWrap;
            e.Handled = true;
            return true;
        }

        // Section 13: Go to Line
        if (ctrl && e.Key == Key.G)
        {
            ShowGoToLinePopup();
            e.Handled = true;
            return true;
        }

        // Comment: Cmd/Ctrl+K
        if (ctrl && !shift && e.Key == Key.K)
        {
            CommentLines();
            e.Handled = true;
            return true;
        }

        // Uncomment: Cmd/Ctrl+Shift+K
        if (ctrl && shift && e.Key == Key.K)
        {
            UncommentLines();
            e.Handled = true;
            return true;
        }

        // Uppercase: Cmd/Ctrl+Shift+U
        if (ctrl && shift && e.Key == Key.U)
        {
            TransformSelection(s => s.ToUpperInvariant());
            e.Handled = true;
            return true;
        }

        // Lowercase: Cmd/Ctrl+Shift+L
        if (ctrl && shift && e.Key == Key.L)
        {
            TransformSelection(s => s.ToLowerInvariant());
            e.Handled = true;
            return true;
        }

        return false;
    }

    public void CommentLines()
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            if (sel.EndPosition.Column == 1 && endLine > startLine) endLine--;
        }

        doc.BeginUpdate();
        for (var line = startLine; line <= endLine; line++)
        {
            var docLine = doc.GetLineByNumber(line);
            doc.Insert(docLine.Offset, "-- ");
        }
        doc.EndUpdate();
    }

    public void UncommentLines()
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            if (sel.EndPosition.Column == 1 && endLine > startLine) endLine--;
        }

        doc.BeginUpdate();
        for (var line = startLine; line <= endLine; line++)
        {
            var docLine = doc.GetLineByNumber(line);
            var text = doc.GetText(docLine.Offset, docLine.Length);
            var trimmed = text.TrimStart();
            var indent = text.Length - trimmed.Length;
            if (trimmed.StartsWith("-- "))
                doc.Remove(docLine.Offset + indent, 3);
            else if (trimmed.StartsWith("--"))
                doc.Remove(docLine.Offset + indent, 2);
        }
        doc.EndUpdate();
    }

    public void UppercaseSelection() => TransformSelection(s => s.ToUpperInvariant());
    public void LowercaseSelection() => TransformSelection(s => s.ToLowerInvariant());

    private void TransformSelection(Func<string, string> transform)
    {
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;
        if (sel.IsEmpty) return;

        var doc = SqlEditor.Document;
        var start = sel.SurroundingSegment.Offset;
        var length = sel.SurroundingSegment.Length;
        var text = doc.GetText(start, length);
        var transformed = transform(text);

        doc.BeginUpdate();
        doc.Replace(start, length, transformed);
        doc.EndUpdate();

        // Restore selection
        textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, start, start + transformed.Length);
    }

    private void MoveLines(int direction)
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            // If selection ends at column 1 of a line, don't include that line
            if (sel.EndPosition.Column == 1 && endLine > startLine)
                endLine--;
        }

        var targetLine = direction < 0 ? startLine - 1 : endLine + 1;
        if (targetLine < 1 || targetLine > doc.LineCount) return;

        // Get the block of lines to move
        var blockStart = doc.GetLineByNumber(startLine);
        var blockEnd = doc.GetLineByNumber(endLine);
        var blockOffset = blockStart.Offset;
        var blockLength = blockEnd.EndOffset - blockStart.Offset;
        var blockText = doc.GetText(blockOffset, blockLength);

        var swapDocLine = doc.GetLineByNumber(targetLine);
        var swapText = doc.GetText(swapDocLine.Offset, swapDocLine.Length);

        doc.BeginUpdate();
        try
        {
            if (direction < 0)
            {
                // Moving up: swap the line above with our block
                doc.Replace(blockStart.Offset, blockLength, swapText);
                doc.Replace(swapDocLine.Offset, swapDocLine.Length, blockText);
            }
            else
            {
                // Moving down: swap our block with the line below
                doc.Replace(swapDocLine.Offset, swapDocLine.Length, blockText);
                doc.Replace(blockStart.Offset, blockLength, swapText);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        // Move caret to follow the moved block
        var newStartLine = startLine + direction;
        var newEndLine = endLine + direction;
        var newCaretLine = textArea.Caret.Line + direction;
        if (newCaretLine >= 1 && newCaretLine <= doc.LineCount)
        {
            textArea.Caret.Line = newCaretLine;
        }
    }

    // ── Section 13: Go to Line ───────────────────────────────────────

    public void ShowGoToLinePopup()
    {
        if (_goToLineBox != null)
        {
            // Already showing — focus it
            _goToLineBox.Focus();
            _goToLineBox.SelectAll();
            return;
        }

        // Create lightweight input overlay at top of editor

        var box = new TextBox
        {
            Watermark = $"Go to line (1–{SqlEditor.Document.LineCount})",
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Width = 250,
            Margin = new Thickness(0, 4, 0, 0),
            ZIndex = 100
        };

        _goToLineBox = box;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (int.TryParse(box.Text, out var line) && line >= 1 && line <= SqlEditor.Document.LineCount)
                {
                    SqlEditor.TextArea.Caret.Line = line;
                    SqlEditor.TextArea.Caret.Column = 1;
                    SqlEditor.ScrollTo(line, 1);
                    SqlEditor.Focus();
                }
                CloseGoToLinePopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseGoToLinePopup();
                SqlEditor.Focus();
                e.Handled = true;
            }
        };

        box.LostFocus += (_, _) => CloseGoToLinePopup();

        // Add to the Grid at Row 0 (on top of editor)
        Grid.SetRow(box, 0);
        EditorResultsGrid.Children.Add(box);
        box.Focus();
    }

    private void CloseGoToLinePopup()
    {
        if (_goToLineBox != null)
        {
            EditorResultsGrid.Children.Remove(_goToLineBox);
            _goToLineBox = null;
        }
    }
}
