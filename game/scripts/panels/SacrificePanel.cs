using Godot;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>
/// 熔剑祭士 献祭面板 (docs/21 §3.2) — docs/22 批次E3: lifted out of BattleScene. One-shot modal:
/// <see cref="TryShow"/> both decides whether the deploy needs the picker and shows it. The panel
/// builds the real command (with SacrificeEntityIds) and hands it back via <paramref name="submit"/>;
/// it never reads the scene.
/// </summary>
public static class SacrificePanel
{
	/// <summary>True (and shows the panel) when this deploy is a 熔剑祭士 with ≥2 hand orders to sacrifice —
	/// otherwise the caller deploys plain. <paramref name="submit"/> receives either the enriched command
	/// (献祭并装备) or the original deploy with an empty sacrifice list (直接上场). <paramref name="onCancel"/>
	/// runs when the player backs out entirely (取消) — the card is never played, so the caller resets any
	/// pending selection/highlights.</summary>
	public static bool TryShow(Control overlayLayer, CardDatabase cards, SfxBank sfx, PlayerView view,
		PlayCardCommand deploy, System.Action<Command> submit, System.Action onCancel)
	{
		if (view.Self.Hand.FirstOrDefault(h => h.EntityId == deploy.CardEntityId) is not { } handCard
			|| !cards.TryGet(handCard.CardId, out var def)
			|| !def.NeedsSacrificePicker) // Rules-side semantic (docs/22 D5) — no magic-string matching here
			return false;

		var orders = view.Self.Hand
			.Where(h => h.EntityId != deploy.CardEntityId && cards.TryGet(h.CardId, out var d) && d.Type == CardType.Order)
			.ToList();
		if (orders.Count < 2)
			return false; // nothing to sacrifice → the caller just deploys the 2/4 body

		Show(overlayLayer, cards, sfx, deploy, orders, submit, onCancel);
		return true;
	}

