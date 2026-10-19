using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickShot.Core;

public static class Protocol
{
    public const int Version = 3;
    public const int MaxMessageBytes = 64 * 1024;
    public const string HostName = "com.clipboardsender.edge";
    public const string ExtensionId = "kgjahiekhopambbcmenggajjghlmbhel";

    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException("Invalid message length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
            ?? throw new InvalidDataException("Expected a JSON object.");
    }

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > MaxMessageBytes) throw new InvalidDataException("Message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public sealed record TargetBinding(string Id, string Key, string Name, string Url);

