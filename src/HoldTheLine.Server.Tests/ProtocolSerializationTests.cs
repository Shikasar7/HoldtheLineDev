using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// The wire contract (plan §8-1): every message shape survives a JSON round-trip byte-stably, a
/// nested polymorphic Command/GameEvent keeps its own <c>$type</c>, unknown message tags decode to
/// null (log-and-skip, don't crash), and unknown *fields* on a known message are ignored (forward
/// compatibility with a newer peer).
/// </summary>
public class ProtocolSerializationTests
{
    private static void RoundTripsClient(ClientMessage m)
    {
        var json = ProtocolJson.Encode(m);
        var decoded = ProtocolJson.TryDecodeClient(json);
        Assert.NotNull(decoded);
        Assert.Equal(json, ProtocolJson.Encode(decoded!)); // stable re-encoding == structural equality
    }

    private static void RoundTripsServer(ServerMessage m)
    {
        var json = ProtocolJson.Encode(m);
        var decoded = ProtocolJson.TryDecodeServer(json);
        Assert.NotNull(decoded);
        Assert.Equal(json, ProtocolJson.Encode(decoded!));
    }

    [Fact]
    public void Client_messages_round_trip()
    {
        RoundTripsClient(new Hello { GuestId = "g1", Name = "Alice", ProtocolVersion = 1, RulesVersion = "0.1.0" });
        RoundTripsClient(new Hello { GuestId = "g1", Name = "Alice", ProtocolVersion = 1, RulesVersion = "0.1.0", ResumeToken = "abc123" });
        RoundTripsClient(new CreateRoom { DeckId = "iron_wall", Seq = 2 });
        RoundTripsClient(new JoinRoom { Code = "X7K2QF", DeckId = "wildpack_hunt", Seq = 3 });
        RoundTripsClient(new LeaveRoom { Seq = 4 });
        RoundTripsClient(new Resync { SinceEventIndex = 12, Seq = 5 });
        RoundTripsClient(new Ping { Seq = 6 });
    }

    [Fact]
    public void SubmitCommand_preserves_nested_polymorphic_command()
    {
        RoundTripsClient(new SubmitCommand { Command = new PlayCardCommand { Seat = 0, CardEntityId = 5 } });
        RoundTripsClient(new SubmitCommand { Command = new AttackCommand { Seat = 1, AttackerEntityId = 9, TargetLeader = true } });
        RoundTripsClient(new SubmitCommand { Command = new EndTurnCommand { Seat = 0 } });

        // The inner discriminator must be the Command family's $type, distinct from the outer t.
        var json = ProtocolJson.Encode(new SubmitCommand { Command = new EndTurnCommand { Seat = 0 } });
        Assert.Contains("\"t\":\"submit_command\"", json);
        Assert.Contains("\"$type\":\"end_turn\"", json);
    }

