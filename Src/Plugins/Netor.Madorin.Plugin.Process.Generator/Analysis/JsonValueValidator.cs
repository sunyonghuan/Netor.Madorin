using System;

namespace Netor.Madorin.Plugin.Process.Generator.Analysis;

internal static class JsonValueValidator
{
    public static bool IsValid(string value)
    {
        var reader = new Reader(value);
        reader.SkipWhiteSpace();
        if (!reader.TryReadValue())
        {
            return false;
        }

        reader.SkipWhiteSpace();
        return reader.IsEnd;
    }

    private sealed class Reader(string text)
    {
        private int _index;

        public bool IsEnd => _index >= text.Length;

        public void SkipWhiteSpace()
        {
            while (!IsEnd && text[_index] is ' ' or '\t' or '\r' or '\n')
            {
                _index++;
            }
        }

        public bool TryReadValue()
        {
            SkipWhiteSpace();
            if (IsEnd)
            {
                return false;
            }

            return text[_index] switch
            {
                '"' => TryReadString(),
                '{' => TryReadObject(),
                '[' => TryReadArray(),
                't' => TryReadLiteral("true"),
                'f' => TryReadLiteral("false"),
                'n' => TryReadLiteral("null"),
                '-' or >= '0' and <= '9' => TryReadNumber(),
                _ => false
            };
        }

        private bool TryReadObject()
        {
            _index++;
            SkipWhiteSpace();
            if (TryReadChar('}'))
            {
                return true;
            }

            while (true)
            {
                SkipWhiteSpace();
                if (!TryReadString())
                {
                    return false;
                }

                SkipWhiteSpace();
                if (!TryReadChar(':') || !TryReadValue())
                {
                    return false;
                }

                SkipWhiteSpace();
                if (TryReadChar('}'))
                {
                    return true;
                }

                if (!TryReadChar(','))
                {
                    return false;
                }
            }
        }

        private bool TryReadArray()
        {
            _index++;
            SkipWhiteSpace();
            if (TryReadChar(']'))
            {
                return true;
            }

            while (true)
            {
                if (!TryReadValue())
                {
                    return false;
                }

                SkipWhiteSpace();
                if (TryReadChar(']'))
                {
                    return true;
                }

                if (!TryReadChar(','))
                {
                    return false;
                }
            }
        }

        private bool TryReadString()
        {
            if (!TryReadChar('"'))
            {
                return false;
            }

            while (!IsEnd)
            {
                var ch = text[_index++];
                if (ch == '"')
                {
                    return true;
                }

                if (ch < 0x20)
                {
                    return false;
                }

                if (ch == '\\' && !TryReadEscape())
                {
                    return false;
                }
            }

            return false;
        }

        private bool TryReadEscape()
        {
            if (IsEnd)
            {
                return false;
            }

            var ch = text[_index++];
            if (ch is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
            {
                return true;
            }

            if (ch != 'u')
            {
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                if (IsEnd || !Uri.IsHexDigit(text[_index++]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryReadNumber()
        {
            if (TryReadChar('-') && IsEnd)
            {
                return false;
            }

            if (TryReadChar('0'))
            {
                if (!IsEnd && IsDigit(text[_index]))
                {
                    return false;
                }
            }
            else if (!TryReadDigits())
            {
                return false;
            }

            if (TryReadChar('.'))
            {
                if (!TryReadDigits())
                {
                    return false;
                }
            }

            if (!IsEnd && text[_index] is 'e' or 'E')
            {
                _index++;
                _ = TryReadChar('+') || TryReadChar('-');
                if (!TryReadDigits())
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryReadDigits()
        {
            var start = _index;
            while (!IsEnd && IsDigit(text[_index]))
            {
                _index++;
            }

            return _index > start;
        }

        private bool TryReadLiteral(string literal)
        {
            if (_index + literal.Length > text.Length)
            {
                return false;
            }

            for (var i = 0; i < literal.Length; i++)
            {
                if (text[_index + i] != literal[i])
                {
                    return false;
                }
            }

            _index += literal.Length;
            return true;
        }

        private static bool IsDigit(char ch)
        {
            return ch is >= '0' and <= '9';
        }

        private bool TryReadChar(char ch)
        {
            if (IsEnd || text[_index] != ch)
            {
                return false;
            }

            _index++;
            return true;
        }
    }
}
