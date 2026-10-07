using System;

namespace Maieutics.Skills;

/// <summary>Parses the restricted <c>SKILL.md</c> frontmatter: one leading <c>---</c>-fenced
/// block of <c>key: value</c> scalar lines. Only <c>name</c> and <c>description</c> are read;
/// unknown keys are ignored so newer skill formats degrade visibly but do not fail. This is a
/// deliberate non-YAML subset — no nesting, quoting, or block scalars — so the kernel carries
/// no YAML dependency (ADR 0039).</summary>
internal static class SkillFrontmatter
{
    /// <summary>Reads the frontmatter block. A file with no leading fence parses as an empty
    /// block (the caller decides whether the required description is missing); a malformed
    /// fence reports <paramref name="error"/>. Line endings normalize to LF first so a
    /// CRLF-authored file (Windows checkouts, Windows authors) parses identically.</summary>
    internal static bool TryRead(string body, out string? name, out string? description, out string? error)
    {
        name = null;
        description = null;
        error = null;
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !IsFence(lines[0]))
            return true;

        var parsedName = (string?)null;
        var parsedDescription = (string?)null;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (IsFence(line))
            {
                name = parsedName;
                // Only the description is bounded here; the name's own charset and length
                // rules are a validity question the discovery layer answers.
                description = parsedDescription is null ||
                              parsedDescription.Length <= SkillDescriptor.MaximumDescriptionLength
                    ? parsedDescription
                    : parsedDescription[..SkillDescriptor.MaximumDescriptionLength];
                return true;
            }

            // Content before the first `key:` line (blank lines, prose) is ignored rather than
            // rejected: the fence is the contract, everything inside it that is not a scalar
            // line is skipped, and only a missing closing fence is an error.
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                if (!IsCleanScalar(value))
                {
                    error = "The 'name' value contains control characters.";
                    return false;
                }

                parsedName = value;
            }
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                if (!IsCleanScalar(value))
                {
                    error = "The 'description' value contains control characters.";
                    return false;
                }

                parsedDescription = value;
            }
        }

        error = "The frontmatter block is not closed.";
        return false;
    }

    /// <summary>A scalar value must be one clean line: interior control characters (CR, NEL,
    /// U+2028/2029) would let untrusted file content fake line breaks inside the system
    /// prompt's catalog section, breaking out of the one-line-per-entry framing.</summary>
    private static bool IsCleanScalar(string value)
    {
        foreach (var character in value)
            if (character < ' ' || character == '\u007f' || character is '\u2028' or '\u2029')
                return false;
        return true;
    }

    private static bool IsFence(string line)
    {
        return line.Length == 3 && line[0] == '-' && line[1] == '-' && line[2] == '-';
    }
}
