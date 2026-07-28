using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Api.Context;

internal static class StorageValueConverters
{
    public static readonly ValueConverter<string, byte[]> RoomHashConverter = new(
        roomHash => Convert.FromHexString(roomHash),
        bytes => Convert.ToHexString(bytes).ToLowerInvariant());

    public static readonly ValueConverter<string, byte[]> Base64Converter = new(
        value => Convert.FromBase64String(value),
        bytes => Convert.ToBase64String(bytes));

    public static readonly ValueConverter<string, byte[]> Utf8StringConverter = new(
        value => System.Text.Encoding.UTF8.GetBytes(value),
        bytes => System.Text.Encoding.UTF8.GetString(bytes));

    public static readonly ValueConverter<string, byte[]> SignatureConverter = new(
        value => Convert.FromBase64String(value),
        bytes => Convert.ToBase64String(bytes));

    public static readonly ValueConverter<DateTime, long> UtcDateTimeTicksConverter = new(
        value => value.ToUniversalTime().Ticks,
        ticks => new DateTime(ticks, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, long?> NullableUtcDateTimeTicksConverter = new(
        value => value.HasValue ? value.Value.ToUniversalTime().Ticks : null,
        ticks => ticks.HasValue ? new DateTime(ticks.Value, DateTimeKind.Utc) : null);
}
