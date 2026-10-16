// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Text;
using System.Text.Json.Nodes;
using Kantor.Core.Remote;
using Xunit;

namespace Kantor.Tests;

public class RemoteProtocolTests
{
    // ---- Marco --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Frame_prefix_is_four_bytes_little_endian_and_round_trips()
    {
        var json = Encoding.UTF8.GetBytes("{\"id\":\"1\"}");
        var frame = FrameCodec.Encode(json);

        Assert.Equal(json.Length, BitConverter.ToInt32(frame, 0));
        Assert.Equal((byte)json.Length, frame[0]);
        Assert.Equal(0, frame[1]);

        using var stream = new MemoryStream(frame);
        var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(json, read);
    }

    [Fact]
    public async Task Frame_of_exactly_one_mebibyte_is_valid_and_one_more_byte_is_not()
    {
        var exact = new byte[Protocol.MaxFrameBytes];
        Array.Fill(exact, (byte)'x');
        var frame = FrameCodec.Encode(exact);
        using (var ok = new MemoryStream(frame))
        {
            Assert.Equal(Protocol.MaxFrameBytes, (await FrameCodec.ReadAsync(ok, CancellationToken.None))!.Length);
        }

        Assert.Throws<FrameException>(() => FrameCodec.Encode(new byte[Protocol.MaxFrameBytes + 1]));

        var tooBig = new byte[4];
        BitConverter.TryWriteBytes(tooBig, (uint)Protocol.MaxFrameBytes + 1);
        using var stream = new MemoryStream(tooBig);
        await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Zero_length_and_truncated_frames_are_protocol_errors_and_clean_end_is_null()
    {
        using var zero = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(zero, CancellationToken.None));

        var truncated = new byte[] { 10, 0, 0, 0, (byte)'{', (byte)'}' };
        using var cut = new MemoryStream(truncated);
        await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(cut, CancellationToken.None));

