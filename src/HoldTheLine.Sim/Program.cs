using System.Text.Json;
using HoldTheLine.Net;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

// Self-play smoke / balance simulator (plan §6; docs/07 X2.2; docs/12 A2). AI policies (per seat):
//   random — random-legal-move bots; exercises the engine, systematically favours defence — NOT a balance signal.
//   greedy — one-ply heuristic bots (GreedyAi); crude but symmetric — the balance口径.
//   easy   — greedy with ε-random misplays (== AiProfile.Easy).
//   fast   — shallow lookahead (== AiProfile.Normal SearchAi params).
//   search — full lookahead (== AiProfile.Hard, today's vs-AI困难).
//
// Usage: dotnet run --project src/HoldTheLine.Sim -- [games] [seed] [mode] [deckA] [deckB] [flags]
//   mode (positional, legacy) = greedy | random | roundrobin           (default greedy)
//   --ai <policy>            both seats same policy (overrides mode)
//   --ai0/--ai1 <policy>     override one seat (e.g. --ai0 search --ai1 greedy)
//   --decks a,b,c,...        roundrobin over N decks (>=2), pairwise matrix
//   --parallel N             concurrent games (default 1; results are identical regardless of N)
//   --mulligan               run the 起手重抽 phase
//   --dump                   print per-game winner/turns (for verifying parallel == serial)
//   --print-hash             print this build's handshake identity as one line of JSON and exit — no games.
//                            (docs/24 R2: scripts/preflight.ps1 diffs it against the live server's /healthz.
//                            Lives here because Sim already loads game/data and references DataHash.)
// deckA / deckB / each --decks item resolves, in order, to: a builtin deck id, a HTL1- deck code,
// or a json file path {"leader":"...","cards":[...]}. Codes/files are validated; bad input exits non-zero.

var argList = args.ToList();

bool TakeBool(string flag)
{
    int i = argList.IndexOf(flag);
    if (i < 0) return false;
    argList.RemoveAt(i);
    return true;
}

string? TakeValue(string flag)
{
    int i = argList.IndexOf(flag);
    if (i < 0) return null;
    if (i + 1 >= argList.Count) throw Die($"{flag} needs a value");
    var v = argList[i + 1];
    argList.RemoveRange(i, 2);
    return v;
}

bool mulligan = TakeBool("--mulligan");
bool dump = TakeBool("--dump");
bool printHash = TakeBool("--print-hash");
string? aiBoth = TakeValue("--ai");
string? ai0Opt = TakeValue("--ai0");
string? ai1Opt = TakeValue("--ai1");
string? decksOpt = TakeValue("--decks");
string? parallelOpt = TakeValue("--parallel");

int games = argList.Count > 0 ? int.Parse(argList[0]) : 400;
ulong seed = argList.Count > 1 ? ulong.Parse(argList[1]) : 20260717UL;
string mode = argList.Count > 2 ? argList[2] : "greedy";
string deckAId = argList.Count > 3 ? argList[3] : "iron_wall";
string deckBId = argList.Count > 4 ? argList[4] : "wildpack_hunt";
int parallel = parallelOpt != null ? Math.Max(1, int.Parse(parallelOpt)) : 1;
const int TurnLimit = 300;

SimAi basePolicy = mode switch
{
    "random" => SimAi.Random,
    "roundrobin" => SimAi.Greedy,
    _ => SimAi.Greedy,
};
SimAi seatBase = aiBoth != null ? ParsePolicy(aiBoth) : basePolicy;
SimAi ai0 = ai0Opt != null ? ParsePolicy(ai0Opt) : seatBase;
SimAi ai1 = ai1Opt != null ? ParsePolicy(ai1Opt) : seatBase;
bool roundRobin = mode == "roundrobin" || decksOpt != null;

string root = FindDataRoot();
var db = CardDatabase.LoadFromDirectory(Path.Combine(root, "cards"));
var leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(root, "leaders"));
var decks = DeckLibrary.LoadFromDirectory(Path.Combine(root, "decks"));
string dataHash = DataHash.Compute(db, leaders, decks);

// docs/24 R2: the handshake identity of the working tree, in the SAME shape /healthz reports it, so
// preflight can compare field-for-field. Printed as a single line starting with '{' — callers should take
// the first such line and ignore any MSBuild/restore chatter above it.
if (printHash)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        protocolVersion = ProtocolConstants.ProtocolVersion,
        rulesVersion = RulesInfo.Version,
        dataHash,
        cards = db.All.Count,
        leaders = leaders.All.Count,
        decks = decks.Count,
    }));
    return;
}

var deckFileJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

if (roundRobin)
{
    RunRoundRobin();
    return;
}

