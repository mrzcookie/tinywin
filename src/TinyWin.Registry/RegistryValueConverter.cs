using System.Globalization;
using System.Text.Json;
using TinyWin.Catalog.Models;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry;

/// <summary>
/// Turns a catalog <c>kind</c> + <c>data</c> pair into the CLR object and Win32 value type that
/// <c>RegSetValueEx</c> expects.
/// </summary>
/// <remarks>
/// This is the layer where a hand-authored catalog most easily goes wrong — a dword written as
/// <c>"0x1"</c>, a multi-sz written as a bare string, binary written as a byte array instead of
/// base64. Every accepted shape is deliberate and unit tested; everything else throws with the
/// offending JSON in the message, because a value silently coerced to the wrong type is a tweak
/// that appears to apply and does nothing.
/// </remarks>
internal static class RegistryValueConverter
{
    public static (object Data, Win32ValueKind Kind) Convert(RegistryValueKind kind, JsonElement? data)
    {
        if (data is not { } element || element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            throw new RegistryActionException($"Registry action of kind '{kind}' requires 'data'.");
        }

        return kind switch
        {
            RegistryValueKind.Dword => (ToDword(element), Win32ValueKind.DWord),
            RegistryValueKind.Qword => (ToQword(element), Win32ValueKind.QWord),
            RegistryValueKind.Sz => (ToSz(element, kind), Win32ValueKind.String),
            RegistryValueKind.ExpandSz => (ToSz(element, kind), Win32ValueKind.ExpandString),
            RegistryValueKind.MultiSz => (ToMultiString(element), Win32ValueKind.MultiString),
            RegistryValueKind.Binary => (ToBinary(element), Win32ValueKind.Binary),
            _ => throw new RegistryActionException($"Unsupported registry value kind '{kind}'."),
        };
    }

    /// <summary>
    /// A REG_DWORD is 32 bits with no sign of its own, so both <c>-1</c> and <c>4294967295</c> are
    /// legitimate ways to write the same value and both are accepted.
    /// </summary>
    private static int ToDword(JsonElement element)
    {
        var raw = ToInteger(element, RegistryValueKind.Dword);

        if (raw is < int.MinValue or > uint.MaxValue)
        {
            throw new RegistryActionException($"Registry value {raw} does not fit in a dword.");
        }

        return unchecked((int)raw);
    }

    private static long ToQword(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var unsigned) && unsigned > long.MaxValue)
        {
            return unchecked((long)unsigned);
        }

        return ToInteger(element, RegistryValueKind.Qword);
    }

    private static long ToInteger(JsonElement element, RegistryValueKind kind) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,

        // Toggle-shaped tweaks read better in JSON as true/false than as 1/0.
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,

        // Strings so authors can paste the hex form regedit shows them.
        JsonValueKind.String => ParseIntegerString(element.GetString(), kind),
        _ => throw new RegistryActionException(
            $"Registry value of kind '{kind}' must be a number, boolean, or numeric string, but was {Describe(element)}."),
    };

    private static long ParseIntegerString(string? text, RegistryValueKind kind)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new RegistryActionException($"Registry value of kind '{kind}' was an empty string.");
        }

        var negative = trimmed.StartsWith('-');
        var magnitude = negative ? trimmed[1..] : trimmed;

        if (magnitude.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(magnitude[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                || hex > long.MaxValue)
            {
                throw new RegistryActionException($"Registry value '{text}' is not a valid hexadecimal {kind}.");
            }

            return negative ? -(long)hex : (long)hex;
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new RegistryActionException($"Registry value '{text}' is not a valid {kind}.");
        }

        return value;
    }

    private static string ToSz(JsonElement element, RegistryValueKind kind) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new RegistryActionException(
                $"Registry value of kind '{kind}' must be a string, but was {Describe(element)}.");

    private static string[] ToMultiString(JsonElement element)
    {
        // A single string is accepted as a one-entry multi-sz; that is what the author meant, and
        // rejecting it would be pedantry rather than safety.
        if (element.ValueKind == JsonValueKind.String)
        {
            return new[] { element.GetString()! };
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new RegistryActionException(
                $"Registry value of kind 'MultiSz' must be a string or an array of strings, but was {Describe(element)}.");
        }

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new RegistryActionException(
                    $"Registry value of kind 'MultiSz' contains a non-string entry ({Describe(item)}).");
            }

            items.Add(item.GetString()!);
        }

        return items.ToArray();
    }

    private static byte[] ToBinary(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            try
            {
                return System.Convert.FromBase64String(text);
            }
            catch (FormatException ex)
            {
                throw new RegistryActionException($"Registry value of kind 'Binary' is not valid base64: '{text}'.", ex);
            }
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new RegistryActionException(
                $"Registry value of kind 'Binary' must be base64 or an array of byte values, but was {Describe(element)}.");
        }

        var bytes = new List<byte>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var value) || value is < 0 or > 255)
            {
                throw new RegistryActionException(
                    $"Registry value of kind 'Binary' contains an entry that is not a byte ({Describe(item)}).");
            }

            bytes.Add((byte)value);
        }

        return bytes.ToArray();
    }

    private static string Describe(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? $"the string '{element.GetString()}'" : $"a JSON {element.ValueKind}";
}
