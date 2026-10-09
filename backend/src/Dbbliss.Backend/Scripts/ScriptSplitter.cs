namespace Dbbliss.Backend.Scripts;

/// <summary>How an engine's scripts divide into the units the server is sent one at a time.</summary>
public enum ScriptDialect
{
    /// <summary>Statements end at a semicolon (PostgreSQL, DB2 for i).</summary>
    Semicolon,

    /// <summary>Batches end at a line holding only GO, optionally followed by a repeat count (SQL Server).</summary>
    GoBatches,
}

/// <summary>A line and column in the source text, both 0-based. A column counts UTF-16 code units.</summary>
public readonly record struct SourcePosition(int Line, int Column);

/// <param name="Text">What to send to the server: the source from <paramref name="Start"/> to <paramref name="End"/>.</param>
/// <param name="End">Exclusive.</param>
/// <param name="Repeat">The count after GO; 1 otherwise.</param>
public sealed record ScriptUnit(string Text, SourcePosition Start, SourcePosition End, int Repeat = 1);

/// <summary>
/// Splits a script into statements (or batches) without a database. It knows enough of each
/// dialect's lexical rules that a terminator inside a string, comment, quoted identifier or
/// dollar-quoted body does not split: comments (block comments nest), single-quoted strings,
/// double-quoted identifiers, [bracketed] identifiers on SQL Server, E'...' strings, dollar quoting
/// and BEGIN ATOMIC bodies on PostgreSQL. Text it cannot terminate (an unclosed string) runs to the end.
///
/// A unit starts at its first character after the previous terminator that is not whitespace, so
/// comments above a statement belong to it; a comment on the line where the previous statement ended
/// does not. Units holding only comments are dropped.
/// </summary>
public static class ScriptSplitter
{
    public static IReadOnlyList<ScriptUnit> Split(string text, ScriptDialect dialect)
    {
        var lexer = new Lexer(text, dialect);
        lexer.Run();
        return lexer.Units;
    }

    /// <summary>
    /// The words of <paramref name="text"/> outside strings, comments, quoted identifiers and dollar
    /// quotes, in order (as written, not lower-cased). Used to classify statements.
    /// </summary>
    public static IReadOnlyList<string> Words(string text, ScriptDialect dialect)
    {
        var lexer = new Lexer(text, dialect) { WordsOut = [] };
        lexer.Run();
        return lexer.WordsOut;
    }

    private sealed class Lexer(string s, ScriptDialect dialect)
    {
        public List<string>? WordsOut { get; init; }

        private readonly bool _go = dialect == ScriptDialect.GoBatches;
        private readonly List<int> _lineStarts = LineStarts(s);
        private int _i;
        private int _unitStart = -1;
        private int _contentEnd = -1;
        private bool _hasContent;
        private int _terminatorLine = -1;
        private string _prevWord = "";
        private int _prevWordEnd = -1;
        private int _atomicDepth;

        public List<ScriptUnit> Units { get; } = [];

        public void Run()
        {
            while (_i < s.Length)
            {
                var c = s[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                }
                else if (c == '-' && Peek(1) == '-')
                {
                    var start = _i;
                    SkipLineComment();
                    MarkComment(start);
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    var start = _i;
                    SkipBlockComment();
                    MarkComment(start);
                }
                else if (c == '\'')
                {
                    var backslash = !_go && _prevWordEnd == _i && _prevWord.Equals("E", StringComparison.OrdinalIgnoreCase);
                    Content();
                    SkipQuoted('\'', backslash);
                    EndContent();
                }
                else if (c == '"')
                {
                    Content();
                    SkipQuoted('"', backslash: false);
                    EndContent();
                }
                else if (c == '[' && _go)
                {
                    Content();
                    SkipQuoted(']', backslash: false);
                    EndContent();
                }
                else if (c == '$' && !_go && TryDollarQuote())
                {
                }
                else if (c == ';' && !_go && _atomicDepth == 0)
                {
                    _i++;
                    if (_hasContent) AddUnit(_unitStart, _i, 1);
                    _terminatorLine = LineOf(_i - 1);
                    Reset();
                }
                else if (IsWordStart(c))
                {
                    var start = _i;
                    while (_i < s.Length && IsWordChar(s[_i])) _i++;
                    var word = s[start.._i];
                    if (_go && word.Equals("GO", StringComparison.OrdinalIgnoreCase) && TryGoLine(start))
                    {
                        continue;
                    }
                    Content(start);
                    EndContent();
                    Track(word);
                    WordsOut?.Add(word);
                }
                else
                {
                    Content();
                    _i++;
                    EndContent();
                }
            }
            if (_hasContent) AddUnit(_unitStart, _contentEnd, 1);
        }

