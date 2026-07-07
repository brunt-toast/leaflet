using System.Buffers.Binary;
using System.Text.Json;
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

    public static readonly ValueConverter<string, byte[]> SignatureEnvelopeConverter = new(
        json => EncodeCompositeEnvelope(json),
        bytes => DecodeCompositeEnvelope(bytes));

    public static readonly ValueConverter<DateTime, long> UtcDateTimeTicksConverter = new(
        value => value.ToUniversalTime().Ticks,
        ticks => new DateTime(ticks, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, long?> NullableUtcDateTimeTicksConverter = new(
        value => value.HasValue ? value.Value.ToUniversalTime().Ticks : null,
        ticks => ticks.HasValue ? new DateTime(ticks.Value, DateTimeKind.Utc) : null);

    private static byte[] EncodeCompositeEnvelope(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        string first = document.RootElement.GetProperty("mldsa").GetString()
            ?? throw new InvalidOperationException("Envelope field 'mldsa' was null.");
        string second = document.RootElement.GetProperty("slhdsa").GetString()
            ?? throw new InvalidOperationException("Envelope field 'slhdsa' was null.");

        byte[] firstBytes = Convert.FromBase64String(first);
        byte[] secondBytes = Convert.FromBase64String(second);
        byte[] payload = new byte[(sizeof(ushort) * 2) + firstBytes.Length + secondBytes.Length];

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, sizeof(ushort)), checked((ushort)firstBytes.Length));
        firstBytes.CopyTo(payload, sizeof(ushort));

        int secondLengthOffset = sizeof(ushort) + firstBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(secondLengthOffset, sizeof(ushort)), checked((ushort)secondBytes.Length));
        secondBytes.CopyTo(payload, secondLengthOffset + sizeof(ushort));

        return payload;
    }

    private static string DecodeCompositeEnvelope(byte[] payload)
    {
        ReadOnlySpan<byte> span = payload;

        ushort firstLength = BinaryPrimitives.ReadUInt16LittleEndian(span[..sizeof(ushort)]);
        string first = Convert.ToBase64String(span.Slice(sizeof(ushort), firstLength));

        int secondLengthOffset = sizeof(ushort) + firstLength;
        ushort secondLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(secondLengthOffset, sizeof(ushort)));
        string second = Convert.ToBase64String(span.Slice(secondLengthOffset + sizeof(ushort), secondLength));

        return JsonSerializer.Serialize(new CompositeEnvelope(first, second));
    }

    private sealed record CompositeEnvelope
    {
        public CompositeEnvelope(string mldsa, string slhdsa)
        {
            this.mldsa = mldsa;
            this.slhdsa = slhdsa;
        }

        public string mldsa { get; init; }

        public string slhdsa { get; init; }
    }
}
