using System.Collections.Concurrent;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Server.Rooms;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// The N1 moat (plan §8-2): two networked bots play a full match over a live server, then we assert
/// the three properties the whole M2 architecture rests on —
///   (1) hidden information stays hidden (each seat's event stream never carries the opponent's card ids),
///   (2) both clients and the server agree on the outcome, and
///   (3) determinism survives the network: replaying MatchConfig + the server's command log through a
///       fresh LocalGameHost reproduces the exact result.
/// </summary>
public class NetworkedMatchTests
{
    private static Hello NewHello(string name) => new()
    {
        GuestId = "",
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    [Fact]
    public async Task Two_bots_play_a_full_match_redacted_and_replay_deterministic()
    {
        await using var server = await RunningServer.StartAsync();
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        // Capture every event each seat is dispatched, to check redaction after the game.
        var eventsA = new ConcurrentQueue<GameEvent>();
        var eventsB = new ConcurrentQueue<GameEvent>();
        hostA.Subscribe(0, eventsA.Enqueue);
        hostB.Subscribe(0, eventsB.Enqueue);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));

        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, seatA);
        Assert.Equal(1, seatB);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var driverA = new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(seed: 101));
        var driverB = new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(seed: 202));
        var winners = await Task.WhenAll(driverA.RunAsync(cts.Token), driverB.RunAsync(cts.Token));

        // (2) Both clients agree, and the server agrees.
        Assert.Equal(winners[0], winners[1]);
        var room = server.Service<RoomManager>().FindRoom(code);
        Assert.NotNull(room);
        var session = room!.Session!;
        var serverResult = session.Host.GetView(0).Result;
        Assert.NotNull(serverResult);
        Assert.Equal(serverResult!.WinnerSeat, winners[0]);

        // (1) Redaction: seat A never saw seat B's drawn card ids, and vice versa.
        AssertNoOpponentCardIds(eventsA, ownerSeat: seatA);
        AssertNoOpponentCardIds(eventsB, ownerSeat: seatB);

        // (3) Determinism across the wire: replay config + command log → identical winner.
        var content = server.Service<GameContent>();
        var replay = new LocalGameHost(content.Cards, content.Leaders, session.Host.Config, loopbackSerialization: false);
        foreach (var command in session.Host.CommandLog)
        {
            var r = await replay.SubmitCommandAsync(command.Seat, command);
            Assert.True(r.Accepted, $"replay rejected a logged command: {r.Error?.Code}");
        }
        Assert.Equal(serverResult.WinnerSeat, replay.GetView(0).Result?.WinnerSeat);
    }

    private static void AssertNoOpponentCardIds(IEnumerable<GameEvent> events, int ownerSeat)
    {
        foreach (var drawn in events.OfType<CardDrawnEvent>())
            if (drawn.Seat != ownerSeat)
                Assert.True(drawn.CardId is null,
                    $"seat {ownerSeat} was leaked opponent card id '{drawn.CardId}' via a redacted draw event");
    }

    [Fact]
    public async Task Dropped_client_reconnects_via_resume_token_and_finishes_the_match()
    {
        await using var server = await RunningServer.StartAsync();

        // Reconnect-capable clients: a fresh transport is minted per (re)connect.
        var helloA = NewHello("alice");
        var helloB = NewHello("bob");
        await using var clientA = new GameServerClient(() => new WebSocketTransport());
        await using var clientB = new GameServerClient(() => new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, helloA);
        await clientB.ConnectAsync(server.Ws, helloB);
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        hostA.EnableReconnect(helloA);
        hostB.EnableReconnect(helloB);

        // Signal when A has come back online after the forced drop.
        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool dropped = false;
        hostA.ConnectionStateChanged += s =>
        {
            if (s == ConnectionState.Reconnecting) dropped = true;
            if (s == ConnectionState.Connected && dropped) reconnected.TrySetResult(true);
        };
        // The server-declared winner, from B's match_ended (survives room cleanup).
        var bEnded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.MessageReceived += m => { if (m is MatchEnded me) bEnded.TrySetResult(me.WinnerSeat); };

        int seatA = hostA.Seat;
        // This bound is a hang-breaker, not an assertion about speed: the test plays a WHOLE match over real
        // websockets and has to survive a mid-game drop + reconnect, so its wall clock depends on how long two
        // random-legal bots take to finish — which changed when the 铁壁/狂猎 precons were rewritten. At 90 s it
        // tripped three times inside the release/deploy gate (docs/24 §7) while passing standalone every time,
        // i.e. it was failing releases without ever catching a defect. Kept finite so a genuine deadlock still
        // fails instead of hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var driverA = new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(seed: 7));
        var driverB = new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(seed: 8));
        var play = Task.WhenAll(driverA.RunAsync(cts.Token), driverB.RunAsync(cts.Token));

        // Drop A mid-game (event-driven so it's a real in-progress reconnect regardless of load), then
        // require it to transparently reconnect and keep playing.
        while (hostA.EventIndex < 2 && hostA.GetView(seatA).Result is null)
            await Task.Delay(15);
        await clientA.SimulateDropAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var winners = await play;
        Assert.Equal(winners[0], winners[1]);              // both clients agree
        Assert.Equal(winners[0], await bEnded.Task.WaitAsync(TimeSpan.FromSeconds(5))); // server agrees
    }

    [Fact]
    public async Task Grace_window_expires_then_the_remaining_player_wins_by_abandon()
    {
        await using var server = await RunningServer.StartAsync(disconnectGraceSeconds: 1);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };
        // B watches for the abandon end + disconnect notice.
        var bEnded = new TaskCompletionSource<MatchEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bSawDrop = new TaskCompletionSource<OpponentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.MessageReceived += m =>
        {
            if (m is MatchEnded me) bEnded.TrySetResult(me);
            if (m is OpponentStatus { Connected: false } os) bSawDrop.TrySetResult(os);
        };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // A leaves for good (no reconnect); B should be told, then win when the grace window closes.
        await clientA.DisposeAsync();

        var drop = await bSawDrop.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(drop.GraceSeconds);

        var ended = await bEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("abandon", ended.Reason);
        Assert.Equal(seatB, ended.WinnerSeat); // the player who stayed wins
    }

    [Fact]
    public async Task Idle_players_time_out_and_the_first_seat_forfeits_after_two_expiries()
    {
        await using var server = await RunningServer.StartAsync(turnSeconds: 1);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerSeen = new TaskCompletionSource<TurnTimer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<MatchEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m =>
        {
            if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code);
            if (m is TurnTimer tt) timerSeen.TrySetResult(tt);
            if (m is MatchEnded me) ended.TrySetResult(me);
        };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The server announces a turn clock…
        var tt = await timerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tt.SecondsLeft > 0);

        // …and with nobody acting, the seat that opened (times out twice in a row) forfeits.
        int firstSeat = hostA.GetView(seatA).ActiveSeat;
        var end = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("timeout", end.Reason);
        Assert.Equal(1 - firstSeat, end.WinnerSeat);
    }

    [Fact]
    public async Task Command_log_on_disk_replays_to_the_same_result()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "htl-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            int serverWinner;
            await using (var server = await RunningServer.StartAsync(commandLogDir: logDir))
            {
                await using var clientA = new GameServerClient(new WebSocketTransport());
                await using var clientB = new GameServerClient(new WebSocketTransport());
                var hostA = new RemoteGameHost(clientA);
                var hostB = new RemoteGameHost(clientB);

                var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

                await clientA.ConnectAsync(server.Ws, NewHello("alice"));
                await clientB.ConnectAsync(server.Ws, NewHello("bob"));
                await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
                var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });
                await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var winners = await Task.WhenAll(
                    new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(21)).RunAsync(cts.Token),
                    new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(22)).RunAsync(cts.Token));
                serverWinner = winners[0];
            }

            // Replay config + logged commands through a fresh host → identical winner.
            var file = Directory.EnumerateFiles(logDir, "match-*.jsonl").Single();
            var lines = File.ReadAllLines(file);
            var config = RulesJson.Deserialize<MatchConfig>(lines[0]);
            var content = GameContent.Load();
            var replay = new LocalGameHost(content.Cards, content.Leaders, config, loopbackSerialization: false);
            foreach (var line in lines.Skip(1))
            {
                var cmd = RulesJson.Deserialize<Command>(line);
                var r = await replay.SubmitCommandAsync(cmd.Seat, cmd);
                Assert.True(r.Accepted, $"logged command rejected on replay: {r.Error?.Code}");
            }
            Assert.Equal(serverWinner, replay.GetView(0).Result?.WinnerSeat);
        }
        finally
        {
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rejected_when_submitting_a_command_out_of_turn()
    {
        await using var server = await RunningServer.StartAsync();
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The non-active seat tries to end the turn — the server must reject, not apply.
        var idleSeat = hostA.GetView(seatA).ActiveSeat == seatA ? (host: hostB, seat: seatB) : (host: hostA, seat: seatA);
        var result = await idleSeat.host.SubmitCommandAsync(idleSeat.seat, new EndTurnCommand { Seat = idleSeat.seat });

        Assert.False(result.Accepted);
        Assert.Equal(RuleErrorCode.NotYourTurn, result.Error!.Code);
    }
}
