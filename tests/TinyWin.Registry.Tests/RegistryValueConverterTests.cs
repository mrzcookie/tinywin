using System.Text.Json;
using TinyWin.Catalog.Models;
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace TinyWin.Registry.Tests;

public class RegistryValueConverterTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData(RegistryValueKind.Dword, "1", Win32ValueKind.DWord)]
    [InlineData(RegistryValueKind.Qword, "1", Win32ValueKind.QWord)]
    [InlineData(RegistryValueKind.Sz, "\"x\"", Win32ValueKind.String)]
    [InlineData(RegistryValueKind.ExpandSz, "\"%TEMP%\"", Win32ValueKind.ExpandString)]
    [InlineData(RegistryValueKind.MultiSz, "[\"a\"]", Win32ValueKind.MultiString)]
    [InlineData(RegistryValueKind.Binary, "\"AAE=\"", Win32ValueKind.Binary)]
    public void Every_catalog_kind_maps_to_its_win32_kind(RegistryValueKind kind, string json, Win32ValueKind expected) =>
        Assert.Equal(expected, RegistryValueConverter.Convert(kind, Json(json)).Kind);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("-1", -1)]
    [InlineData("4294967295", -1)]        // 0xFFFFFFFF written unsigned is the same 32 bits as -1
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("\"0x1\"", 1)]
    [InlineData("\"0xFF\"", 255)]
    [InlineData("\"0Xff\"", 255)]
    [InlineData("\"42\"", 42)]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    public void Dword_accepts_the_shapes_a_catalog_author_actually_writes(string json, int expected) =>
        Assert.Equal(expected, RegistryValueConverter.Convert(RegistryValueKind.Dword, Json(json)).Data);

    [Fact]
    public void Dword_rejects_a_value_that_does_not_fit()
    {
        var ex = Assert.Throws<RegistryActionException>(
            () => RegistryValueConverter.Convert(RegistryValueKind.Dword, Json("4294967296")));
        Assert.Contains("dword", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("\"0x100000000\"", 4294967296L)]
    public void Qword_carries_the_full_64_bit_range(string json, long expected) =>
        Assert.Equal(expected, RegistryValueConverter.Convert(RegistryValueKind.Qword, Json(json)).Data);

    [Fact]
    public void Sz_takes_the_string_verbatim() =>
        Assert.Equal("Hello world", RegistryValueConverter.Convert(RegistryValueKind.Sz, Json("\"Hello world\"")).Data);

    [Fact]
    public void Multi_sz_takes_an_array() =>
        Assert.Equal(
            new[] { "a", "b" },
            (string[])RegistryValueConverter.Convert(RegistryValueKind.MultiSz, Json("[\"a\",\"b\"]")).Data);

    [Fact]
    public void Multi_sz_accepts_a_bare_string_as_a_single_entry() =>
        Assert.Equal(
            new[] { "only" },
            (string[])RegistryValueConverter.Convert(RegistryValueKind.MultiSz, Json("\"only\"")).Data);

    [Fact]
    public void Multi_sz_rejects_a_non_string_entry() =>
        Assert.Throws<RegistryActionException>(
            () => RegistryValueConverter.Convert(RegistryValueKind.MultiSz, Json("[\"a\",1]")));

    [Fact]
    public void Binary_decodes_base64() =>
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0xFF },
            (byte[])RegistryValueConverter.Convert(RegistryValueKind.Binary, Json("\"AAH/\"")).Data);

    [Fact]
    public void Binary_also_accepts_an_array_of_byte_values() =>
        Assert.Equal(
            new byte[] { 0, 1, 255 },
            (byte[])RegistryValueConverter.Convert(RegistryValueKind.Binary, Json("[0,1,255]")).Data);

    [Theory]
    [InlineData("\"not base64!\"")]
    [InlineData("[0,256]")]
    [InlineData("[0,-1]")]
    [InlineData("{}")]
    public void Binary_rejects_anything_it_cannot_decode_exactly(string json) =>
        Assert.Throws<RegistryActionException>(
            () => RegistryValueConverter.Convert(RegistryValueKind.Binary, Json(json)));

    [Theory]
    [InlineData(RegistryValueKind.Dword, "\"abc\"")]
    [InlineData(RegistryValueKind.Dword, "\"\"")]
    [InlineData(RegistryValueKind.Dword, "[1]")]
    [InlineData(RegistryValueKind.Sz, "1")]
    [InlineData(RegistryValueKind.Sz, "true")]
    [InlineData(RegistryValueKind.ExpandSz, "[]")]
    public void A_payload_that_does_not_match_its_kind_throws_rather_than_coercing(
        RegistryValueKind kind, string json) =>
        Assert.Throws<RegistryActionException>(() => RegistryValueConverter.Convert(kind, Json(json)));

    [Fact]
    public void Missing_data_throws() =>
        Assert.Throws<RegistryActionException>(() => RegistryValueConverter.Convert(RegistryValueKind.Dword, null));

    [Fact]
    public void Explicit_json_null_throws() =>
        Assert.Throws<RegistryActionException>(
            () => RegistryValueConverter.Convert(RegistryValueKind.Sz, Json("null")));

    [Fact]
    public void Error_messages_name_the_offending_value()
    {
        var ex = Assert.Throws<RegistryActionException>(
            () => RegistryValueConverter.Convert(RegistryValueKind.Dword, Json("\"abc\"")));
        Assert.Contains("abc", ex.Message, StringComparison.Ordinal);
    }
}