    [Fact]
    public void Mulligan_protocol_shapes_round_trip()
    {
        // C→S: MulliganCommand rides SubmitCommand — no new message type, inner $type "mulligan" (protocol v4).
        RoundTripsClient(new SubmitCommand { Command = new MulliganCommand { Seat = 0, ReplacedEntityIds = [3, 5] } });
        Assert.Contains("\"$type\":\"mulligan\"",
            ProtocolJson.Encode(new SubmitCommand { Command = new MulliganCommand { Seat = 0, ReplacedEntityIds = [3] } }));

        // S→C: the mulligan countdown rides match_started / resync_ok.
        RoundTripsServer(new MatchStarted { Seat = 0, ResumeToken = "tok", View = MinimalView(), OpponentName = "Bob", MulliganSecondsLeft = 45 });
        RoundTripsServer(new ResyncOk { View = MinimalView(), EventsSince = [], EventIndex = 0, MulliganSecondsLeft = 30 });

        // S→C: the new event shapes in a batch.
        RoundTripsServer(new EventsMsg
        {
            Batch = [
                new MulliganResolvedEvent { Seat = 0, ReplacedEntityIds = [3, 5], ReplacedCount = 2 },
                new MulliganCompletedEvent(),
            ],
            View = MinimalView(),
            EventIndex = 2,
        });
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        RoundTripsServer(new HelloOk { ServerTimeUnixMs = 1_700_000_000_000, Seq = 1 });
        RoundTripsServer(new RoomCreated { Code = "X7K2QF", Seq = 2 });
        RoundTripsServer(new CommandResultMsg { AckSeq = 7, Accepted = false, ErrorCode = "not_your_turn", ErrorMessage = "wait" });
        RoundTripsServer(new EventsMsg
        {
            Batch = [
                new TurnStartedEvent { Seat = 0, TurnNumber = 1, Mana = 1, ManaMax = 1 },
                new CardDrawnEvent { Seat = 0, CardEntityId = 3, CardId = "iron_recruit" },
                // Both clients need the public target cell to reproduce cell-targeted leader-skill FX.
                new LeaderSkillUsedEvent
                {
                    Seat = 1, LeaderId = "leader_dw_vela", TargetCell = new Cell(2, 1),
                },
            ],
            View = MinimalView(),
            EventIndex = 3,
        });
        RoundTripsServer(new OpponentStatus { Connected = false, GraceSeconds = 120 });
        RoundTripsServer(new TurnTimer { Seat = 1, SecondsLeft = 30 });
        RoundTripsServer(new MatchEnded { WinnerSeat = 0, Reason = "concede" });
        RoundTripsServer(new ErrorMsg { Code = "room_not_found", Message = "No room 'ZZZ'." });
        RoundTripsServer(new Pong { Seq = 6 });
    }

    [Fact]
    public void Profile_deck_summary_round_trips_with_full_card_list()
    {
        // Protocol v3: DeckSummary carries leader + card_ids so a saved deck is editable client-side.
        var profile = new Profile
        {
            Name = "Alice",
            Rating = 1200,
            Wins = 3,
            Losses = 1,
            CollectionMode = "unlock_all",
            Decks =
            [
                new DeckSummary
                {
                    Id = "deck-abc123", Name = "铁壁改", Faction = "iron_vow", Leader = "warden_karr",
                    CardIds = ["iv_shield_bearer", "iv_shield_bearer", "neutral_scout"],
                },
            ],
        };
        var json = ProtocolJson.Encode(profile);
        Assert.Contains("\"leader\":", json);
        Assert.Contains("\"card_ids\":", json);
        RoundTripsServer(profile);
    }

    private static PlayerView MinimalView() => new()
    {
        ViewerSeat = 0,
        TurnNumber = 1,
        ActiveSeat = 0,
        Self = new SelfView
        {
            LeaderId = "iron_vow", LeaderHp = 25, Mana = 1, ManaMax = 1, DeckCount = 25, Fatigue = 0,
            Hand = [new CardInHandView { EntityId = 3, CardId = "iron_recruit" }],
        },
        Opponent = new OpponentView
        {
            LeaderId = "wild_hunt", LeaderHp = 25, Mana = 0, ManaMax = 0, DeckCount = 26, HandCount = 7, Fatigue = 0,
        },
        Units = [],
    };

    [Fact]
    public void Unknown_tag_decodes_to_null_not_throw()
    {
        Assert.Null(ProtocolJson.TryDecodeServer("{\"t\":\"totally_unknown\",\"foo\":1}"));
        Assert.Null(ProtocolJson.TryDecodeClient("{\"t\":\"nope\"}"));
        Assert.Null(ProtocolJson.TryDecodeServer("not even json"));
        Assert.Null(ProtocolJson.TryDecodeClient("{}")); // no discriminator
    }

    [Fact]
    public void Unknown_field_on_known_message_is_ignored()
    {
        var decoded = ProtocolJson.TryDecodeClient("{\"t\":\"ping\",\"seq\":4,\"unknown_future_field\":true}");
        var ping = Assert.IsType<Ping>(decoded);
        Assert.Equal(4, ping.Seq);
    }
}
