using System.Globalization;
using System.Text;

namespace Fusion.Common.TCP_Classes;

public static class TcpTextEncoding
{
    public static string DecodeEscapedSequence(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (current != '\\' || i + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            char next = value[++i];
            switch (next)
            {
                case '0':
                    builder.Append('\0');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case 'x' when i + 2 < value.Length && TryParseHex(value.AsSpan(i + 1, 2), out char hexChar):
                    builder.Append(hexChar);
                    i += 2;
                    break;
                case 'u' when i + 4 < value.Length && TryParseHex(value.AsSpan(i + 1, 4), out char unicodeChar):
                    builder.Append(unicodeChar);
                    i += 4;
                    break;
                default:
                    builder.Append('\\');
                    builder.Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool TryParseHex(ReadOnlySpan<char> value, out char decoded)
    {
        decoded = '\0';

        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
        {
            return false;
        }

        decoded = (char)codePoint;
        return true;
    }
}
