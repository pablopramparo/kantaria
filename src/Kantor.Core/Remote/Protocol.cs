// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kantor.Core.Remote;

/// <summary>Constantes del contrato <c>Kantor.ControlPlane.v1</c> (ver <c>docs/AVYN_CONTRATO.md</c>).</summary>
public static class Protocol
{
    public const string PipeName = "Kantor.ControlPlane.v1";
    public const int ProtocolVersion = 1;
    public const int ContractRevision = 3;

    /// <summary>Largo máximo del JSON de un marco, sin contar el prefijo de 4 bytes: 1 MiB exacto.</summary>
    public const int MaxFrameBytes = 1_048_576;

    public const int MaxPageSize = 50;
    public const int MaxQueueListSize = 100;
    public const int MaxQueryChars = 100;
    public const int MaxPlayerNameChars = 24;
    public const int MaxSessionNameChars = 48;
    public const int MaxOriginNameChars = 48;
    public const int MaxOriginIdChars = 64;
    public const int MinCommandIdChars = 8;
    public const int MaxCommandIdChars = 64;
    public const int MaxIdChars = 64;

    /// <summary>Tamaño máximo (JSON) de una página de búsqueda, aunque falten ítems para llegar a <c>limit</c>.</summary>
    public const int SearchPageBudgetBytes = 256 * 1024;

    public const int CoverMaxBytes = 96 * 1024;

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LivenessGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AutoAdvanceDelay = TimeSpan.FromSeconds(10);
}

/// <summary>Códigos de error del contrato (sección 5).</summary>
public static class ErrorCodes
{
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string NotFound = "NOT_FOUND";
    public const string InvalidState = "INVALID_STATE";
    public const string Busy = "BUSY";
    public const string Conflict = "CONFLICT";
    public const string CommandIdReused = "COMMAND_ID_REUSED";
    public const string ItemRunning = "ITEM_RUNNING";
    public const string QueueFull = "QUEUE_FULL";
    public const string SessionOpen = "SESSION_OPEN";
    public const string SessionRequired = "SESSION_REQUIRED";
    public const string PlayerArchived = "PLAYER_ARCHIVED";
    public const string PlayerNotInSession = "PLAYER_NOT_IN_SESSION";
    public const string PlayerNameTaken = "PLAYER_NAME_TAKEN";
    public const string SlotUnavailable = "SLOT_UNAVAILABLE";
    public const string NoMicrophones = "NO_MICROPHONES";
    public const string EntryUnavailable = "ENTRY_UNAVAILABLE";
    public const string NeedsLocalAction = "NEEDS_LOCAL_ACTION";
    public const string UnknownCommand = "UNKNOWN_COMMAND";
    public const string CapabilityUnavailable = "CAPABILITY_UNAVAILABLE";
    public const string ProtocolError = "PROTOCOL_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>Un comando que Kantaria rechaza con un error estructurado. Nunca cambió nada (salvo <c>INTERNAL_ERROR</c>).</summary>
public sealed class RemoteException : Exception
{
    public RemoteException(string code, string message, JsonObject? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonObject? Details { get; }

    public static RemoteException Invalid(string field, string message) =>
        new(ErrorCodes.InvalidPayload, message, new JsonObject { ["field"] = field });

    public static RemoteException NotFound(string field, bool gone) =>
        new(ErrorCodes.NotFound, gone ? $"{field} no longer exists." : $"{field} is unknown.",
            new JsonObject { ["field"] = field, ["reason"] = gone ? "gone" : "unknown" });

    public static RemoteException State(string message, string? screen = null, string? reason = null)
    {
        var details = new JsonObject();
        if (screen is not null)
        {
            details["screen"] = screen;
        }

        if (reason is not null)
        {
            details["reason"] = reason;
        }

        return new RemoteException(ErrorCodes.InvalidState, message, details);
    }
}

/// <summary>El marco recibido no respeta el contrato: no hay forma de resincronizar, se cierra el pipe.</summary>
public sealed class FrameException(string message) : Exception(message);

/// <summary>Marco de la conexión: 4 bytes little-endian con el largo del JSON UTF-8, y el JSON.</summary>
public static class FrameCodec
{
    /// <summary>Lee un marco. <c>null</c> si el otro lado cerró limpio entre dos marcos.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var prefix = new byte[4];
        var got = await ReadExactAsync(stream, prefix, cancellation).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < 4)
        {
            throw new FrameException("El marco terminó dentro de su prefijo.");
        }

        var length = BitConverter.ToUInt32(prefix, 0);
        if (length == 0 || length > Protocol.MaxFrameBytes)
        {
            throw new FrameException($"Largo de marco inválido: {length}.");
        }

        var body = new byte[length];
        if (await ReadExactAsync(stream, body, cancellation).ConfigureAwait(false) < body.Length)
        {
            throw new FrameException("El marco terminó antes de tiempo.");
        }

        return body;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Prefijo + JSON. Falla si el JSON supera el máximo: quien emite debe paginar antes.</summary>
    public static byte[] Encode(byte[] json)
    {
        if (json.Length is 0 or > Protocol.MaxFrameBytes)
        {
            throw new FrameException($"El mensaje mide {json.Length} bytes; el máximo es {Protocol.MaxFrameBytes}.");
        }

        var frame = new byte[4 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), (uint)json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }
}

/// <summary>JSON del contrato: camelCase, con los <c>null</c> explícitos.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, Options);

