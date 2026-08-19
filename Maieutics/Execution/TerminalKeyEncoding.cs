using System.Text;
using Maieutics.Agent;
using XTerm.Input;

namespace Maieutics.Execution;

/// <summary>The closed vim-notation key names accepted by the <c>k</c> input line.</summary>
internal enum TerminalKeyName
{
    Enter,
    Escape,
    Tab,
    Backspace,
    Delete,
    Space,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12
}

[Flags]
internal enum TerminalKeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

internal abstract record TerminalKeySpec;

internal sealed record TerminalSpecialKey(TerminalKeyName Name, TerminalKeyModifiers Modifiers) : TerminalKeySpec;

internal sealed record TerminalCharKey(char Character, TerminalKeyModifiers Modifiers) : TerminalKeySpec;

internal sealed record TerminalKey(int? Count, TerminalKeySpec Spec);

/// <summary>
///     Parses the <c>terminal_input</c> input batch into encoded write operations. Parsing is purely
///     static: every line is validated before the batch is executed, so a syntax error never leaves a
///     partially-sent batch. The byte encoding of key tokens is deferred to <see cref="TerminalKeyEncoder"/>,
///     which knows the terminal mode at write time.
/// </summary>
internal static class TerminalInputBatchParser
{
    private const string TextPrefix = "t ";
    private const string KeyPrefix = "k ";

    private static readonly IReadOnlyDictionary<string, TerminalKeyName> SpecialKeyNames =
        new Dictionary<string, TerminalKeyName>(StringComparer.OrdinalIgnoreCase)
        {
            ["CR"] = TerminalKeyName.Enter,
            ["Enter"] = TerminalKeyName.Enter,
            ["Return"] = TerminalKeyName.Enter,
            ["Esc"] = TerminalKeyName.Escape,
            ["Tab"] = TerminalKeyName.Tab,
            ["BS"] = TerminalKeyName.Backspace,
            ["Backspace"] = TerminalKeyName.Backspace,
            ["Del"] = TerminalKeyName.Delete,
            ["Delete"] = TerminalKeyName.Delete,
            ["Space"] = TerminalKeyName.Space,
            ["Up"] = TerminalKeyName.Up,
            ["Down"] = TerminalKeyName.Down,
            ["Left"] = TerminalKeyName.Left,
            ["Right"] = TerminalKeyName.Right,
            ["Home"] = TerminalKeyName.Home,
            ["End"] = TerminalKeyName.End,
            ["PageUp"] = TerminalKeyName.PageUp,
            ["PgUp"] = TerminalKeyName.PageUp,
            ["PageDown"] = TerminalKeyName.PageDown,
            ["PgDn"] = TerminalKeyName.PageDown,
            ["F1"] = TerminalKeyName.F1,
            ["F2"] = TerminalKeyName.F2,
            ["F3"] = TerminalKeyName.F3,
            ["F4"] = TerminalKeyName.F4,
            ["F5"] = TerminalKeyName.F5,
            ["F6"] = TerminalKeyName.F6,
            ["F7"] = TerminalKeyName.F7,
            ["F8"] = TerminalKeyName.F8,
            ["F9"] = TerminalKeyName.F9,
            ["F10"] = TerminalKeyName.F10,
            ["F11"] = TerminalKeyName.F11,
            ["F12"] = TerminalKeyName.F12
        };

    private static readonly IReadOnlyDictionary<TerminalKeyName, Key> XTermKeys =
        new Dictionary<TerminalKeyName, Key>
        {
            [TerminalKeyName.Enter] = Key.Enter,
            [TerminalKeyName.Escape] = Key.Escape,
            [TerminalKeyName.Tab] = Key.Tab,
            [TerminalKeyName.Backspace] = Key.Backspace,
            [TerminalKeyName.Delete] = Key.Delete,
            [TerminalKeyName.Space] = Key.Space,
            [TerminalKeyName.Up] = Key.UpArrow,
            [TerminalKeyName.Down] = Key.DownArrow,
            [TerminalKeyName.Left] = Key.LeftArrow,
            [TerminalKeyName.Right] = Key.RightArrow,
            [TerminalKeyName.Home] = Key.Home,
            [TerminalKeyName.End] = Key.End,
            [TerminalKeyName.PageUp] = Key.PageUp,
            [TerminalKeyName.PageDown] = Key.PageDown,
            [TerminalKeyName.F1] = Key.F1,
            [TerminalKeyName.F2] = Key.F2,
            [TerminalKeyName.F3] = Key.F3,
            [TerminalKeyName.F4] = Key.F4,
            [TerminalKeyName.F5] = Key.F5,
            [TerminalKeyName.F6] = Key.F6,
            [TerminalKeyName.F7] = Key.F7,
            [TerminalKeyName.F8] = Key.F8,
            [TerminalKeyName.F9] = Key.F9,
            [TerminalKeyName.F10] = Key.F10,
            [TerminalKeyName.F11] = Key.F11,
            [TerminalKeyName.F12] = Key.F12
        };

