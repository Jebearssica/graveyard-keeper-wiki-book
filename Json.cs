using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WikiBook;

/// <summary>
///     Minimal JSON reader for the shipped dataset. The plugin targets net481 and must not add a dependency that
///     BepInEx does not already ship, so the schema is read with this small recursive-descent parser instead of
///     System.Text.Json or Newtonsoft.Json. Values map to Dictionary&lt;string, object&gt;, List&lt;object&gt;,
///     string, double, bool and null.
/// </summary>
internal static class Json
{
    internal static object Parse(string text)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text);
        var value = parser.ParseValue();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw parser.Error("unexpected trailing content");
        }

        return value;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;

        internal Parser(string text)
        {
            _text = text;
        }

        internal bool AtEnd => _index >= _text.Length;

        internal object ParseValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("unexpected end of input");
            }

            var ch = _text[_index];
            switch (ch)
            {
                case '{':
                    return ParseObject();
                case '[':
                    return ParseArray();
                case '"':
                    return ParseString();
                case 't':
                    Expect("true");
                    return true;
                case 'f':
                    Expect("false");
                    return false;
                case 'n':
                    Expect("null");
                    return null;
                default:
                    if (ch == '-' || char.IsDigit(ch))
                    {
                        return ParseNumber();
                    }

                    throw Error("unexpected character '" + ch + "'");
            }
        }

        internal void SkipWhitespace()
        {
            while (!AtEnd)
            {
                var ch = _text[_index];
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    _index++;
                    continue;
                }

                break;
            }
        }

        internal FormatException Error(string message)
        {
            var line = 1;
            var column = 1;
            var limit = Math.Min(_index, _text.Length);
            for (var i = 0; i < limit; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new FormatException("JSON " + message + " at line " + line + ", column " + column + ".");
        }

        private Dictionary<string, object> ParseObject()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            _index++; // {
            SkipWhitespace();
            if (Peek('}'))
            {
                _index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (!Peek('"'))
                {
                    throw Error("expected a property name");
                }

                var key = ParseString();
                SkipWhitespace();
                Expect(":");
                var value = ParseValue();
                result[key] = value;
                SkipWhitespace();
                if (Peek(','))
                {
                    _index++;
                    continue;
                }

                if (Peek('}'))
                {
                    _index++;
                    return result;
                }

                throw Error("expected ',' or '}'");
            }
        }

        private List<object> ParseArray()
        {
            var result = new List<object>();
            _index++; // [
            SkipWhitespace();
            if (Peek(']'))
            {
                _index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue());
                SkipWhitespace();
                if (Peek(','))
                {
                    _index++;
                    continue;
                }

                if (Peek(']'))
                {
                    _index++;
                    return result;
                }

                throw Error("expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            var builder = new StringBuilder();
            _index++; // opening quote
            while (!AtEnd)
            {
                var ch = _text[_index++];
                if (ch == '"')
                {
                    return builder.ToString();
                }

                if (ch != '\\')
                {
                    builder.Append(ch);
                    continue;
                }

                if (AtEnd)
                {
                    break;
                }

                var escape = _text[_index++];
                switch (escape)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (_index + 4 > _text.Length)
                        {
                            throw Error("truncated unicode escape");
                        }

                        var hex = _text.Substring(_index, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("invalid unicode escape");
                        }

                        builder.Append((char)code);
                        _index += 4;
                        break;
                    default:
                        throw Error("invalid escape '\\" + escape + "'");
                }
            }

            throw Error("unterminated string");
        }

        private double ParseNumber()
        {
            var start = _index;
            if (Peek('-'))
            {
                _index++;
            }

            while (!AtEnd && char.IsDigit(_text[_index]))
            {
                _index++;
            }

            if (!AtEnd && _text[_index] == '.')
            {
                _index++;
                while (!AtEnd && char.IsDigit(_text[_index]))
                {
                    _index++;
                }
            }

            if (!AtEnd && (_text[_index] == 'e' || _text[_index] == 'E'))
            {
                _index++;
                if (!AtEnd && (_text[_index] == '+' || _text[_index] == '-'))
                {
                    _index++;
                }

                while (!AtEnd && char.IsDigit(_text[_index]))
                {
                    _index++;
                }
            }

            var slice = _text.Substring(start, _index - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw Error("invalid number '" + slice + "'");
            }

            return value;
        }

        private void Expect(string expected)
        {
            if (_index + expected.Length > _text.Length ||
                string.CompareOrdinal(_text, _index, expected, 0, expected.Length) != 0)
            {
                throw Error("expected '" + expected + "'");
            }

            _index += expected.Length;
        }

        private bool Peek(char expected)
        {
            return !AtEnd && _text[_index] == expected;
        }
    }
}
