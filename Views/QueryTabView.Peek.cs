using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    /// <summary>Fired when user Cmd/Ctrl+Clicks a word — host fetches definition and calls ShowPeekDefinition.</summary>
    public event Func<string, Task<string?>>? PeekDefinitionRequested;

    /// <summary>Fired when user Shift+Clicks a word — host fetches params and opens exec template.</summary>
    public event Func<string, Task>? QuickExecuteRequested;

    /// <summary>Fired when context menu requests Format SQL (lives on host).</summary>
    public event Action? FormatSqlRequested;

    /// <summary>Fired when context menu requests Quick Quote (lives on host).</summary>
    public event Action? QuickQuoteRequested;

    /// <summary>Fired when context menu requests Show Dependencies for a word.</summary>
    public event Action<string>? ShowDependenciesRequested;

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(SqlEditor);
        if (!point.Properties.IsLeftButtonPressed) return;

        var mods = e.KeyModifiers;
        var hasCmdCtrl = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
        var hasShift = mods.HasFlag(KeyModifiers.Shift);

        if (!hasCmdCtrl && !hasShift) return;

        var word = GetWordAtCaret();
        if (string.IsNullOrWhiteSpace(word)) return;

        // Strip square brackets if present: [dbo].[MyProc] → MyProc
        word = word.Trim('[', ']');
        if (word.Length == 0) return;

        if (hasShift && !hasCmdCtrl)
        {
            // Shift+Click → Quick Execute
            _ = QuickExecuteRequested?.Invoke(word);
        }
        else if (hasCmdCtrl)
        {
            // Cmd/Ctrl+Click → Peek Definition
            _ = PeekDefinitionAsync(word);
        }
        e.Handled = true;
    }

    private string? GetWordAtCaret()
    {
        var doc = SqlEditor.Document;
        var offset = SqlEditor.CaretOffset;
        if (offset < 0 || offset > doc.TextLength) return null;

        // Expand left and right to find word boundaries (letters, digits, underscore, brackets, dot)
        int start = offset, end = offset;
        while (start > 0 && IsWordChar(doc.GetCharAt(start - 1))) start--;
        while (end < doc.TextLength && IsWordChar(doc.GetCharAt(end))) end++;

        if (start == end) return null;
        return doc.GetText(start, end - start);
    }

    private static bool IsWordChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '[' || c == ']' || c == '.' || c == '#';

    private async Task PeekDefinitionAsync(string objectName)
    {
        if (PeekDefinitionRequested == null) return;

        var definition = await PeekDefinitionRequested.Invoke(objectName);
        if (definition == null)
        {
            // Show "not found" briefly in the peek panel
            ShowPeekPanel($"Peek: {objectName}", $"-- Object '{objectName}' not found or is not a scriptable object");
            return;
        }

        ShowPeekPanel($"Peek: {objectName}", definition);
    }

    private void ShowPeekPanel(string title, string content)
    {
        PeekTitle.Text = title;
        PeekEditor.Text = content;

        // Apply syntax highlighting from main editor
        if (SqlEditor.SyntaxHighlighting != null)
            PeekEditor.SyntaxHighlighting = SqlEditor.SyntaxHighlighting;
        ApplyThemeToEditor(PeekEditor);

        // Show peek, hide other result panels
        ResultsGrid.IsVisible = false;
        MessagesPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        PeekPanel.IsVisible = true;

        // Expand results panel if collapsed
        if (_resultsCollapsed)
        {
            _resultsCollapsed = false;
            var totalHeight = EditorResultsGrid.Bounds.Height;
            var peekHeight = totalHeight > 0 ? totalHeight * 0.4 : 250;
            EditorResultsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(peekHeight, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC"; // ▼
        }
    }

    private void ClosePeekPanel()
    {
        PeekPanel.IsVisible = false;
        // Restore previous result view
        if (_viewModel?.Results.Count > 0 || _pinnedResults.Count > 0)
        {
            if (_selectedTabIndex != MessagesTabTag && _selectedTabIndex != -1)
                SelectResultTab(_selectedTabIndex);
            else
                SelectMessagesTab();
        }
        else
        {
            EmptyState.IsVisible = true;
        }
    }

    private void ApplyThemeToEditor(TextEditor editor)
    {
        editor.Background = new SolidColorBrush(ThemeManager.GetDiffBackground());
        editor.Foreground = new SolidColorBrush(ThemeManager.GetIdentifierColor());
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        ShowEditorContextMenu(e);
        e.Handled = true;
    }

    // ── Editor Right-Click Context Menu ─────────────────────────────

    private void ShowEditorContextMenu(PointerReleasedEventArgs e)
    {
        var menu = new MenuFlyout();
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var mod = isMac ? "Cmd" : "Ctrl";

        // Clipboard
        menu.Items.Add(CreateEditorMenuItem("Cut", $"{mod}+X", () => SqlEditor.Cut()));
        menu.Items.Add(CreateEditorMenuItem("Copy", $"{mod}+C", () => SqlEditor.Copy()));
        menu.Items.Add(CreateEditorMenuItem("Paste", $"{mod}+V", () => SqlEditor.Paste()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateEditorMenuItem("Select All", $"{mod}+A", () => SqlEditor.SelectAll()));
        menu.Items.Add(new Separator());

        // Formatting
        menu.Items.Add(CreateEditorMenuItem("Format SQL", "Ctrl+Shift+F", () => FormatSqlRequested?.Invoke()));
        menu.Items.Add(CreateEditorMenuItem("Comment Lines", $"{mod}+K", CommentLines));
        menu.Items.Add(CreateEditorMenuItem("Uncomment Lines", $"{mod}+L", UncommentLines));
        menu.Items.Add(CreateEditorMenuItem("Uppercase", $"{mod}+Shift+U", () => TransformSelection(s => s.ToUpperInvariant())));
        menu.Items.Add(CreateEditorMenuItem("Lowercase", $"{mod}+Shift+L", () => TransformSelection(s => s.ToLowerInvariant())));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateEditorMenuItem("Quick Quote Selection", "Ctrl+Shift+Q", () => QuickQuoteRequested?.Invoke()));
        menu.Items.Add(new Separator());

        // Navigation
        menu.Items.Add(CreateEditorMenuItem("Go to Line...", $"{mod}+G", ShowGoToLinePopup));
        menu.Items.Add(CreateEditorMenuItem("Find", $"{mod}+F", () => AvaloniaEdit.Search.SearchPanel.Install(SqlEditor)));
        menu.Items.Add(CreateEditorMenuItem("Replace", $"{mod}+H", () => AvaloniaEdit.Search.SearchPanel.Install(SqlEditor)));

        // Contextual items — only if cursor is on a word
        var word = GetWordAtCaret();
        if (!string.IsNullOrEmpty(word))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateEditorMenuItem($"Peek Definition: {word}", $"{mod}+Click", () => _ = PeekDefinitionAsync(word)));
            menu.Items.Add(CreateEditorMenuItem($"Quick Execute: {word}", "Shift+Click", () => _ = QuickExecuteRequested?.Invoke(word)));
            menu.Items.Add(CreateEditorMenuItem($"Show Dependencies: {word}", "", () => ShowDependenciesRequested?.Invoke(word)));
        }

        menu.ShowAt(SqlEditor, true);
    }

    private static MenuItem CreateEditorMenuItem(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (!string.IsNullOrEmpty(gesture))
            item.InputGesture = null; // just display text, not bound
        item.Click += (_, _) => action();

        // Show shortcut hint as right-aligned text
        if (!string.IsNullOrEmpty(gesture))
            item.Header = $"{header}";

        return item;
    }
}
