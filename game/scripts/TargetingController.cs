using System;
using System.Collections.Generic;
using System.Linq;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Game;

/// <summary>What (if anything) is currently selected / being aimed.</summary>
public enum TargetingKind { None, Card, Unit, Leader }

/// <summary>Semantic highlight/message color for a pick — the host maps these onto BattleTheme colors
/// (Default→Accent, Danger→DangerColor, Receiver→HpColor, Channel→CostColor).</summary>
public enum PickHighlight { Default, Danger, Receiver, Channel }

/// <summary>The only unit facts a targeting decision ever needs — kept engine-pure (no Godot, no view types).</summary>
public readonly record struct TargetUnitFact(Cell Cell, int OwnerSeat);

/// <summary>Everything the targeting state machine asks of the scene: fact queries (legal commands, unit
/// position/owner, card semantics) and presentation requests (highlights, log messages, the echo button
/// bar, the selection UI) plus the final "command ready" submission.</summary>
public interface ITargetingHost
{
	// ---- facts ----
	int ActiveSeat { get; }
	IReadOnlyList<Command> LegalCommands(int seat);
	TargetUnitFact? Unit(int entityId);
	bool CandidatesDealDamage(IReadOnlyList<Command> candidates);
	bool CandidatesAreFriendlyReceivers(IReadOnlyList<Command> candidates);

	// ---- presentation requests ----
	void ClearHighlights();
	void HighlightCell(Cell cell);
	void HighlightUnit(int unitId, PickHighlight color);
	void HighlightLeader();
	void RefreshSelectionUi();
	void ShowEchoBar(bool global);
	void CloseEchoBar();
	void Log(string message);
	void LogPick(string keyword, PickHighlight color, string instruction);

	// ---- command ready ----
	void Submit(Command cmd);
}

/// <summary>
/// docs/22 批次E2: the selection/aiming state machine, extracted from BattleScene. Owns the six-piece
/// state cluster (kind / candidates / chosen cell / extra pick / cross-aim flag) and the whole decision
/// pipeline: start from the LegalCommands candidate list, then narrow it click by click (card → cell →
/// unit → 引导者/二段目标/门德复述) until exactly one Command remains and is handed to the host for
/// submission. Pure C# — never references Godot; the host renders highlights and owns every node.
/// Busy-gating and drag visuals stay host-side: the host only forwards input facts while interactable.
/// </summary>
public sealed class TargetingController
{
	// docs/21: after cell+target are pinned, a 引导·N order still needs its 引导者, and 焰鞭's friendly mode a
	// 二段目标 — an extra unit pick that narrows the remaining candidates before submit.
	private enum ExtraPick { None, Channeler, Secondary, Echo }

	private readonly ITargetingHost _host;
	private List<Command> _candidates = new();
	private Cell? _chosenCell;
	private ExtraPick _extraPick = ExtraPick.None;
	private bool _crossAim; // true while aiming a 十字 AOE order (cell_cross_all) — hover shows the footprint

	public TargetingController(ITargetingHost host) => _host = host;

	/// <summary>Current selection kind — None means nothing is selected/aiming.</summary>
	public TargetingKind Kind { get; private set; } = TargetingKind.None;

	/// <summary>True while a 十字 AOE order is being aimed (hover should preview the blast footprint).</summary>
	public bool CrossAim => _crossAim;

	// Which hand card is selected — DERIVED, never stored: every Card-kind candidate is a PlayCardCommand for the
	// same card. Deriving it means a transient un-lift (mid-drag, a _busy pulse) can never forget it (review fix).
	public int? SelectedCardId =>
		Kind == TargetingKind.Card && _candidates.FirstOrDefault() is PlayCardCommand p ? p.CardEntityId : null;

	/// <summary>Whether <paramref name="center"/> is a legal center for the current cross-AOE candidates.</summary>
	public bool IsCrossCenter(Cell center) => _candidates.Any(c => CellOf(c) is { } cc && cc == center);

	/// <summary>Whether some candidate targets exactly this cell (drag-hover bright highlight).</summary>
	public bool IsExactLegalCell(Cell cell) =>
		_candidates.Any(c => CellOf(c) is { } x && x.Col == cell.Col && x.Row == cell.Row);