        // BEGIN ATOMIC ... END (a SQL-standard function body) holds semicolons that do not end the
        // statement. CASE ... END nests inside it.
        private void Track(string word)
        {
            if (!_go)
            {
                if (_atomicDepth > 0)
                {
                    if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase)) _atomicDepth++;
                    else if (word.Equals("END", StringComparison.OrdinalIgnoreCase)) _atomicDepth--;
                }
                else if (word.Equals("ATOMIC", StringComparison.OrdinalIgnoreCase) && _prevWord.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    _atomicDepth = 1;
                }
            }
            _prevWord = word;
            _prevWordEnd = _i;
        }

        private void Content(int? start = null)
        {
            if (_unitStart < 0) _unitStart = start ?? _i;
            _hasContent = true;
        }

        private void EndContent() => _contentEnd = _i;

        private void MarkComment(int start)
        {
            // A comment on the line where the previous statement ended trails that statement.
            if (_unitStart < 0 && LineOf(start) != _terminatorLine) _unitStart = start;
        }

        private void Reset()
        {
            _unitStart = -1;
            _contentEnd = -1;
            _hasContent = false;
            _atomicDepth = 0;
            _prevWord = "";
            _prevWordEnd = -1;
        }

        private void AddUnit(int start, int end, int repeat)
        {
            Units.Add(new ScriptUnit(s[start..end], PositionOf(start), PositionOf(end), repeat));
        }

        private char Peek(int offset) => _i + offset < s.Length ? s[_i + offset] : '\0';

        private void SkipLineComment()
        {
            while (_i < s.Length && s[_i] != '\n') _i++;
        }

        // Block comments nest in both PostgreSQL and T-SQL.
        private void SkipBlockComment()
        {
            var depth = 0;
            while (_i < s.Length)
            {
                if (s[_i] == '/' && Peek(1) == '*')
                {
                    depth++;
                    _i += 2;
                }
                else if (s[_i] == '*' && Peek(1) == '/')
                {
                    depth--;
                    _i += 2;
                    if (depth == 0) return;
                }
                else
                {
                    _i++;
                }
            }
        }

        /// <summary>Skips a quoted run, from its opening character; a doubled closing quote is an escaped quote.</summary>
        private void SkipQuoted(char close, bool backslash)
        {
            _i++;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (backslash && c == '\\')
                {
                    _i += 2;
                }
                else if (c == close)
                {
                    if (Peek(1) == close)
                    {
                        _i += 2;
                        continue;
                    }
                    _i++;
                    return;
                }
                else
                {
                    _i++;
                }
            }
            _i = s.Length;
        }

        /// <summary>$tag$ ... $tag$ with an optional tag. $1 is a parameter, not a quote.</summary>
        private bool TryDollarQuote()
        {
            var start = _i;
            var j = _i + 1;
            if (j < s.Length && IsWordStart(s[j]))
            {
                while (j < s.Length && s[j] != '$' && IsWordChar(s[j])) j++;
            }
            if (j >= s.Length || s[j] != '$') return false;
            var tag = s[start..(j + 1)];
            Content(start);
            var close = s.IndexOf(tag, j + 1, StringComparison.Ordinal);
            _i = close < 0 ? s.Length : close + tag.Length;
            EndContent();
            return true;
        }

        /// <summary>A GO line: only whitespace before it, then an optional count and an optional comment.</summary>
        private bool TryGoLine(int wordStart)
        {
            var lineStart = _lineStarts[LineOf(wordStart)];
            for (var k = lineStart; k < wordStart; k++)
            {
                if (!char.IsWhiteSpace(s[k])) return false;
            }
            var j = SkipBlanks(_i);
            var repeat = 1;
            var digits = j;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j > digits)
            {
                if (!int.TryParse(s.AsSpan(digits, j - digits), out repeat) || repeat < 1) return false;
                j = SkipBlanks(j);
            }
            if (j + 1 < s.Length && s[j] == '-' && s[j + 1] == '-')
            {
                while (j < s.Length && s[j] != '\n') j++;
            }
            if (j < s.Length && s[j] != '\n') return false;
            if (_hasContent) AddUnit(_unitStart, _contentEnd, repeat);
            Reset();
            _terminatorLine = -1;
            _i = j;
            return true;
        }

        private int SkipBlanks(int j)
        {
            while (j < s.Length && s[j] != '\n' && char.IsWhiteSpace(s[j])) j++;
            return j;
        }

        private int LineOf(int offset)
        {
            var index = _lineStarts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        private SourcePosition PositionOf(int offset)
        {
            var line = LineOf(offset);
            return new SourcePosition(line, offset - _lineStarts[line]);
        }

        private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c > 127;

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c > 127;

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') starts.Add(i + 1);
            }
            return starts;
        }
    }
}
