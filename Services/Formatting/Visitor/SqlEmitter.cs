using System;
using System.Collections.Generic;
using System.Text;

namespace SqlVersionControl.Services.Formatting.Visitor;

internal sealed class SqlEmitter
{
    private readonly StringBuilder _sb = new();
    private readonly FormatterOptions _options;
    private int _indentLevel;
    private bool _atLineStart = true;

    // Clause-scope alignment (right-pad keywords to widest in the current clause group).
    // 4b-ii: stack of buffers. The top of the stack is the active scope; an inner buffer
    // pop either flushes to _sb (if stack empties) or injects rendered lines into the new
    // top buffer's body.
    private readonly Stack<ClauseBuffer> _clauseStack = new();

    public SqlEmitter(FormatterOptions options)
    {
        _options = options;
    }

    public int IndentLevel => _indentLevel;

    // 4b-iii-b: surface whether the cursor is positioned at the start of a fresh line in the
    // underlying builder. Used by overrides that wrap an inner block in `(...)` — when flushing
    // at top level, the inner scope's trailing NewLine survives into _sb (unlike the
    // injected-into-parent case where EndClauseScope TrimEnds it), so the caller can skip a
    // redundant NewLine before the closing `)`. Only meaningful when no clause scope is active;
    // inside a clause scope, callers should unconditionally NewLine.
    public bool AtLineStart => _clauseStack.Count == 0 && _atLineStart;