	/// <summary>Drop every pending selection: reset the state cluster, close the echo bar, clear highlights
	/// and reconcile the selection UI (取消 button / card lift).</summary>
	public void Clear()
	{
		Kind = TargetingKind.None;
		_candidates.Clear();
		_chosenCell = null;
		_crossAim = false;
		_extraPick = ExtraPick.None;
		_host.CloseEchoBar(); // 门德复述: drop the 空放/再次施放 bar if a pick was pending
		_host.ClearHighlights();
		_host.RefreshSelectionUi(); // SelectedCardId now derives to null → hides 取消 and drops the card lift
	}

	/// <summary>A hand card was picked (tap or drag start). <paramref name="crossAim"/> is the host-evaluated
	/// "this card aims a 十字 AOE" fact (needs the card database, so it stays scene-side). A no-target 指令 no
	/// longer fires on the pick — it stages a 使用 confirmation instead (see <see cref="NoTargetReady"/> /
	/// <see cref="ConfirmNoTarget"/>) so brushing the card while reading its effect can't play it by accident.</summary>
	public void SelectCard(int cardEntityId, bool crossAim)
	{
		int seat = _host.ActiveSeat;
		var legal = _host.LegalCommands(seat);
		_candidates = legal.Where(c => c is PlayCardCommand p && p.CardEntityId == cardEntityId).ToList();
		Kind = TargetingKind.Card;
		_chosenCell = null;
		_crossAim = crossAim; // 十字 AOE → hover shows friendly-fire footprint
		_extraPick = ExtraPick.None; // a fresh pick must not inherit a stale 引导者/复述 dimension
		_host.CloseEchoBar(); // ...nor leave a 门德复述 bar from a prior selection on screen
		_host.ClearHighlights();

		if (_candidates.Count == 0) { _host.Log("这张牌现在打不出。"); Clear(); return; }
		// Non-directional channels (燔火/燎原) still need their channeler disambiguated first. Otherwise they
		// would fall through to the unit-target prompt even though their commands carry no primary target.
		if (_candidates.All(c => CellOf(c) is null && UnitOf(c) is null) && PromptExtraPick()) return;
		// No-target 指令 (抽牌指令 等): stage it and let the host offer 使用 — a tap no longer auto-plays, so a
		// mis-tap while reading the effect can't fire it. Dragging the card onto the board also confirms
		// (the host routes a board drop to ConfirmNoTarget while NoTargetReady holds).
		if (NoTargetReady)
		{
			_host.Log("点「使用」打出这张指令,或将卡拖到场上。");
			_host.RefreshSelectionUi();
			return;
		}

		RefreshCandidateHighlights();
		_host.RefreshSelectionUi(); // lift the card + show 取消 (skipped mid-drag; re-applied once the drag drops)
	}

	/// <summary>True when the card selection has converged to a single no-target play awaiting 使用 confirmation
	/// (no cell, no unit, no pending 引导者/复述 pick). The host shows the 使用 button and treats a board drop as
	/// confirmation while this holds.</summary>
	public bool NoTargetReady =>
		Kind == TargetingKind.Card && _extraPick == ExtraPick.None && _candidates.Count == 1
		&& CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null;

	/// <summary>使用 / 拖到场上: submit the sole staged no-target play. No-op unless <see cref="NoTargetReady"/>.</summary>
	public void ConfirmNoTarget()
	{
		if (NoTargetReady) _host.Submit(_candidates[0]);
	}

	/// <summary>Re-highlight the current candidates (cells to aim at, or the target/receiver units) and repeat
	/// the pick instruction. Also used by the host to restore the plain highlight after a hover preview.</summary>
	public void RefreshCandidateHighlights()
	{
		var cells = _candidates.Select(CellOf).Where(c => c != null).Select(c => c!.Value).Distinct().ToList();
		bool needCell = cells.Count > 0 && _chosenCell is null;
		if (needCell)
		{
			foreach (var cell in cells)
				_host.HighlightCell(cell);
			if (_host.CandidatesDealDamage(_candidates))
				_host.LogPick("作用目标", PickHighlight.Danger, "选择一个格子施放。");
			else
				_host.Log("选择一个格子放置 / 施放。");
		}
		else
		{
			bool receiver = _host.CandidatesAreFriendlyReceivers(_candidates);
			var color = receiver ? PickHighlight.Receiver : PickHighlight.Danger;
			foreach (var id in _candidates.Select(UnitOf).Where(u => u != null).Select(u => u!.Value).Distinct())
				_host.HighlightUnit(id, color);
			_host.LogPick(receiver ? "接受目标" : "作用目标", color,
				receiver ? "选择一个友方随从接受效果。" : "选择一个随从承受效果。");
		}
	}

