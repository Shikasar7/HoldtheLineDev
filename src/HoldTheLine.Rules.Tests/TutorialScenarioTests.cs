using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>
/// 新手教学引导关 (docs/23): replays the whole scripted choreography against the REAL game data (game/data) so
/// the intricate mechanics — coin economy, 持盾/反击, 坚守, 围猎 kill, 越线者死, positions — are proven legal and
/// produce the intended board, deterministically, without the client/UI. This mirrors game/scripts/tutorial/
/// TutorialData.cs; if that choreography changes, update this in lockstep.
/// </summary>
public class TutorialScenarioTests
{
    private static string Dir(string s) => Path.Combine(RepoPaths.Root, "game", "data", s);
    private static readonly CardDatabase Cards = CardDatabase.LoadFromDirectory(Dir("cards"));
    private static readonly LeaderDatabase Leaders = LeaderDatabase.LoadFromDirectory(Dir("leaders"));

    // card ids
    private const string Squire = "iv_oath_squire", Sergeant = "iv_line_sergeant", Overline = "iv_overline_execution";
    private const string Sentinel = "iv_stone_sentinel", Archer = "iv_bastion_archer", ForcedMarch = "nl_forced_march";
    private const string Pup = "wp_pup", Flanker = "wp_flanker", Leaper = "wp_rift_leaper", Coin = "neutral_coin";
    private const string Huntress = "wp_moon_huntress";

    private static readonly IReadOnlyList<string> Deck0 = new List<string>
    {
        "iv_shieldbearer", "iv_garrison_guard", "iv_oathguard", "iv_gate_ward", "iv_bulwark", "iv_shield_wall",
        ForcedMarch, Archer, Sentinel, Overline, Sergeant, Squire,
    };
    private static readonly IReadOnlyList<string> Deck1 = new List<string>
    {
        "wp_raider", "wp_howler", "wp_ambusher", "wp_night_prowler",
        Huntress, "wp_fang_rider", "wp_stalker", "wp_bone_gnawer",
        Leaper, Flanker, Pup, Pup,
    };

    private static Cell C(int col, int row) => new(col, row);

