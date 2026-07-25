using System.Text.Json;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B4: the ladder query surfaces standings after ranked play, and /healthz reports live load.</summary>
public class LadderQueryTests
{
    private static Hello Hello(string guest, string name) => new()
    {
        GuestId = guest,
        Secret = "secret-" + guest,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    [Fact]
    public async Task Get_ladder_shows_standings_after_a_ranked_match()
    {
        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());

        var aStarted = Tcs<MatchStarted>();
        var bRating = Tcs<RatingChange>();
        var ladder = Tcs<Ladder>();
        a.MessageReceived += m => { if (m is MatchStarted ms) aStarted.TrySetResult(ms); else if (m is Ladder l) ladder.TrySetResult(l); };
        b.MessageReceived += m => { if (m is RatingChange rc) bRating.TrySetResult(rc); };

        await a.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await b.ConnectAsync(server.Ws, Hello("gB", "Bob"));
        await a.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await b.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });

        var aMs = await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await a.SendAsync(new SubmitCommand { Command = new ConcedeCommand { Seat = aMs.Seat } }); // Alice loses
        await bRating.Task.WaitAsync(TimeSpan.FromSeconds(5)); // settled

        await a.SendAsync(new GetLadder());
        var l = await ladder.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, l.Entries.Count);
        Assert.Equal("Bob", l.Entries[0].Name);   // winner on top
        Assert.Equal(1, l.Entries[0].Rank);
        Assert.True(l.Entries[0].Rating > l.Entries[1].Rating);
        Assert.Equal(2, l.MyRank);                 // Alice is the querier, ranked 2nd
    }

    [Fact]
    public async Task Healthz_returns_json_status()
    {
        await using var server = await RunningServer.StartAsync();
        var healthUri = new Uri(server.Ws.ToString().Replace("ws://", "http://").Replace("/ws", "/healthz"));

        using var http = new HttpClient();
        var json = await http.GetStringAsync(healthUri).WaitAsync(TimeSpan.FromSeconds(5));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("queue").GetInt32());
        Assert.Equal(0, root.GetProperty("matches").GetInt32());
        Assert.True(root.TryGetProperty("connections", out _));
        Assert.True(root.TryGetProperty("rankedToday", out _));
    }

    /// <summary>docs/24 R1: /healthz also reports the three values the hello handshake gates on, so a release
    /// can diff the live server against the local build (scripts/preflight.ps1) instead of guessing.</summary>
    [Fact]
    public async Task Healthz_reports_the_handshake_identity()
    {
        await using var server = await RunningServer.StartAsync();
        var healthUri = new Uri(server.Ws.ToString().Replace("ws://", "http://").Replace("/ws", "/healthz"));

        using var http = new HttpClient();
        var json = await http.GetStringAsync(healthUri).WaitAsync(TimeSpan.FromSeconds(5));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ProtocolConstants.ProtocolVersion, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(RulesInfo.Version, root.GetProperty("rulesVersion").GetString());
        // Exactly the hash the handshake compares hello.DataHash against — not a recomputed lookalike.
        Assert.Equal(server.Service<GameContent>().DataHash, root.GetProperty("dataHash").GetString());
    }

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