	/// <summary>A unit was clicked while interactable (the host already showed the inspector and gated _busy):
	/// fills a pending extra pick first, else tries it as a target, else selects the friendly unit to act.</summary>
	public void OnUnitClicked(int entityId)
	{
		// docs/21: a click during a pending 引导者 / 二段目标 / 门德复述 pick fills that dimension first.
		if (_extraPick == ExtraPick.Channeler) { PickExtra(ChannelerOf, entityId); return; }
		if (_extraPick == ExtraPick.Secondary) { PickExtra(SecondaryOf, entityId); return; }
		if (_extraPick == ExtraPick.Echo) { PickEchoUnit(entityId); return; }

		int seat = _host.ActiveSeat;

		// If we're mid-target-pick, treat as a target first (a unit target, or the unit's cell for an AOE).
		if (Kind is TargetingKind.Card or TargetingKind.Leader && TryPickUnitOrItsCell(entityId)) return;

		var unit = _host.Unit(entityId);
		if (unit is null) return;

		if (unit.Value.OwnerSeat != seat)
		{
			// Clicking an enemy while a unit is selected = attack it.
			if (Kind == TargetingKind.Unit && TryPickUnitTarget(entityId)) return;
			return; // enemy piece: detail is already shown, nothing else to do
		}

		var legal = _host.LegalCommands(seat);
		_candidates = legal.Where(c =>
			(c is MoveUnitCommand m && m.UnitEntityId == entityId) ||
			(c is AttackCommand a && a.AttackerEntityId == entityId)).ToList();
		Kind = TargetingKind.Unit;
		_chosenCell = null;
		_host.ClearHighlights();
		_host.HighlightUnit(entityId, PickHighlight.Default);
		_host.RefreshSelectionUi(); // a selected unit can also be backed out of with 取消

		if (_candidates.Count == 0) { _host.Log("这个随从本回合无法行动。"); return; }
		foreach (var cmd in _candidates)
		{
			if (cmd is MoveUnitCommand m) _host.HighlightCell(m.To);
			else if (cmd is AttackCommand a)
			{
				if (a.TargetLeader) _host.HighlightLeader();
				else if (a.TargetUnitId is { } t) _host.HighlightUnit(t, PickHighlight.Default);
			}
		}
		_host.Log("移动到高亮格,或攻击高亮目标。");
	}

	private bool TryPickUnitTarget(int unitId)
	{
		var filtered = _candidates.Where(c => UnitOf(c) == unitId).ToList();
		if (filtered.Count == 0) return false;
		if (filtered.Count == 1) { _host.Submit(filtered[0]); return true; }
		_candidates = filtered;
		if (PromptExtraPick()) return true; // a 引导·N target still needs its 引导者
		_host.ClearHighlights();
		RefreshCandidateHighlights();
		_host.RefreshSelectionUi();
		return true;
	}

	/// <summary>A click landing on a unit while aiming: first try it as a unit target, otherwise (a
	/// row/column/十字 AOE order or leader skill) treat it as picking that unit's CELL — a click on an
	/// occupied cell must aim the AOE there, not fizzle (the standee sits on top of the cell button).</summary>
	public bool TryPickUnitOrItsCell(int unitId)
	{
		if (_extraPick == ExtraPick.Channeler) { PickExtra(ChannelerOf, unitId); return true; }
		if (_extraPick == ExtraPick.Secondary) { PickExtra(SecondaryOf, unitId); return true; }
		if (TryPickUnitTarget(unitId)) return true;
		var u = _host.Unit(unitId);
		if (u != null && _candidates.Any(c => CellOf(c) is { } cc && cc == u.Value.Cell))
		{
			PickCell(u.Value.Cell);
			return true;
		}
		return false;
	}