    /// <summary>The maximum repeat count for one key token; bounds the bytes one input line can write.</summary>
    private const int MaximumKeyCount = 10_000;

    internal static TerminalInputBatch Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // A trailing newline is a common model habit; treat it as an empty final line rather than an error.
        var normalized = input.EndsWith('\n') ? input[..^1] : input;
        var lines = normalized.Split('\n');
        var operations = new List<TerminalInputOperation>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            if (line.StartsWith(TextPrefix, StringComparison.Ordinal))
            {
                var payload = line[TextPrefix.Length..];
                if (ContainsForbiddenControl(payload))
                    throw InvalidInput(lineNumber, "text payloads cannot contain control characters other than tab.");

                operations.Add(new TerminalTextOperation(lineNumber, payload));
                continue;
            }

            if (line.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                var payload = line[KeyPrefix.Length..];
                var keys = ParseKeyTokens(payload, lineNumber);
                operations.Add(new TerminalKeysOperation(lineNumber, keys));
                continue;
            }

            throw InvalidInput(lineNumber, "every line must start with 't ' or 'k '.");
        }

        return new TerminalInputBatch(operations, lines.Length);
    }

    internal static bool ContainsForbiddenControl(string text)
    {
        foreach (var character in text)
            if ((character < 0x20 && character != '\t') || character == 0x7f)
                return true;

        return false;
    }

    private static IReadOnlyList<TerminalKey> ParseKeyTokens(string payload, int lineNumber)
    {
        if (payload.Length == 0)
            throw InvalidInput(lineNumber, "the 'k ' line requires at least one key token.");

        var keys = new List<TerminalKey>();
        var offset = 0;
        while (offset < payload.Length)
        {
            while (offset < payload.Length && payload[offset] == ' ')
                offset++;
            if (offset >= payload.Length) break;

            int? count = null;
            if (char.IsAsciiDigit(payload[offset]))
            {
                var digits = 0;
                while (offset + digits < payload.Length && char.IsAsciiDigit(payload[offset + digits])) digits++;
                var countText = payload[offset..(offset + digits)];
                if (countText.Length > 1 && countText[0] == '0')
                    throw InvalidInput(lineNumber, $"key count '{countText}' cannot have leading zeros.");

                // int.TryParse keeps a maliciously long count from throwing OverflowException out of the
                // parser; the cap bounds the bytes one token can write.
                if (!int.TryParse(countText, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                    parsed < 1 || parsed > MaximumKeyCount)
                    throw InvalidInput(lineNumber, $"key count '{countText}' must be between 1 and {MaximumKeyCount}.");

                count = parsed;
                offset += digits;
            }

            if (offset >= payload.Length || payload[offset] != '<')
                throw InvalidInput(lineNumber, "a key token must start with '<'.");

            var end = payload.IndexOf('>', offset + 1);
            if (end < 0)
                throw InvalidInput(lineNumber, "a key token is missing its closing '>'.");

            var token = payload[(offset + 1)..end];
            if (token.Length == 0)
                throw InvalidInput(lineNumber, "a key token cannot be empty.");

            keys.Add(ParseKeyToken(token, count, lineNumber));
            offset = end + 1;
        }

        if (keys.Count == 0)
            throw InvalidInput(lineNumber, "the 'k ' line requires at least one key token.");

        return keys;
    }

    private static TerminalKey ParseKeyToken(string token, int? count, int lineNumber)
    {
        var modifiers = TerminalKeyModifiers.None;
        while (token.Length >= 2 && token[1] == '-')
        {
            modifiers |= token[0] switch
            {
                'C' or 'c' => TerminalKeyModifiers.Control,
                'A' or 'a' or 'M' or 'm' => TerminalKeyModifiers.Alt,
                'S' or 's' => TerminalKeyModifiers.Shift,
                _ => throw InvalidInput(lineNumber, $"unknown key modifier '{token[0]}'.")
            };
            token = token[2..];
        }

        if (token.Length == 0)
            throw InvalidInput(lineNumber, "a key token must name a key after its modifiers.");

        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            if ((modifiers & TerminalKeyModifiers.Shift) != 0)
                throw InvalidInput(lineNumber, $"'{token}' cannot carry a shift modifier; use the uppercase letter.");

            if ((modifiers & TerminalKeyModifiers.Control) != 0 &&
                (modifiers & TerminalKeyModifiers.Alt) != 0)
                throw InvalidInput(lineNumber, $"'{token}' cannot combine control and meta modifiers.");

            if ((modifiers & (TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt)) == 0)
                throw InvalidInput(lineNumber, $"'{token}' is not a known key name.");

            return new TerminalKey(count, new TerminalCharKey(token[0], modifiers));
        }

        if (token.Length == 1 && token[0] is '@' or '[' or '\\' or ']' or '^' or '_')
        {
            if ((modifiers & TerminalKeyModifiers.Control) == 0 ||
                (modifiers & (TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift)) != 0)
                throw InvalidInput(lineNumber, $"'{token}' requires exactly the control modifier.");

            return new TerminalKey(count, new TerminalCharKey(token[0], modifiers));
        }

        if (!SpecialKeyNames.TryGetValue(token, out var name))
            throw InvalidInput(lineNumber, $"'{token}' is not a known key name.");

        return new TerminalKey(count, new TerminalSpecialKey(name, modifiers));
    }

    private static AgentToolException InvalidInput(int lineNumber, string reason)
    {
        return new AgentToolException(
            "terminal_invalid_input",
            $"The shell input failed on line {lineNumber}: {reason}");
    }

    internal static Key ToXTermKey(TerminalKeyName name)
    {
        return XTermKeys[name];
    }

    internal static KeyModifiers ToXTermModifiers(TerminalKeyModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) result |= KeyModifiers.Control;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) result |= KeyModifiers.Alt;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) result |= KeyModifiers.Shift;
        return result;
    }
}