// ---- single pairing (deckA vs deckB, seats alternate each game) ----
var pool = new[] { ResolveDeck(deckAId), ResolveDeck(deckBId) };
var seeds = DeriveSeeds(seed, games);
var jobs = new Job[games];
for (int g = 0; g < games; g++)
    jobs[g] = (g % 2 == 0) ? new Job(0, 1, seeds[g]) : new Job(1, 0, seeds[g]);

var results = RunJobs(pool, jobs);

int[] firstSeatWins = new int[2];
int aWins = 0, bWins = 0, draws = 0, timeouts = 0, tideKills = 0;
long totalTurns = 0, totalCommands = 0;
for (int g = 0; g < games; g++)
{
    var r = results[g];
    bool aIsSeat0 = g % 2 == 0;
    totalTurns += r.Turns;
    totalCommands += r.Commands;
    if (r.TideKill) tideKills++;
    if (r.Winner == -2) { timeouts++; continue; }
    if (r.Winner < 0) { draws++; continue; }
    firstSeatWins[r.Winner]++;
    bool aWon = (r.Winner == 0) == aIsSeat0;
    if (aWon) aWins++; else bWins++;
}
int decided = aWins + bWins;

Console.WriteLine($"ai0={Name(ai0)} ai1={Name(ai1)} deckA={pool[0].Label} deckB={pool[1].Label} games={games} seed={seed} parallel={parallel}");
Console.WriteLine($"[{pool[0].Label} vs {pool[1].Label}] {pool[0].Label}={aWins} ({Pct(aWins, decided)})  {pool[1].Label}={bWins} ({Pct(bWins, decided)})");
Console.WriteLine($"first-seat wins: seat0={firstSeatWins[0]} ({Pct(firstSeatWins[0], decided)})  seat1={firstSeatWins[1]} ({Pct(firstSeatWins[1], decided)})");
if (ai0 != ai1)
    Console.WriteLine($"by policy (seat-bound): seat0={Name(ai0)} {Pct(firstSeatWins[0], decided)}   seat1={Name(ai1)} {Pct(firstSeatWins[1], decided)}");
Console.WriteLine($"draws={draws}  timeouts(>{TurnLimit} turns)={timeouts}  tide-kills={tideKills} ({Pct(tideKills, games)} of games)");
Console.WriteLine($"avg turns/game={(double)totalTurns / games:F1}  avg commands/game={(double)totalCommands / games:F1}");
if (dump)
    for (int g = 0; g < games; g++)
        Console.WriteLine($"  game {g}: winner={results[g].Winner} turns={results[g].Turns} commands={results[g].Commands}");
return;

// ---- round robin (docs/07 X2.2 / docs/12 A2) ----
void RunRoundRobin()
{
    SimDeck[] rrPool = decksOpt != null
        ? decksOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ResolveDeck).ToArray()
        : new[] { "iron_wall", "wildpack_hunt", "duskweaver_vesper", "undervault_sunline" }.Select(ResolveDeck).ToArray();
    if (rrPool.Length < 2) throw Die("roundrobin needs at least 2 decks");
    int n = rrPool.Length;

    var pairs = new List<(int I, int J)>();
    for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
            pairs.Add((i, j));

    var rrSeeds = DeriveSeeds(seed, pairs.Count * games);
    var rrJobs = new Job[pairs.Count * games];
    var meta = new (int I, int J, bool ISeat0)[pairs.Count * games];
    int k = 0;
    foreach (var (i, j) in pairs)
        for (int g = 0; g < games; g++, k++)
        {
            bool iSeat0 = g % 2 == 0;
            rrJobs[k] = new Job(iSeat0 ? i : j, iSeat0 ? j : i, rrSeeds[k]);
            meta[k] = (i, j, iSeat0);
        }

    var rr = RunJobs(rrPool, rrJobs);

    var wins = new int[n, n];
    var played = new int[n, n];
    int[] firstSeat = new int[2];
    long turnsSum = 0;
    int decidedTotal = 0, tideTotal = 0, timeoutTotal = 0;
    for (k = 0; k < rr.Length; k++)
    {
        var (i, j, iSeat0) = meta[k];
        var r = rr[k];
        turnsSum += r.Turns;
        if (r.TideKill) tideTotal++;
        if (r.Winner == -2) { timeoutTotal++; continue; }
        if (r.Winner < 0) continue; // draw
        firstSeat[r.Winner]++;
        decidedTotal++;
        int winnerDeck = (r.Winner == 0) == iSeat0 ? i : j;
        int loserDeck = winnerDeck == i ? j : i;
        wins[winnerDeck, loserDeck]++;
        played[winnerDeck, loserDeck]++;
        played[loserDeck, winnerDeck]++;
    }

    Console.WriteLine($"mode=roundrobin ai0={Name(ai0)} ai1={Name(ai1)} decks={n} games/pairing={games} seed={seed} parallel={parallel}");
    Console.WriteLine();
    Console.WriteLine("row win% vs column:");
    Console.Write("               ");
    foreach (var d in rrPool) Console.Write($"{Short(d.Label),10}");
    Console.WriteLine();
    for (int i = 0; i < n; i++)
    {
        Console.Write($"{Short(rrPool[i].Label),-15}");
        for (int j = 0; j < n; j++)
        {
            if (i == j) { Console.Write($"{"—",10}"); continue; }
            Console.Write($"{Pct(wins[i, j], played[i, j]),10}");
        }
        Console.WriteLine();
    }
    Console.WriteLine();
    Console.WriteLine($"first-seat: seat0={Pct(firstSeat[0], decidedTotal)}  seat1={Pct(firstSeat[1], decidedTotal)}");
    Console.WriteLine($"avg turns/game={(double)turnsSum / rr.Length:F1}  tide-kills={tideTotal} ({Pct(tideTotal, rr.Length)})  timeouts={timeoutTotal}");
    if (dump)
        for (k = 0; k < rr.Length; k++)
            Console.WriteLine($"  job {k}: pair=({meta[k].I},{meta[k].J}) iSeat0={meta[k].ISeat0} winner={rr[k].Winner} turns={rr[k].Turns}");
}