	/// <summary>A board cell was picked (click or drag drop): narrow the candidates to that cell (with the
	/// column fallback for spatial column orders) and submit if it resolves to a single command.</summary>
	public void PickCell(Cell cell)
	{
		if (Kind == TargetingKind.None) return;
		if (_extraPick == ExtraPick.Echo) { PickEchoCell(cell); return; } // 门德复述: cell-aimed re-cast

		// Exact cell match, else column fallback (spatial column orders).
		var exact = _candidates.Where(c => CellOf(c) is { } cc && cc.Col == cell.Col && cc.Row == cell.Row).ToList();
		var pick = exact.Count > 0 ? exact : _candidates.Where(c => CellOf(c) is { } cc && cc.Col == cell.Col).ToList();
		if (pick.Count == 0) { _host.Log("这里不是合法目标。"); return; }
		if (pick.Count == 1 && (UnitOf(pick[0]) is null)) { _host.Submit(pick[0]); return; }

		// Deploy cell chosen but a battlecry target is still needed — or a 引导·N cell order needs its 引导者.
		_candidates = pick;
		_chosenCell = cell;
		if (PromptExtraPick()) return; // 烟幕弹 etc.: cell pinned, now pick the 引导者
		_host.ClearHighlights();
		RefreshCandidateHighlights();
		_host.RefreshSelectionUi(); // after a drag-drop this is where the lift + 取消 first appear
	}

	/// <summary>The enemy leader plate was picked: submit the sole leader attack if one is staged.</summary>
	public void PickLeader()
	{
		var filtered = _candidates.Where(c => c is AttackCommand { TargetLeader: true }).ToList();
		if (filtered.Count == 1) { _host.Submit(filtered[0]); return; }
		_host.Log("需要先选中一个能攻击本体的随从。");
	}

	/// <summary>The leader-skill button was pressed: stage the UseLeaderSkill candidates (auto-submit when untargeted).</summary>
	public void SelectLeaderSkill()
	{
		int seat = _host.ActiveSeat;
		var legal = _host.LegalCommands(seat);
		_candidates = legal.Where(c => c is UseLeaderSkillCommand).ToList();
		Kind = TargetingKind.Leader;
		_chosenCell = null;
		_host.ClearHighlights();
		if (_candidates.Count == 0) { _host.Log("现在无法发动领袖技能。"); Clear(); return; }
		if (_candidates.Count == 1 && CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null)
		{ _host.Submit(_candidates[0]); return; }
		RefreshCandidateHighlights();
		_host.RefreshSelectionUi(); // leader skill is aiming — offer 取消
	}

	// ---------- extra picks (引导者 / 二段目标 / 门德复述) ----------

	/// <summary>After cell+target are pinned, a 引导·N order still needs its 引导者, and 焰鞭's friendly mode a
	/// distinct 二段目标. Highlights the remaining distinct choices and waits for a click. Returns true when it
	/// took over (an extra pick is pending); false when there is nothing left to disambiguate.</summary>
	private bool PromptExtraPick()
	{
		if (_candidates.Count <= 1)
			return false;

		var channelers = _candidates.Select(ChannelerOf).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
		if (channelers.Count > 1)
		{
			_extraPick = ExtraPick.Channeler;
			_host.ClearHighlights();
			foreach (var id in channelers) _host.HighlightUnit(id, PickHighlight.Channel); // teal = 引导
			_host.LogPick("引导者", PickHighlight.Channel, "选择一个友方随从引导（费用/伤害随引导者变化）。");
			_host.RefreshSelectionUi();
			return true;
		}

		var secondaries = _candidates.Select(SecondaryOf).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
		if (secondaries.Count > 1)
		{
			_extraPick = ExtraPick.Secondary;
			_host.ClearHighlights();
			foreach (var id in secondaries) _host.HighlightUnit(id, PickHighlight.Receiver);
			_host.LogPick("接受目标", PickHighlight.Receiver, "选择接收属性的另一个友方随从。");
			_host.RefreshSelectionUi();
			return true;
		}

		// 薪火回响·门德 (docs/21 §3.1): the turn's first 薪炎 order can be RECAST at a target you re-aim, or 空放.
		// The candidate set carries both an EchoRecast=false baseline and one variant per re-aim target.
		var recasts = _candidates.Where(c => c is PlayCardCommand { EchoRecast: true }).ToList();
		bool canDecline = _candidates.Any(c => c is PlayCardCommand { EchoRecast: false });
		if (recasts.Count > 0 && canDecline)
		{
			_extraPick = ExtraPick.Echo;
			_host.ClearHighlights();
			var echoUnits = recasts.Select(EchoUnitOf).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var echoCells = recasts.Select(EchoCellOf).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			foreach (var id in echoUnits) _host.HighlightUnit(id, PickHighlight.Danger); // red = 复述目标
			foreach (var cell in echoCells) _host.HighlightCell(cell);
			bool global = echoUnits.Count == 0 && echoCells.Count == 0; // 燎原/燔火: board-wide, no aim
			_host.ShowEchoBar(global);
			if (global)
				_host.Log("薪火回响·门德：再次施放这道薪炎指令，或点「空放」放弃。");
			else
				_host.LogPick("作用目标", PickHighlight.Danger, "选择一个复述目标，或点「空放」放弃。");
			_host.RefreshSelectionUi();
			return true;
		}
		return false;
	}