	private static void Show(Control overlayLayer, CardDatabase cards, SfxBank sfx,
		PlayCardCommand deploy, List<CardInHandView> orders, System.Action<Command> submit, System.Action onCancel)
	{
		var picks = new List<int>();
		// The parchment has a thick painted frame (about 76 px on every edge), so all content lives inside
		// an explicit safe area. Keep every child in panel-local coordinates: the old implementation mixed
		// panel-local headings with screen-space cards/actions, which let them drift outside the canvas.
		const float pw = 1300, cardsTop = 218, choiceH = 112, choiceGapY = 14;
		const int choicesPerRow = 3;
		int choiceRows = (orders.Count + choicesPerRow - 1) / choicesPerRow;
		float cardsBottom = cardsTop + choiceRows * choiceH + (choiceRows - 1) * choiceGapY;
		float pickCountY = cardsBottom + 18;
		float actionsY = pickCountY + 42;
		float buttonY = actionsY - 34; // lift the action pair clear of the parchment's bottom ornament
		float ph = actionsY + 64 + 86; // bottom ornament safe area
		float px = (BattleTheme.ScreenW - pw) / 2f, py = (BattleTheme.ScreenH - ph) / 2f;
		var ember = Color.FromHtml("c65a3d");
		var emberDark = Color.FromHtml("513029");
		var bronze = Color.FromHtml("8f7247");
		var charred = Color.FromHtml("2a2520");

		var overlay = new Button { Name = "__sacrifice_panel", Flat = true };
		overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		overlay.AddThemeStyleboxOverride("normal", BattleTheme.Box(new Color(0, 0, 0, 0.72f), null, 0, 0));

		void Close()
		{
			if (overlayLayer.GetNodeOrNull("__sacrifice_panel") is { } p) { overlayLayer.RemoveChild(p); p.QueueFree(); }
			picks.Clear();
		}

		var panel = new Panel
		{
			Position = new Vector2(px, py),
			Size = new Vector2(pw, ph),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		panel.AddThemeStyleboxOverride("panel", (StyleBox?)BattleTheme.ParchmentPanel() ?? BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 3, 12));
		overlay.AddChild(panel);

		// A short plaque title carries the dramatic beat; the rules are split into two quieter lines below it
		// instead of forcing one long sentence through the ornamental frame.
		var plaque = BattleTheme.TitlePlaque(new Vector2(240, 24), new Vector2(820, 96));
		bool hasPlaque = plaque is not null;
		if (plaque is not null) panel.AddChild(plaque);
		var title = hasPlaque
			? BattleTheme.MakeOutlinedLabel("熔剑祭士 · 熔岩献祭", 32, BattleTheme.TextMain, HorizontalAlignment.Center)
			: BattleTheme.MakeLabel("熔剑祭士 · 熔岩献祭", 32, BattleTheme.InkMain, HorizontalAlignment.Center);
		title.AddThemeFontOverride("font", BattleTheme.HeadingFont);
		title.Position = new Vector2(250, 43); title.Size = new Vector2(800, 48);
		panel.AddChild(title);

		var callout = BattleTheme.MakeLabel("选择 2 张指令牌投入炉火", 22, BattleTheme.InkMain, HorizontalAlignment.Center);
		callout.AddThemeFontOverride("font", BattleTheme.UiFontBold);
		callout.Position = new Vector2(90, 120); callout.Size = new Vector2(pw - 180, 34);
		panel.AddChild(callout);
		var reward = BattleTheme.MakeLabel("熔岩巨剑  ◆  +3 攻击   ◆  射程 2   ◆  贯穿", 20, emberDark, HorizontalAlignment.Center);
		reward.AddThemeFontOverride("font", BattleTheme.UiFontBold);
		reward.Position = new Vector2(90, 153); reward.Size = new Vector2(pw - 180, 32);
		panel.AddChild(reward);
		var hint = BattleTheme.MakeLabel("献祭牌进入墓地，之后仍可被复燃或信使回收", 16, BattleTheme.InkDim, HorizontalAlignment.Center);
		hint.Position = new Vector2(90, 183); hint.Size = new Vector2(pw - 180, 26);
		panel.AddChild(hint);

		var cardBtns = new Dictionary<int, Button>();
		var stateLabels = new Dictionary<int, Label>();
		Button? equipBtn = null;
		var pickCount = BattleTheme.MakeLabel("献祭槽  0 / 2", 18, BattleTheme.InkDim, HorizontalAlignment.Center);
		pickCount.AddThemeFontOverride("font", BattleTheme.UiFontBold);
		pickCount.Position = new Vector2(90, pickCountY); pickCount.Size = new Vector2(pw - 180, 30);
		panel.AddChild(pickCount);
		void Repaint()
		{
			foreach (var (id, b) in cardBtns)
			{
				bool selected = picks.Contains(id);
				BattleTheme.SetButtonBg(b, selected ? emberDark : charred, selected ? ember : bronze, selected ? 4 : 2, 10);
				stateLabels[id].Text = selected ? "◆  已选入炉" : "点击选择";
				stateLabels[id].AddThemeColorOverride("font_color", selected ? new Color(1f, 0.69f, 0.46f) : BattleTheme.TextDim);
			}
			pickCount.Text = $"献祭槽  {picks.Count} / 2";
			pickCount.AddThemeColorOverride("font_color", picks.Count == 2 ? emberDark : BattleTheme.InkDim);
			if (equipBtn != null) equipBtn.Disabled = picks.Count != 2;
		}

		// Wide, card-like choice tiles: real card art provides recognition, while a stable name/cost column
		// remains readable. Three columns keep the maximum eight eligible cards within three rows.
		const float cw = 350, gapX = 20;
		for (int i = 0; i < orders.Count; i++)
		{
			var o = orders[i];
			cards.TryGet(o.CardId, out var od);
			int col = i % choicesPerRow, row = i / choicesPerRow;
			int rowCount = Math.Min(choicesPerRow, orders.Count - row * choicesPerRow);
			float rowW = rowCount * cw + (rowCount - 1) * gapX;
			float bx = (pw - rowW) / 2f + col * (cw + gapX), by = cardsTop + row * (choiceH + choiceGapY);
			var b = BattleTheme.MakeButton(new Vector2(bx, by), new Vector2(cw, choiceH), charred, bronze, 2, 10);

			var artFrame = new Panel { Position = new Vector2(12, 10), Size = new Vector2(78, 92), MouseFilter = Control.MouseFilterEnum.Ignore };
			artFrame.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.08f, 0.07f, 0.06f, 1f), bronze, 2, 6));
			b.AddChild(artFrame);
			if (BattleTheme.Tex($"cards/{o.CardId}.png") is { } art)
				b.AddChild(CardView.ArtWindow(art, o.CardId, new Vector2(16, 14), new Vector2(70, 84)));

			var cardName = BattleTheme.MakeOutlinedLabel(od?.Name ?? o.CardId, 22, BattleTheme.TextMain, HorizontalAlignment.Left);
			cardName.Position = new Vector2(106, 13); cardName.Size = new Vector2(226, 32);
			b.AddChild(cardName);
			var meta = BattleTheme.MakeLabel($"指令牌   ·   {od?.Cost ?? 0} 费", 17, new Color(0.57f, 0.82f, 0.78f), HorizontalAlignment.Left);
			meta.AddThemeFontOverride("font", BattleTheme.UiFontBold);
			meta.Position = new Vector2(106, 44); meta.Size = new Vector2(226, 26);
			b.AddChild(meta);
			var state = BattleTheme.MakeLabel("点击选择", 16, BattleTheme.TextDim, HorizontalAlignment.Left);
			state.Position = new Vector2(106, 73); state.Size = new Vector2(226, 24);
			b.AddChild(state);
			int id = o.EntityId;
			b.Pressed += () =>
			{
				if (picks.Contains(id)) picks.Remove(id);
				else if (picks.Count < 2) picks.Add(id);
				sfx.Play("button");
				Repaint();
			};
			cardBtns[id] = b;
			stateLabels[id] = state;
			panel.AddChild(b);
		}

		// Three actions on one row: 献祭并装备 (equip), 直接上场 (deploy plain), 取消 (abort the whole play).
		// The player used to be trapped here — 直接上场 re-sent the SacrificeEntityIds:null command, which the
		// caller's gate re-read as "not decided yet" and re-opened this same panel (looked frozen). Plain deploy
		// now sends an EMPTY sacrifice list (rules: null/empty = decline), and 取消 backs out without playing.
		const float btnW = 250, btnGap = 28;
		float btnRowW = 3 * btnW + 2 * btnGap;
		float btnRowX = (pw - btnRowW) / 2f;

		equipBtn = BattleTheme.MakeButton(new Vector2(btnRowX, buttonY), new Vector2(btnW, 64), BattleTheme.AtkColor, BattleTheme.AtkColor, 2, 10, textured: true);
		// The gold plate uses 30 px nine-slice rails. Their implicit content margins used to force this control
		// taller than its steel twins, despite identical Size/Position values. Keep the ornate rails, but give
		// the text a compact content box so all three buttons resolve to the same 64 px visual height.
		foreach (var stateName in new[] { "normal", "hover", "pressed", "focus", "disabled" })
			if (equipBtn.GetThemeStylebox(stateName) is { } style)
			{
				style.ContentMarginTop = 10;
				style.ContentMarginBottom = 10;
			}
		equipBtn.Text = "献祭并装备"; equipBtn.AddThemeFontSizeOverride("font_size", 22);
		equipBtn.Pressed += () =>
		{
			if (picks.Count != 2) return;
			var withSac = deploy with { SacrificeEntityIds = picks.ToList() };
			Close();
			submit(withSac);
		};
		panel.AddChild(equipBtn);

		var skipBtn = BattleTheme.MakeButton(new Vector2(btnRowX + (btnW + btnGap), buttonY), new Vector2(btnW, 64), BattleTheme.PanelDark, bronze, 2, 10, textured: true);
		skipBtn.Text = "直接上场"; skipBtn.AddThemeFontSizeOverride("font_size", 22);
		skipBtn.Pressed += () => { sfx.Play("button"); Close(); submit(deploy with { SacrificeEntityIds = new List<int>() }); };
		panel.AddChild(skipBtn);

		var cancelBtn = BattleTheme.MakeButton(new Vector2(btnRowX + 2 * (btnW + btnGap), buttonY), new Vector2(btnW, 64), BattleTheme.PanelDark, bronze, 2, 10, textured: true);
		cancelBtn.Text = "取消"; cancelBtn.AddThemeFontSizeOverride("font_size", 22);
		cancelBtn.Pressed += () => { sfx.Play("button"); Close(); onCancel(); };
		panel.AddChild(cancelBtn);

		Repaint();
		overlayLayer.AddChild(overlay);
	}
}