    [Fact]
    public async Task Full_tutorial_choreography_is_legal_and_reaches_the_scripted_board()
    {
        var config = new MatchConfig
        {
            Seed = 1, FirstSeat = 0,
            Deck0 = Deck0, Leader0 = "leader_iv_valen",
            Deck1 = Deck1, Leader1 = "leader_wp_saen",
            LeaderHp = 5, OpeningHandFirst = 0, OpeningHandSecond = 2,
            CoinCardId = Coin, Shuffle = false, MulliganEnabled = false,
            PressureTideStartRound = 7,
        };
        IGameHost host = new LocalGameHost(Cards, Leaders, config);

        // helpers over live state
        UnitView? At(Cell c) => host.GetView(0).Units.FirstOrDefault(u => u.Cell == c);
        int Uid(Cell c) => At(c) is { } u ? u.EntityId : throw new Xunit.Sdk.XunitException($"no unit at {c}");
        int Hand(int seat, string id) => host.GetView(seat).Self.Hand.First(h => h.CardId == id).EntityId;
        async Task Ok(Command cmd)
        {
            var r = await host.SubmitCommandAsync(cmd.Seat, cmd);
            Assert.True(r.Accepted, $"command rejected: {cmd.GetType().Name} → {r.Error?.Code} {r.Error?.Message}");
        }

        // ===== T1 · player (1 mana): deploy the squire, end =====
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, Squire), TargetCell = C(2, 0) });
        Assert.Equal(Squire, At(C(2, 0))!.CardId);
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T2 · opponent (1 + coin): coin, two pups, end =====
        // Resolve BOTH pup instances up front (as the client's beat builder does): a shared "deploy a pup"
        // helper would return the same instance twice — the regression this asserts against.
        var pups = host.GetView(1).Self.Hand.Where(h => h.CardId == Pup).Select(h => h.EntityId).ToList();
        Assert.Equal(2, pups.Count);
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = Hand(1, Coin) });           // +1 mana → 2
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = pups[0], TargetCell = C(1, 3) });
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = pups[1], TargetCell = C(2, 3) });
        Assert.Equal(2, host.GetView(0).Units.Count(u => u.CardId == Pup));
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T3 · player (2 mana): advance, 授盾, end =====
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(2, 0)), To = C(2, 1) });
        await Ok(new UseLeaderSkillCommand { Seat = 0, TargetUnitId = Uid(C(2, 1)) });
        Assert.True(At(C(2, 1))!.ShieldActive); // squire is shielded
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T4 · opponent (2 mana): both pups rush; pupB strikes the squire; deploy flanker; end =====
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(1, 3)), To = C(1, 2) });
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(1, 2)), To = C(1, 1) });
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(2, 3)), To = C(2, 2) });
        await Ok(new AttackCommand { Seat = 1, AttackerEntityId = Uid(C(2, 2)), TargetUnitId = Uid(C(2, 1)) });
        // 持盾 absorbed the hit (squire full), and the squire retaliated onto pupB.
        Assert.Equal(2, At(C(2, 1))!.CurrentHp);
        Assert.False(At(C(2, 1))!.ShieldActive);
        Assert.Equal(1, At(C(2, 2))!.CurrentHp); // pupB chipped to 1 by the retaliation
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = Hand(1, Flanker), TargetCell = C(3, 3) });
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T5 · player (3 mana): squire kills pupB in place (坚守 cancels the retaliation), then sergeant =====
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(2, 1)), TargetUnitId = Uid(C(2, 2)) });
        Assert.Null(At(C(2, 2)));               // pupB dead
        Assert.Equal(2, At(C(2, 1))!.CurrentHp); // 坚守: no retaliation damage taken
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, Sergeant), TargetCell = C(2, 0) });
        Assert.Equal(2, At(C(2, 1))!.Atk);      // sergeant 战吼 buffed the squire +1 atk
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T6 · opponent (3 mana): 狩猎号角 on flanker, cross the line, 围猎 kills sergeant, pup hits the hero =====
        await Ok(new UseLeaderSkillCommand { Seat = 1, TargetUnitId = Uid(C(3, 3)) }); // +1 move & 偷袭 on flanker
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(1, 1)), To = C(1, 0) }); // pupA to the backline
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(3, 3)), To = C(3, 2) });
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(3, 2)), To = C(3, 1) });
        await Ok(new MoveUnitCommand { Seat = 1, UnitEntityId = Uid(C(3, 1)), To = C(3, 0) });
        await Ok(new AttackCommand { Seat = 1, AttackerEntityId = Uid(C(3, 0)), TargetUnitId = Uid(C(2, 0)) });
        Assert.Null(At(C(2, 0)));                       // sergeant killed by 2 + 围猎2 = 4
        Assert.Equal(2, At(C(3, 0))!.CurrentHp);        // flanker took no retaliation (偷袭)
        await Ok(new AttackCommand { Seat = 1, AttackerEntityId = Uid(C(1, 0)), TargetLeader = true });
        Assert.Equal(4, host.GetView(0).Self.LeaderHp); // hero 5 → 4
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T7 · player (4 mana): 越线者死 on the flanker, then squire moves + kills pupA (survives at 1) =====
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, Overline), TargetUnitId = Uid(C(3, 0)) });
        Assert.Null(At(C(3, 0)));               // flanker executed (4 dmg on your half)
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(2, 1)), To = C(1, 1) });
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(1, 1)), TargetUnitId = Uid(C(1, 0)) });
        Assert.Null(At(C(1, 0)));               // pupA dead
        Assert.Equal(1, At(C(1, 1))!.CurrentHp); // squire survives the retaliation (2 → 1, 坚守 inactive after moving)
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T8 · opponent (4 mana): pass =====
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T9 · player (5 mana): deploy the sentinel, tuck the squire above it =====
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, Sentinel), TargetCell = C(2, 0) });
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(1, 1)), To = C(2, 1) });
        Assert.Equal(Sentinel, At(C(2, 0))!.CardId);
        Assert.Equal(Squire, At(C(2, 1))!.CardId);
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T10 · opponent (5 mana): deploy the rift leaper, end =====
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = Hand(1, Leaper), TargetCell = C(2, 3) });
        Assert.Equal(Leaper, At(C(2, 3))!.CardId);
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T11 · player (6 mana): deploy the archer, push up, then chip the leaper (guardian soaks the retaliation) =====
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, Archer), TargetCell = C(1, 0) });
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(2, 1)), To = C(2, 2) }); // squire up to (2,2)
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(2, 0)), To = C(2, 1) }); // sentinel up to (2,1)
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(2, 2)), TargetUnitId = Uid(C(2, 3)) });
        Assert.Equal(3, At(C(2, 3))!.CurrentHp); // leaper 5 → 3
        Assert.Equal(3, At(C(2, 1))!.CurrentHp); // sentinel (guardian) soaked the 4-dmg retaliation: 7 → 3
        Assert.Equal(1, At(C(2, 2))!.CurrentHp); // squire took none
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T12 · opponent (6 mana): the leaper strikes the squire; the guardian dies protecting it =====
        await Ok(new AttackCommand { Seat = 1, AttackerEntityId = Uid(C(2, 3)), TargetUnitId = Uid(C(2, 2)) });
        Assert.Null(At(C(2, 1)));                // sentinel (guardian) soaked the hit and fell (3 - 4)
        Assert.Equal(1, At(C(2, 2))!.CurrentHp); // squire safe (soaked)
        Assert.Equal(1, At(C(2, 3))!.CurrentHp); // leaper took the squire's retaliation: 3 → 1
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T13 · player (7 mana): 强行军 → archer moves 2 & kills the leaper; squire breaks onto the enemy line =====
        await Ok(new PlayCardCommand { Seat = 0, CardEntityId = Hand(0, ForcedMarch), TargetUnitId = Uid(C(1, 0)) });
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(1, 0)), To = C(1, 1) });
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(1, 1)), To = C(1, 2) });
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(1, 2)), TargetUnitId = Uid(C(2, 3)) });
        Assert.Null(At(C(2, 3)));                // leaper dead (ranged, no retaliation)
        await Ok(new MoveUnitCommand { Seat = 0, UnitEntityId = Uid(C(2, 2)), To = C(2, 3) }); // squire onto the enemy home row
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(2, 3)), TargetLeader = true });
        Assert.Equal(3, host.GetView(0).Opponent.LeaderHp); // squire 2 → 5-2 = 3
        await Ok(new EndTurnCommand { Seat = 0 });

        // ===== T14 · opponent (round 7): tide bleeds it (start round lowered to 7), then it drops the huntress =====
        Assert.Equal(2, host.GetView(0).Opponent.LeaderHp); // 压力潮汐 at round 7 (no unit in our half): 3 → 2
        await Ok(new PlayCardCommand { Seat = 1, CardEntityId = Hand(1, Huntress), TargetCell = C(1, 3) });
        Assert.Equal(Huntress, At(C(1, 3))!.CardId);
        await Ok(new EndTurnCommand { Seat = 1 });

        // ===== T15 · player (8 mana): 直捣黄龙 — the squire is already on the enemy home row, so strike the leader dead =====
        await Ok(new AttackCommand { Seat = 0, AttackerEntityId = Uid(C(2, 3)), TargetLeader = true }); // squire 2 → 2-2 = 0

        Assert.NotNull(host.GetView(0).Result);
        Assert.Equal(0, host.GetView(0).Result!.WinnerSeat); // the player (seat 0) wins
        Assert.Equal(4, host.GetView(0).Self.LeaderHp);       // the player's leader never dropped below 4
    }
}