	/// <summary>A unit click during the 门德 recast pick: keep only the recast variant aimed at that unit and submit.
	/// A cell-aimed echo (row/column/十字 order) has no unit variant, so fall back to the standee's cell.</summary>
	private void PickEchoUnit(int entityId)
	{
		var filtered = _candidates.Where(c => EchoUnitOf(c) == entityId).ToList();
		if (filtered.Count > 0) { _host.CloseEchoBar(); _extraPick = ExtraPick.None; _host.Submit(filtered[0]); return; }
		var u = _host.Unit(entityId);
		if (u != null && _candidates.Any(c => EchoCellOf(c) is { } cc && cc == u.Value.Cell)) { PickEchoCell(u.Value.Cell); return; }
		_host.Log("请点选高亮的复述目标,或点「空放」。");
	}

	private void PickEchoCell(Cell cell)
	{
		var filtered = _candidates.Where(c => EchoCellOf(c) is { } cc && cc.Col == cell.Col && cc.Row == cell.Row).ToList();
		if (filtered.Count == 0) { _host.Log("请点选高亮的复述目标,或点「空放」。"); return; }
		_host.CloseEchoBar();
		_extraPick = ExtraPick.None;
		_host.Submit(filtered[0]);
	}

	/// <summary>空放/取消: submit the EchoRecast=false baseline — the order resolves once, no recast.</summary>
	public void DeclineEcho()
	{
		var baseline = _candidates.FirstOrDefault(c => c is PlayCardCommand { EchoRecast: false });
		if (baseline is null) { _host.CloseEchoBar(); Clear(); return; }
		_host.CloseEchoBar();
		_extraPick = ExtraPick.None;
		_host.Submit(baseline);
	}

	/// <summary>再次施放 (board-wide orders only): submit the sole EchoRecast=true variant.</summary>
	public void ConfirmGlobalEcho()
	{
		var recast = _candidates.FirstOrDefault(c => c is PlayCardCommand { EchoRecast: true });
		if (recast is null) { DeclineEcho(); return; }
		_host.CloseEchoBar();
		_extraPick = ExtraPick.None;
		_host.Submit(recast);
	}

	/// <summary>A click during a pending 引导者 / 二段目标 pick: keep only the candidates whose extra dimension
	/// matches the clicked unit, then submit (or prompt the next dimension).</summary>
	private void PickExtra(Func<Command, int?> dim, int entityId)
	{
		var filtered = _candidates.Where(c => dim(c) == entityId).ToList();
		if (filtered.Count == 0) { _host.Log("请点选高亮的随从。"); return; }
		_candidates = filtered;
		_extraPick = ExtraPick.None;
		if (filtered.Count == 1) { _host.Submit(filtered[0]); return; }
		PromptExtraPick(); // e.g. 引导者 chosen → now the 二段目标
	}

	// ---------- command target helpers ----------

	public static Cell? CellOf(Command c) => c switch
	{
		PlayCardCommand p => p.TargetCell,
		MoveUnitCommand m => m.To,
		UseLeaderSkillCommand s => s.TargetCell,
		_ => null,
	};

	public static int? UnitOf(Command c) => c switch
	{
		PlayCardCommand p => p.TargetUnitId,
		UseLeaderSkillCommand s => s.TargetUnitId,
		AttackCommand a when !a.TargetLeader => a.TargetUnitId,
		_ => null,
	};

	// docs/21 §1.2/§1.8: the extra command dimensions a 引导·N / 焰鞭 play carries.
	private static int? ChannelerOf(Command c) => (c as PlayCardCommand)?.ChannelerUnitId;
	private static int? SecondaryOf(Command c) => (c as PlayCardCommand)?.SecondaryTargetUnitId;

	// docs/21 §3.1: the 门德 recast dimensions — only the EchoRecast=true variants carry a re-aim.
	private static int? EchoUnitOf(Command c) => c is PlayCardCommand { EchoRecast: true } p ? p.EchoTargetUnitId : null;
	private static Cell? EchoCellOf(Command c) => c is PlayCardCommand { EchoRecast: true } p ? p.EchoTargetCell : null;
}
