using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk.Execution;

internal delegate T Utf8BufferReader<T>(ReadOnlySpan<byte> bytes);

/// <summary>Owns a native UTF-8 response. No UTF-16 string or intermediate JSON DOM is allocated.</summary>
internal sealed class NativeJsonBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    private int length = -1;
    internal NativeJsonBuffer(IntPtr pointer) : base(true) => SetHandle(pointer);
    internal unsafe T Read<T>(Utf8BufferReader<T> action, int offset = 0, int count = -1)
    {
        bool held = false;
        try
        {
            DangerousAddRef(ref held);
            if (IsInvalid) throw new InvalidDataException("Missing native response.");
            if (length < 0) length = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)handle).Length;
            var bytes = new ReadOnlySpan<byte>((void*)handle, length);
            return action(count < 0 ? bytes[offset..] : bytes.Slice(offset, count));
        }
        finally { if (held) DangerousRelease(); }
    }
    protected override bool ReleaseHandle()
    {
        Native.StringFree(handle);
        return true;
    }
}

/// <summary>Frees all response buffers after the final owned InspectionResult.Data has been created.</summary>
internal sealed class NativeJsonBuffers : IDisposable
{
    private readonly List<NativeJsonBuffer> buffers = new();
    internal NativeJsonBuffer Take(int status, IntPtr pointer)
    {
        var buffer = Native.TakeJsonBuffer(status, pointer);
        try { buffers.Add(buffer); return buffer; }
        catch { buffer.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var buffer in buffers) buffer.Dispose();
        buffers.Clear();
    }
}

/// <summary>
/// A validated JSON value referencing a slice of a native response. Values can be shared by node,
/// debug and public aliases without parsing their large image/result payloads into separate DOMs.
/// </summary>
[JsonConverter(typeof(RawKernelJsonConverter))]
internal sealed class RawKernelJson
{
    private readonly NativeJsonBuffer? owner;
    private readonly int offset, count;
    private JsonElement? materialized;
    private IReadOnlyDictionary<string, RawKernelJson>? properties;
    private readonly bool constructedObject;
    private RawKernelJson(NativeJsonBuffer owner, int offset, int count)
    { this.owner = owner; this.offset = offset; this.count = count; }
    private RawKernelJson(IReadOnlyDictionary<string, RawKernelJson> properties)
    { this.properties = properties; constructedObject = true; }

    internal static RawKernelJson ReadValue(NativeJsonBuffer owner) => owner.Read(bytes =>
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read()) throw new JsonException("Missing native JSON value.");
        int start = checked((int)reader.TokenStartIndex);
        reader.Skip();
        var value = new RawKernelJson(owner, start, checked((int)reader.BytesConsumed) - start);
        if (reader.Read()) throw new JsonException("Unexpected trailing native JSON data.");
        return value;
    });

    internal static RawKernelStep ReadStep(NativeJsonBuffer owner) => owner.Read(bytes =>
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Invalid kernel response.");
        Dictionary<string, RawKernelJson>? ports = null;
        var envelope = ReadProperties(ref reader, owner, 0, true, ref ports);
        if (reader.Read()) throw new JsonException("Unexpected trailing native JSON data.");
        if (ports is null || !envelope.TryGetValue("ports", out var rawPorts)) throw new JsonException("Missing kernel ports.");
        rawPorts.properties = ports;
        return new RawKernelStep(rawPorts, ports, envelope);
    });

    private static Dictionary<string, RawKernelJson> ReadProperties(ref Utf8JsonReader reader,
        NativeJsonBuffer owner, int baseOffset, bool capturePorts, ref Dictionary<string, RawKernelJson>? ports)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a JSON object.");
        var result = new Dictionary<string, RawKernelJson>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a JSON property.");
            string name = reader.GetString()!;
            if (!reader.Read()) throw new JsonException("Missing JSON property value.");
            int start = checked((int)reader.TokenStartIndex);
            if (capturePorts && name == "ports")
            {
                Dictionary<string, RawKernelJson>? ignored = null;
                ports = ReadProperties(ref reader, owner, baseOffset, false, ref ignored);
            }
            else reader.Skip();
            result.Add(name, new(owner, checked(baseOffset + start), checked((int)reader.BytesConsumed) - start));
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Incomplete JSON object.");
        return result;
    }

    internal RawKernelJson WithoutProperty(string name)
    {
        if (properties is null)
            properties = owner!.Read(bytes =>
            {
                var reader = new Utf8JsonReader(bytes);
                if (!reader.Read()) throw new JsonException("Missing JSON object.");
                Dictionary<string, RawKernelJson>? ignored = null;
                return ReadProperties(ref reader, owner!, offset, false, ref ignored);
            }, offset, count);
        if (!properties.ContainsKey(name)) return this;
        return new RawKernelJson(properties.Where(p => p.Key != name).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    internal JsonElement ToElement()
    {
        if (materialized is JsonElement value) return value;
        materialized = constructedObject ? JsonSerializer.SerializeToElement(this) : owner!.Read(bytes =>
        {
            var reader = new Utf8JsonReader(bytes);
            return JsonElement.ParseValue(ref reader);
        }, offset, count);
        return materialized.Value;
    }
    internal bool Boolean() => owner!.Read(bytes =>
    {
        var reader = new Utf8JsonReader(bytes); reader.Read(); return reader.GetBoolean();
    }, offset, count);
    internal double Number() => owner!.Read(bytes =>
    {
        var reader = new Utf8JsonReader(bytes); reader.Read(); return reader.GetDouble();
    }, offset, count);

    internal void Write(Utf8JsonWriter writer)
    {
        if (constructedObject)
        {
            writer.WriteStartObject();
            foreach (var property in properties!)
            {
                writer.WritePropertyName(property.Key);
                property.Value.Write(writer);
            }
            writer.WriteEndObject();
        }
        else owner!.Read(bytes =>
        {
            // Every slice was completely validated by Utf8JsonReader before it was constructed.
            // The SafeHandle ref stays held until the writer has copied the UTF-8 bytes.
            writer.WriteRawValue(bytes, skipInputValidation: true);
            return 0;
        }, offset, count);
    }
}

internal sealed record RawKernelStep(RawKernelJson Ports, IReadOnlyDictionary<string, RawKernelJson> PortValues,
    IReadOnlyDictionary<string, RawKernelJson> Envelope);

internal sealed class RawKernelJsonConverter : JsonConverter<RawKernelJson>
{
    public RawKernelJsonConverter() { }
    public override RawKernelJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw kernel JSON is created only from validated native responses.");
    public override void Write(Utf8JsonWriter writer, RawKernelJson value, JsonSerializerOptions options) => value.Write(writer);
}