// ---- runner ----
MatchResult[] RunJobs(SimDeck[] deckPool, Job[] batch)
{
    var res = new MatchResult[batch.Length];
    var opts = new ParallelOptions { MaxDegreeOfParallelism = parallel };
    Parallel.For(0, batch.Length, opts, k =>
    {
        var jb = batch[k];
        res[k] = PlayMatch(deckPool[jb.A], deckPool[jb.B], jb.Seed);
    });
    return res;
}

// Pure per-match function: builds its own Resolver / SearchAi / RNG so parallel runs never share mutable
// state. Seat 0 uses ai0, seat 1 uses ai1 (the batch already picked which deck sits where).
MatchResult PlayMatch(SimDeck d0, SimDeck d1, ulong matchSeed)
{
    var resolver = new Resolver(db, leaders);
    var rng = new DeterministicRng(matchSeed ^ 0x9E3779B97F4A7C15UL);
    var searchers = new SearchAi?[2];

    SearchAi SearchFor(int seat, SimAi ai) => searchers[seat] ??= ai == SimAi.Fast
        ? new SearchAi(db, leaders, AiProfile.Normal.SearchTopK, AiProfile.Normal.SearchRollout)
        : new SearchAi(db, leaders);

    Command Pick(GameState st, IReadOnlyList<Command> legal)
    {
        int seat = st.ActiveSeat;
        SimAi ai = seat == 0 ? ai0 : ai1;
        switch (ai)
        {
            case SimAi.Random:
                var nonEnd = legal.Where(c => c is not EndTurnCommand).ToList();
                return nonEnd.Count > 0 && rng.NextInt(100) < 85
                    ? nonEnd[rng.NextInt(nonEnd.Count)]
                    : new EndTurnCommand { Seat = seat };
            case SimAi.Easy:
                if (rng.NextInt(1000) < (int)(AiProfile.Easy.Epsilon * 1000))
                {
                    var epool = legal.Where(c => c is not ConcedeCommand and not EndTurnCommand).ToList();
                    if (epool.Count > 0) return epool[rng.NextInt(epool.Count)];
                }
                return GreedyAi.Pick(st, db, leaders, legal);
            case SimAi.Fast:
            case SimAi.Search:
                return SearchFor(seat, ai).Pick(st, legal);
            default: // Greedy
                return GreedyAi.Pick(st, db, leaders, legal);
        }
    }

    var (state, _) = GameFactory.CreateGame(new MatchConfig
    {
        Seed = matchSeed,
        FirstSeat = 0,
        Deck0 = d0.Cards, Deck1 = d1.Cards,
        Leader0 = d0.Leader, Leader1 = d1.Leader,
        MulliganEnabled = mulligan,
    }, db, leaders);

    long commands = 0;
    bool tideKill = false;

    // 起手重抽 (docs/11): resolve both seats before the first turn (no-op when disabled — Mulligan is null).
    while (state.Mulligan is not null)
    {
        int seat = state.Mulligan.Done[0] ? 1 : 0;
        SimAi ai = seat == 0 ? ai0 : ai1;
        var mull = ai == SimAi.Random
            ? new MulliganCommand { Seat = seat, ReplacedEntityIds = [] }
            : MulliganAi.Pick(state, db, seat);
        var mres = resolver.Execute(state, mull);
        if (!mres.Success)
            throw new InvalidOperationException($"Mulligan rejected: {mres.Error!.Code} {mres.Error.Message}");
        state = mres.State!;
        commands++;
    }

    while (state.Result is null && state.TurnNumber <= TurnLimit)
    {
        var legal = CommandEnumerator.LegalCommands(state, db, leaders);
        var pick = Pick(state, legal);
        var res = resolver.Execute(state, pick);
        if (!res.Success)
            throw new InvalidOperationException($"Enumerated command rejected: {res.Error!.Code} {res.Error.Message}");
        state = res.State!;
        commands++;
        if (state.Result != null)
            tideKill = res.Events.Any(e => e is PressureTideEvent) && res.Events.Any(e => e is GameEndedEvent);
    }

    int winner = state.Result is null ? -2 : state.Result.WinnerSeat; // -2 timeout, -1 draw, else seat
    return new MatchResult(winner, Math.Min(state.TurnNumber, TurnLimit), commands, tideKill);
}