    public static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(pair.Key)).Append(':');
                    Write(pair.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }
}

/// <summary>Lee y valida los campos de un <c>payload</c>; todo lo que no cumple es <c>INVALID_PAYLOAD</c>.</summary>
public sealed partial class PayloadReader(JsonObject payload)
{
    private static readonly Regex CommandIdPattern = CommandIdRegex();

    public JsonObject Raw => payload;

    public bool Has(string name) => payload.TryGetPropertyValue(name, out var node) && node is not null;

    public string? String(string name, bool required = false, int min = 0, int max = int.MaxValue)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return required ? throw RemoteException.Invalid(name, $"'{name}' is required.") : null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw RemoteException.Invalid(name, $"'{name}' must be a string.");
        }

        if (text.Length < min || text.Length > max)
        {
            throw RemoteException.Invalid(name, $"'{name}' must have between {min} and {max} characters.");
        }

        return text;
    }

    public int? Int(string name, int min = int.MinValue, int max = int.MaxValue, bool required = false)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return required ? throw RemoteException.Invalid(name, $"'{name}' is required.") : null;
        }

        if (node is not JsonValue value || !value.TryGetValue<long>(out var number) || number < min || number > max)
        {
            throw RemoteException.Invalid(name, $"'{name}' must be an integer between {min} and {max}.");
        }

        return (int)number;
    }

    public long? Long(string name, bool required = false)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return required ? throw RemoteException.Invalid(name, $"'{name}' is required.") : null;
        }

        if (node is not JsonValue value || !value.TryGetValue<long>(out var number))
        {
            throw RemoteException.Invalid(name, $"'{name}' must be an integer.");
        }

        return number;
    }

    public bool? Bool(string name, bool required = false)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return required ? throw RemoteException.Invalid(name, $"'{name}' is required.") : null;
        }

        if (node is not JsonValue value || !value.TryGetValue<bool>(out var flag))
        {
            throw RemoteException.Invalid(name, $"'{name}' must be a boolean.");
        }

        return flag;
    }

    /// <summary>Un valor de una lista cerrada (sin distinguir mayúsculas).</summary>
    public string? Choice(string name, IReadOnlyList<string> allowed, bool required = false)
    {
        var text = String(name, required);
        if (text is null)
        {
            return null;
        }

        return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
               ?? throw RemoteException.Invalid(name, $"'{name}' must be one of: {string.Join(", ", allowed)}.");
    }

    public JsonArray? Array(string name, int maxCount, bool required = false)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return required ? throw RemoteException.Invalid(name, $"'{name}' is required.") : null;
        }

        if (node is not JsonArray array || array.Count > maxCount)
        {
            throw RemoteException.Invalid(name, $"'{name}' must be an array of at most {maxCount} items.");
        }

        return array;
    }

    public static string CheckCommandId(string? commandId)
    {
        if (commandId is null || commandId.Length < Protocol.MinCommandIdChars
            || commandId.Length > Protocol.MaxCommandIdChars || !CommandIdPattern.IsMatch(commandId))
        {
            throw RemoteException.Invalid("commandId",
                $"'commandId' must have {Protocol.MinCommandIdChars}-{Protocol.MaxCommandIdChars} characters from [A-Za-z0-9_-].");
        }

        return commandId;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex CommandIdRegex();

    public static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Quién originó un comando; lo construye el Agent a partir de la identidad autenticada del dispositivo.</summary>
public sealed record Origin(string ClientId, string Name);

/// <summary>Un mensaje entrante ya separado en sus campos del sobre.</summary>
public sealed record IncomingMessage(
    string Id, string Type, string? CommandId, Origin? Origin, PayloadReader Payload, DateTimeOffset ReceivedAt)
{
    /// <summary>Interpreta un marco. Lanza <see cref="RemoteException"/> con <c>PROTOCOL_ERROR</c> si es ilegible.</summary>
    public static IncomingMessage Parse(byte[] frame, DateTimeOffset now, out string? idIfKnown)
    {
        idIfKnown = null;
        JsonObject root;
        try
        {
            root = JsonNode.Parse(frame) as JsonObject
                   ?? throw new RemoteException(ErrorCodes.ProtocolError, "The message must be a JSON object.");
        }
        catch (JsonException e)
        {
            throw new RemoteException(ErrorCodes.ProtocolError, "Invalid JSON: " + e.Message);
        }

        var id = (root["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
        if (id is null || id.Length is 0 or > Protocol.MaxIdChars)
        {
            throw new RemoteException(ErrorCodes.ProtocolError, "'id' must be a string of 1 to 64 characters.");
        }

        idIfKnown = id;
        var type = (root["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
        if (string.IsNullOrEmpty(type))
        {
            throw new RemoteException(ErrorCodes.ProtocolError, "'type' is required.");
        }

        var commandId = (root["commandId"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null;
        Origin? origin = null;
        if (root["origin"] is JsonObject o)
        {
            var clientId = (o["clientId"] as JsonValue)?.TryGetValue<string>(out var ci) == true ? ci : null;
            var name = (o["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
            if (clientId is { Length: > 0 and <= Protocol.MaxOriginIdChars }
                && name is { Length: > 0 and <= Protocol.MaxOriginNameChars })
            {
                origin = new Origin(clientId, name);
            }
        }

        var payload = root["payload"] as JsonObject ?? [];
        return new IncomingMessage(id, type, commandId, origin, new PayloadReader(payload), now);
    }
}