    // 4b-iv: lets BooleanBinaryExpression decide whether AND/OR participates in a clause-keyword
    // right-alignment (true) or falls back to a generator scaffold (false). True iff a
    // BeginClauseScope is currently active anywhere in the stack.
    public bool InClauseScope => _clauseStack.Count > 0;

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_clauseStack.Count > 0) { _clauseStack.Peek().Write(text); return; }
        if (_atLineStart) WriteIndent();
        _sb.Append(text);
        _atLineStart = false;
    }

    public void WriteKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return;
        Write(_options.Uppercase ? keyword.ToUpperInvariant() : keyword.ToLowerInvariant());
    }

    /// <summary>
    /// Inside a <see cref="BeginClauseScope"/>, records the keyword for alignment. Outside a
    /// scope, falls back to <see cref="WriteKeyword"/> (no alignment available).
    /// </summary>
    public void WriteClauseKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return;
        if (_clauseStack.Count > 0) { _clauseStack.Peek().SetKeyword(keyword); return; }
        WriteKeyword(keyword);
    }

    public void WriteLine(string text = "")
    {
        if (!string.IsNullOrEmpty(text)) Write(text);
        if (_clauseStack.Count > 0) { _clauseStack.Peek().NewLine(); return; }
        _sb.Append('\n');
        _atLineStart = true;
    }

    public void NewLine()
    {
        if (_clauseStack.Count > 0) { _clauseStack.Peek().NewLine(); return; }
        _sb.Append('\n');
        _atLineStart = true;
    }

    public IDisposable Indent()
    {
        _indentLevel++;
        return new IndentScope(this);
    }

    /// <summary>
    /// Ensures the last emitted statement terminates with `;`. Used by body-recursion loops
    /// (procedure / BEGIN-END / TRY-CATCH) so that control-flow statements like ROLLBACK and
    /// BEGIN TRANSACTION — which the generator emits without trailing `;` — don't collide with
    /// subsequent statements at re-parse. Idempotent: if the content already ends with `;`,
    /// no change. Only meaningful outside any clause scope; inside a scope, callers shouldn't
    /// invoke this (the buffer model handles its own line endings).
    /// </summary>
    public void EnsureTrailingSemicolon()
    {
        if (_clauseStack.Count > 0) return;

        int i = _sb.Length - 1;
        while (i >= 0 && (_sb[i] == '\n' || _sb[i] == '\r' || _sb[i] == ' ' || _sb[i] == '\t'))
        {
            i--;
        }
        if (i < 0) return;
        if (_sb[i] == ';') return;

        _sb.Length = i + 1;
        _sb.Append(';');
        _sb.Append('\n');
        _atLineStart = true;
    }

    /// <summary>
    /// Opens a clause group. Inside: <see cref="WriteClauseKeyword"/> records keywords, plain
    /// <see cref="Write"/> appends to the current line's body, <see cref="NewLine"/> ends a line.
    /// On dispose, lines flush to output with each keyword left-padded so the last character of
    /// every keyword lands in the same column; body-only continuation lines indent to the body
    /// column (widest-keyword-length + 1). Set <see cref="FormatterOptions.AlignClauseBodies"/>
    /// to false to flush left-aligned with no padding.
    ///
    /// Nested scopes (4b-ii): <see cref="BeginClauseScope"/> may be called while another scope
    /// is active. Each call pushes a new buffer; on dispose, the popped buffer is rendered and
    /// its lines inject into the parent buffer's body (first line continues the parent's
    /// current line; each subsequent line becomes a body-only continuation in the parent).
    /// </summary>
    public IDisposable BeginClauseScope()
    {
        _clauseStack.Push(new ClauseBuffer(_indentLevel));
        return new ClauseScopeDisposer(this);
    }

    private void EndClauseScope()
    {
        if (_clauseStack.Count == 0) throw new InvalidOperationException("No active clause scope.");
        var buffer = _clauseStack.Pop();
        if (_clauseStack.Count == 0)
        {
            if (!_atLineStart) _sb.Append('\n');
            buffer.FlushTo(_sb, _options);
            _atLineStart = true;
            return;
        }

        // Nested pop: render the inner buffer to a string and inject into the parent's body.
        // The first rendered line continues the parent's current LineEntry; each subsequent
        // line becomes a body-only continuation in the parent. The parent's eventual flush
        // prepends its own outerIndent + (maxKw_parent + 1) to each continuation line — so
        // the parent's outerIndent gets added on top of whatever leading spaces the inner
        // buffer already wrote. To avoid double-counting the parent's indent (which was the
        // col-19 bug for QueryDerivedTable-inside-JOIN-inside-CTE: inner lines picked up
        // +IndentSize per nesting level past the visual minimum), strip parent.outerIndent
        // leading spaces from each injected line. Safe because FlushTo always writes
        // outerIndent chars before any keyword/body, and inner.captured >= parent.captured
        // (inner is nested deeper), so the inner's leading always has at least parent
        // outerIndent chars of spaces to strip.
        var temp = new StringBuilder();
        buffer.FlushTo(temp, _options);
        var rendered = temp.ToString().TrimEnd('\n');
        if (rendered.Length == 0) return;
        var parent = _clauseStack.Peek();
        int strip = parent.CapturedIndentLevel * _options.IndentSize;
        var lines = rendered.Split('\n');
        parent.Write(StripLeadingSpaces(lines[0], strip));
        for (int i = 1; i < lines.Length; i++)
        {
            parent.NewLine();
            parent.Write(StripLeadingSpaces(lines[i], strip));
        }
    }

    private void WriteIndent()
    {
        int spaces = _indentLevel * _options.IndentSize;
        for (int i = 0; i < spaces; i++) _sb.Append(' ');
    }

    private static string StripLeadingSpaces(string s, int count)
    {
        if (count <= 0 || s.Length == 0) return s;
        int skip = 0;
        while (skip < count && skip < s.Length && s[skip] == ' ') skip++;
        return skip == 0 ? s : s.Substring(skip);
    }

    public override string ToString() => _sb.ToString();

    private sealed class IndentScope : IDisposable
    {
        private readonly SqlEmitter _emitter;
        private bool _disposed;
        public IndentScope(SqlEmitter emitter) { _emitter = emitter; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _emitter._indentLevel--;
        }
    }

    private sealed class ClauseScopeDisposer : IDisposable
    {
        private readonly SqlEmitter _emitter;
        private bool _disposed;
        public ClauseScopeDisposer(SqlEmitter emitter) { _emitter = emitter; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _emitter.EndClauseScope();
        }
    }

    private sealed class ClauseBuffer
    {
        private readonly int _capturedIndentLevel;
        private readonly List<LineEntry> _lines = new() { new LineEntry() };

        public ClauseBuffer(int indentLevel) { _capturedIndentLevel = indentLevel; }

        public int CapturedIndentLevel => _capturedIndentLevel;

        public void SetKeyword(string kw)
        {
            var current = _lines[^1];
            if (current.Keyword != null || current.Body.Length > 0)
            {
                _lines.Add(new LineEntry());
                current = _lines[^1];
            }
            current.Keyword = kw;
        }

        public void Write(string s) => _lines[^1].Body.Append(s);

        public void NewLine() => _lines.Add(new LineEntry());

        public void FlushTo(StringBuilder sb, FormatterOptions options)
        {
            int maxKw = 0;
            if (options.AlignClauseBodies)
            {
                foreach (var line in _lines)
                {
                    if (line.Keyword != null && line.Keyword.Length > maxKw) maxKw = line.Keyword.Length;
                }
            }

            int outerIndent = _capturedIndentLevel * options.IndentSize;
            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                bool isTrailingEmpty = i == _lines.Count - 1 && line.Keyword == null && line.Body.Length == 0;
                if (isTrailingEmpty) continue;

                AppendSpaces(sb, outerIndent);
                if (line.Keyword != null)
                {
                    int pad = options.AlignClauseBodies ? maxKw - line.Keyword.Length : 0;
                    if (pad > 0) AppendSpaces(sb, pad);
                    sb.Append(options.Uppercase ? line.Keyword.ToUpperInvariant() : line.Keyword.ToLowerInvariant());
                    if (line.Body.Length > 0)
                    {
                        sb.Append(' ');
                        sb.Append(line.Body);
                    }
                }
                else if (line.Body.Length > 0)
                {
                    if (options.AlignClauseBodies) AppendSpaces(sb, maxKw + 1);
                    sb.Append(line.Body);
                }
                sb.Append('\n');
            }
        }

        private static void AppendSpaces(StringBuilder sb, int count)
        {
            for (int i = 0; i < count; i++) sb.Append(' ');
        }

        private sealed class LineEntry
        {
            public string? Keyword;
            public StringBuilder Body = new();
        }
    }
}