        using var empty = new MemoryStream([]);
        Assert.Null(await FrameCodec.ReadAsync(empty, CancellationToken.None));
    }

    // ---- Sobre y validación -------------------------------------------------------------------------------------

    private static IncomingMessage Parse(string json) =>
        IncomingMessage.Parse(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow, out _);

    [Fact]
    public void Envelope_is_parsed_with_origin_and_unknown_fields_are_ignored()
    {
        var message = Parse("""
            {"id":"r1","type":"kantor.queue.add","commandId":"abcdefgh","origin":{"clientId":"phone-1","name":"Pablo"},
             "extra":123,"payload":{"libraryEntryId":"e1","futuro":true}}
            """);

        Assert.Equal("r1", message.Id);
        Assert.Equal("kantor.queue.add", message.Type);
        Assert.Equal("abcdefgh", message.CommandId);
        Assert.Equal(new Origin("phone-1", "Pablo"), message.Origin);
        Assert.Equal("e1", message.Payload.String("libraryEntryId"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"type\":\"x\"}")]
    [InlineData("{\"id\":\"\",\"type\":\"x\"}")]
    [InlineData("{\"id\":\"1\"}")]
    [InlineData("not json")]
    public void Unreadable_envelopes_are_protocol_errors(string json)
    {
        var error = Assert.Throws<RemoteException>(() => Parse(json));
        Assert.Equal(ErrorCodes.ProtocolError, error.Code);
    }

    [Fact]
    public void Origin_with_oversized_fields_is_dropped_so_mutations_are_rejected_later()
    {
        var longName = new string('n', Protocol.MaxOriginNameChars + 1);
        var message = Parse($$$"""{"id":"1","type":"t","origin":{"clientId":"c","name":"{{{longName}}}"},"payload":{}}""");

        Assert.Null(message.Origin);
    }

    [Fact]
    public void Payload_reader_validates_types_ranges_and_choices()
    {
        var p = new PayloadReader(JsonNode.Parse("""{"s":"hola","n":7,"b":true,"c":"Title","a":[1,2,3],"big":99999999999}""")!.AsObject());

        Assert.Equal("hola", p.String("s", required: true, min: 1, max: 10));
        Assert.Equal(7, p.Int("n", 0, 10));
        Assert.True(p.Bool("b"));
        Assert.Equal("title", p.Choice("c", ["artist", "title"]));
        Assert.Null(p.String("missing"));

        var tooLong = Assert.Throws<RemoteException>(() => p.String("s", max: 2));
        Assert.Equal(ErrorCodes.InvalidPayload, tooLong.Code);
        Assert.Equal("s", tooLong.Details!["field"]!.GetValue<string>());
        Assert.Throws<RemoteException>(() => p.Int("n", 10, 20));
        Assert.Throws<RemoteException>(() => p.Int("big"));
        Assert.Throws<RemoteException>(() => p.String("n")); // tipo equivocado
        Assert.Throws<RemoteException>(() => p.Bool("s"));
        Assert.Throws<RemoteException>(() => p.Choice("c", ["a", "b"]));
        Assert.Throws<RemoteException>(() => p.String("zzz", required: true));
        Assert.Throws<RemoteException>(() => p.Array("a", maxCount: 2));
    }

    [Theory]
    [InlineData("abcdefgh", true)]
    [InlineData("A-b_c-1234", true)]
    [InlineData("short", false)]
    [InlineData("tiene espacio", false)]
    [InlineData("ñandú-12345", false)]
    public void Command_ids_must_be_8_to_64_url_safe_characters(string id, bool valid)
    {
        if (valid)
        {
            Assert.Equal(id, PayloadReader.CheckCommandId(id));
        }
        else
        {
            Assert.Equal(ErrorCodes.InvalidPayload, Assert.Throws<RemoteException>(() => PayloadReader.CheckCommandId(id)).Code);
        }

        Assert.Throws<RemoteException>(() => PayloadReader.CheckCommandId(new string('a', 65)));
        Assert.Throws<RemoteException>(() => PayloadReader.CheckCommandId(null));
    }

    // ---- Deduplicación -----------------------------------------------------------------------------------------

    private static string Fp(string type, string payload) =>
        CommandDeduplicator.Fingerprint(type, JsonNode.Parse(payload)!.AsObject());

    [Fact]
    public void Fingerprint_ignores_key_order_and_whitespace_but_not_values_or_type()
    {
        Assert.Equal(Fp("t", """{"a":1,"b":[1,2],"c":{"x":1,"y":2}}"""), Fp("t", """{ "c":{"y":2,"x":1}, "b":[1,2], "a":1 }"""));
        Assert.NotEqual(Fp("t", """{"a":1}"""), Fp("t", """{"a":2}"""));
        Assert.NotEqual(Fp("t", """{"a":1}"""), Fp("u", """{"a":1}"""));
    }

    [Fact]
    public async Task A_repeated_command_replays_the_original_response_and_a_reused_id_is_rejected()
    {
        var dedupe = new CommandDeduplicator();
        var fp = Fp("kantor.queue.add", """{"libraryEntryId":"e1"}""");

        Assert.IsType<DedupeDecision.Execute>(dedupe.Begin("cmd-00001", fp));
        var waiting = Assert.IsType<DedupeDecision.Replay>(dedupe.Begin("cmd-00001", fp)); // reintento mientras corre
        Assert.False(waiting.Response.IsCompleted);

        var original = new CachedResponse(true, 7, "{\"queueRev\":3}", null, null, null);
        dedupe.Complete("cmd-00001", original);

        Assert.Equal(original, await waiting.Response);
        var later = Assert.IsType<DedupeDecision.Replay>(dedupe.Begin("cmd-00001", fp));
        Assert.Equal(original, await later.Response);
        Assert.IsType<DedupeDecision.Reused>(dedupe.Begin("cmd-00001", Fp("kantor.queue.add", """{"libraryEntryId":"e2"}""")));
    }

    [Fact]
    public async Task A_response_that_must_not_be_remembered_frees_the_id_for_a_real_retry()
    {
        var dedupe = new CommandDeduplicator();
        var fp = Fp("t", "{}");
        dedupe.Begin("cmd-00002", fp);
        var waiter = Assert.IsType<DedupeDecision.Replay>(dedupe.Begin("cmd-00002", fp));

        dedupe.Complete("cmd-00002", new CachedResponse(false, 1, null, ErrorCodes.Timeout, "t", null), remember: false);

        Assert.Equal(ErrorCodes.Timeout, (await waiter.Response).ErrorCode);
        Assert.IsType<DedupeDecision.Execute>(dedupe.Begin("cmd-00002", fp)); // reintento: se ejecuta de verdad
    }

    [Fact]
    public void Entries_expire_after_fifteen_minutes_and_the_cache_is_bounded()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var dedupe = new CommandDeduplicator(() => now, capacity: 3);
        var fp = Fp("t", "{}");

        dedupe.Begin("cmd-00001", fp);
        dedupe.Complete("cmd-00001", new CachedResponse(true, 1, "{}", null, null, null));
        now = now.AddMinutes(14);
        Assert.IsType<DedupeDecision.Replay>(dedupe.Begin("cmd-00001", fp));
        now = now.AddMinutes(2);
        Assert.IsType<DedupeDecision.Execute>(dedupe.Begin("cmd-00001", fp)); // venció

        for (var i = 0; i < 10; i++)
        {
            dedupe.Begin($"bulk-{i:D5}", fp);
        }

        Assert.True(dedupe.Count <= 3);
    }

    // ---- Presencia ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_silent_connection_is_dead_only_after_timeout_plus_grace()
    {
        var liveness = new PipeLiveness().Start(TimeSpan.Zero);

        // el vigilante corre cada segundo
        for (var s = 1; s <= 20; s++)
        {
            Assert.Equal(LivenessVerdict.Alive, liveness.Check(TimeSpan.FromSeconds(s)));
        }

        Assert.Equal(LivenessVerdict.Dead, liveness.Check(TimeSpan.FromSeconds(21)));
    }

    [Fact]
    public void Any_inbound_frame_keeps_the_connection_alive_indefinitely()
    {
        var liveness = new PipeLiveness().Start(TimeSpan.Zero);
        for (var s = 1; s <= 600; s++)
        {
            if (s % 5 == 0)
            {
                liveness.Touch(TimeSpan.FromSeconds(s)); // el ping del Agent
            }

            Assert.Equal(LivenessVerdict.Alive, liveness.Check(TimeSpan.FromSeconds(s)));
        }
    }

    [Fact]
    public void A_stall_of_the_watchdog_itself_is_not_charged_to_the_client()
    {
        var liveness = new PipeLiveness().Start(TimeSpan.Zero);
        Assert.Equal(LivenessVerdict.Alive, liveness.Check(TimeSpan.FromSeconds(1)));

        // el equipo se suspendió 10 minutos: la primera revisión al volver no puede matar la conexión
        Assert.Equal(LivenessVerdict.Alive, liveness.Check(TimeSpan.FromSeconds(601)));
        Assert.Equal(1, liveness.StallResets);

        // y a partir de ahí vuelve a valer la regla normal
        for (var s = 602; s <= 621; s++)
        {
            Assert.Equal(LivenessVerdict.Alive, liveness.Check(TimeSpan.FromSeconds(s)));
        }

        Assert.Equal(LivenessVerdict.Dead, liveness.Check(TimeSpan.FromSeconds(622)));
    }

    // ---- Revisiones --------------------------------------------------------------------------------------------

    private static Dictionary<string, SectionValue> Sections(params (string Name, string Json)[] values) =>
        values.ToDictionary(v => v.Name, v => new SectionValue(JsonNode.Parse(v.Json), v.Json));

    [Fact]
    public void One_update_with_several_changed_sections_advances_the_revision_once()
    {
        var publisher = new StatePublisher();

        var first = publisher.Update(Sections(("a", "1"), ("b", "2")))!;
        Assert.Equal(1, first.Rev);
        Assert.Equal(2, first.Changed.Count);

        Assert.Null(publisher.Update(Sections(("a", "1"), ("b", "2")))); // sin cambios: sin revisión nueva
        Assert.Equal(1, publisher.Rev);

        var third = publisher.Update(Sections(("a", "9"), ("b", "8")))!;
        Assert.Equal(2, third.Rev);
        Assert.Equal(["a", "b"], third.Changed.Select(c => c.Key).Order());
    }

    [Fact]
    public void Only_changed_sections_are_published_and_snapshots_have_everything()
    {
        var publisher = new StatePublisher();
        publisher.Update(Sections(("a", "1"), ("b", "2")));

        var change = publisher.Update(Sections(("a", "1"), ("b", "3")))!;

        Assert.Equal(["b"], change.Changed.Select(c => c.Key));
        var (rev, snapshot) = publisher.Snapshot();
        Assert.Equal(2, rev);
        Assert.Equal(1, snapshot["a"]!.GetValue<int>());
        Assert.Equal(3, snapshot["b"]!.GetValue<int>());
    }

    [Fact]
    public void Volatile_fields_refresh_snapshots_without_publishing()
    {
        var publisher = new StatePublisher();
        publisher.Update(new Dictionary<string, SectionValue>
        {
            ["playback"] = new(JsonNode.Parse("""{"status":"playing","position":1.0}"""), "playing"),
        });

        var none = publisher.Update(new Dictionary<string, SectionValue>
        {
            ["playback"] = new(JsonNode.Parse("""{"status":"playing","position":9.5}"""), "playing"),
        });

        Assert.Null(none);
        Assert.Equal(9.5, publisher.Snapshot().Sections["playback"]!["position"]!.GetValue<double>());
        Assert.Equal(1, publisher.Rev);
    }
}