/// <summary>
///     Encodes parsed key tokens into the byte sequences the terminal expects. Function keys follow the
///     emulator's current application-cursor-key and keypad modes; control letters map to their C0 bytes;
///     meta keys are ESC-prefixed.
/// </summary>
internal sealed class TerminalKeyEncoder
{
    private readonly KeyboardInputGenerator? generator;

    internal TerminalKeyEncoder()
    {
        generator = null;
    }

    internal TerminalKeyEncoder(XTerm.Terminal terminal)
    {
        generator = new KeyboardInputGenerator(terminal);
    }

    internal byte[] Encode(IReadOnlyList<TerminalKey> keys)
    {
        var builder = new MemoryStream();
        foreach (var key in keys)
        {
            var bytes = EncodeOne(key.Spec);
            var repeat = key.Count ?? 1;
            for (var index = 0; index < repeat; index++) builder.Write(bytes);
        }

        return builder.ToArray();
    }

    private byte[] EncodeOne(TerminalKeySpec spec)
    {
        return spec switch
        {
            TerminalCharKey character => EncodeCharKey(character),
            TerminalSpecialKey special => EncodeSpecialKey(special),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, null)
        };
    }

    private static byte[] EncodeCharKey(TerminalCharKey key)
    {
        if ((key.Modifiers & TerminalKeyModifiers.Control) != 0)
            return [(byte)EncodeControl(key.Character)];

        if ((key.Modifiers & TerminalKeyModifiers.Alt) == 0)
            throw new ArgumentOutOfRangeException(nameof(key), key,
                "A character key requires a control or meta modifier.");
        var payload = Encoding.UTF8.GetBytes(key.Character.ToString());
        var bytes = new byte[payload.Length + 1];
        bytes[0] = 0x1b;
        payload.CopyTo(bytes, 1);
        return bytes;

    }

    private byte[] EncodeSpecialKey(TerminalSpecialKey key)
    {
        if (generator is null)
            throw new InvalidOperationException("A special key requires a terminal-backed encoder.");

        return Encoding.UTF8.GetBytes(generator.GenerateKeySequence(
            TerminalInputBatchParser.ToXTermKey(key.Name),
            TerminalInputBatchParser.ToXTermModifiers(key.Modifiers)));
    }

    private static int EncodeControl(char character)
    {
        return character switch
        {
            '@' => 0x00,
            '[' => 0x1b,
            '\\' => 0x1c,
            ']' => 0x1d,
            '^' => 0x1e,
            '_' => 0x1f,
            _ when char.IsAsciiLetter(character) => char.ToLowerInvariant(character) - 'a' + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(character), character, null)
        };
    }
}