// ---- deck resolution: builtin id | HTL1- code | json file ----
SimDeck ResolveDeck(string arg)
{
    var builtin = decks.FirstOrDefault(d => d.Id == arg);
    if (builtin != null)
        return new SimDeck(builtin.Name, builtin.Leader, builtin.Expand());

    if (arg.StartsWith(DeckCode.Prefix, StringComparison.Ordinal))
    {
        var (err, payload) = DeckCode.Decode(arg);
        if (err != DeckCodeError.None)
            throw Die($"deck code decode failed: {err}");
        if (DeckCode.Check(payload!, RulesInfo.Version, dataHash) != DeckCodeError.None)
            throw Die($"deck code env mismatch: code rules {payload!.Rules} hash {payload.Hash}, this build rules {RulesInfo.Version} hash {dataHash[..8]}");
        var invalid = DeckValidator.Validate(payload!.Cards, db);
        if (invalid != null)
            throw Die($"deck code is not a legal deck: {invalid.Message}");
        return new SimDeck($"code:{payload!.Hash}", payload.Leader, payload.Cards);
    }

    if (File.Exists(arg))
    {
        DeckFile? doc;
        try { doc = JsonSerializer.Deserialize<DeckFile>(File.ReadAllText(arg), deckFileJson); }
        catch (Exception ex) { throw Die($"deck file '{arg}' parse failed: {ex.Message}"); }
        if (doc is null || doc.Cards is null || doc.Leader is null)
            throw Die($"deck file '{arg}' is missing leader/cards");
        var invalid = DeckValidator.Validate(doc.Cards, db);
        if (invalid != null)
            throw Die($"deck file '{arg}' is not a legal deck: {invalid.Message}");
        return new SimDeck(Path.GetFileNameWithoutExtension(arg), doc.Leader, doc.Cards);
    }

    throw Die($"deck '{arg}' is not a builtin id, a HTL1- code, or an existing json file path");
}

SimAi ParsePolicy(string s) => s.Trim().ToLowerInvariant() switch
{
    "random" => SimAi.Random,
    "greedy" => SimAi.Greedy,
    "easy" => SimAi.Easy,
    "fast" => SimAi.Fast,
    "search" => SimAi.Search,
    _ => throw Die($"unknown ai policy '{s}' (random|greedy|easy|fast|search)"),
};

ulong[] DeriveSeeds(ulong master, int count)
{
    var r = new DeterministicRng(master);
    var s = new ulong[count];
    for (int i = 0; i < count; i++)
        s[i] = r.NextUInt64();
    return s;
}

string Pct(int nn, int dd) => dd == 0 ? "0.0%" : $"{100.0 * nn / dd:F1}%";
string Name(SimAi ai) => ai.ToString().ToLowerInvariant();

// Environment.Exit terminates before the returned exception is thrown; `throw Die(...)` satisfies flow analysis.
Exception Die(string msg)
{
    Console.Error.WriteLine("error: " + msg);
    Environment.Exit(1);
    return new InvalidOperationException(msg);
}

static string Short(string s) => s.Length <= 8 ? s : s[..8];

static string FindDataRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "game", "data");
        if (Directory.Exists(Path.Combine(candidate, "cards")))
            return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("game/data not found above " + AppContext.BaseDirectory);
}

enum SimAi { Random, Greedy, Easy, Fast, Search }
readonly record struct Job(int A, int B, ulong Seed);
readonly record struct MatchResult(int Winner, int Turns, long Commands, bool TideKill);
sealed record SimDeck(string Label, string Leader, IReadOnlyList<string> Cards);
sealed record DeckFile(string Leader, List<string> Cards);
