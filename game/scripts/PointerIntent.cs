using Godot;
using System;

// docs/19 A2 — per-Control gesture translator. "Hover to preview / right-click to inspect" desktop
// interactions become touch long-press on mobile, without call sites knowing which platform they run on.
//
// Desktop (no touchscreen): call sites keep their native MouseEntered/MouseExited/right-click wiring and
// never attach this component, so the desktop input path is byte-for-byte unchanged. On touch devices the
// call site attaches one PointerIntent per target Control instead; it listens to the target's GuiInput and
// emits semantic signals:
//   LongPressed        — held >= LongPressSeconds without moving past MoveCancelPixels
//   LongPressReleased  — the pointer lifted after a long press
//   Dragged            — moved past MoveCancelPixels before the long-press fired (a drag/scroll, not a hold)
//   Tapped             — a quick press+release, no long-press and no drag
//
// The project keeps `emulate_mouse_from_touch` on (tap-to-play relies on it), so a finger arrives here as
// emulated mouse events; handling those (rather than raw InputEventScreenTouch) avoids double-firing.
//
// Timings are consts on purpose: the feel (dwell, drag threshold) is tuned on a real device in docs/19
// D-track (risk R3), and keeping them here makes that a one-file change.
public partial class PointerIntent : Node
{
	public const float LongPressSeconds = 0.4f;
	public const float MoveCancelPixels = 16f;

	[Signal] public delegate void LongPressedEventHandler();
	[Signal] public delegate void LongPressReleasedEventHandler();
	[Signal] public delegate void DraggedEventHandler();
	[Signal] public delegate void TappedEventHandler();

	private bool _down;             // a press is in progress
	private bool _fired;            // long-press already emitted this press cycle
	private bool _moved;            // moved past the drag threshold this cycle
	private bool _recentGesture;    // a long-press OR drag happened since the press began (see ConsumeRecentGesture)
	private float _held;
	private Vector2 _start;
	private ScrollContainer? _scroll; // nearest ScrollContainer ancestor (if any) — a drag pans it (touch list scroll)
	private bool _scrollResolved;   // _scroll is found lazily on first drag (see ResolveScroll)

	public static PointerIntent Attach(Control target)
	{
		var pi = new PointerIntent { Name = "PointerIntent" };
		target.AddChild(pi);
		pi.SetProcess(false); // only tick while a press is being held
		target.GuiInput += pi.OnGuiInput;
		return pi;
	}

	/// <summary>True (once) if a long-press OR a drag happened during the press that is now releasing. Lets a
	/// Button's Pressed handler skip the release that follows a long-press (inspect) or a drag (scroll), so
	/// neither also fires the tap action. Always false on desktop (no PointerIntent is attached there).</summary>
	public bool ConsumeRecentGesture()
	{
		bool v = _recentGesture;
		_recentGesture = false;
		return v;
	}

	// The nearest ScrollContainer ancestor, found lazily on first drag. Can't be found in Attach() — the target
	// is usually built and hooked BEFORE it's added under the ScrollContainer, so its ancestors aren't reachable
	// yet. By the time a drag happens it's in the tree, so the walk succeeds.
	private ScrollContainer? ResolveScroll()
	{
		if (_scrollResolved) return _scroll;
		_scrollResolved = true;
		for (Node? n = GetParent(); n is not null; n = n.GetParent())
			if (n is ScrollContainer sc) { _scroll = sc; break; }
		return _scroll;
	}

	private void OnGuiInput(InputEvent e)
	{
		switch (e)
		{
			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
				_down = true; _fired = false; _moved = false; _recentGesture = false;
				_held = 0f; _start = mb.Position;
				SetProcess(true);
				break;

			case InputEventMouseMotion mm when _down && !_fired:
				if (!_moved && _start.DistanceTo(mm.Position) > MoveCancelPixels)
				{
					_moved = true;
					_recentGesture = true; // a drag suppresses the tap the button would otherwise fire on release
					SetProcess(false);     // a drag cancels the pending long-press
					EmitSignal(SignalName.Dragged);
				}
				if (_moved && ResolveScroll() is { } sc && sc.VerticalScrollMode != ScrollContainer.ScrollMode.Disabled)
					sc.ScrollVertical -= (int)mm.Relative.Y; // pan the list with the finger (touch drag-scroll over content)
				break;

			case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _down:
				if (_fired) EmitSignal(SignalName.LongPressReleased);
				else if (!_moved) EmitSignal(SignalName.Tapped);
				_down = false; _fired = false; _moved = false; _held = 0f;
				SetProcess(false);
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (!_down || _fired || _moved) { SetProcess(false); return; }
		_held += (float)delta;
		if (_held >= LongPressSeconds)
		{
			_fired = true;
			_recentGesture = true;
			EmitSignal(SignalName.LongPressed);
		}
	}

	// ---------- call-site helpers ----------

	/// <summary>Preview bracketed by show/hide: mouse hover on desktop, touch long-press on mobile. Returns
	/// the PointerIntent on touch (so a Button caller can guard its tap action via ConsumeRecentLongPress),
	/// or null on desktop.</summary>
	public static PointerIntent? HookPreview(Control target, Action show, Action hide)
	{
		if (DisplayServer.IsTouchscreenAvailable())
		{
			var pi = Attach(target);
			pi.LongPressed += () => show();
			pi.LongPressReleased += () => hide();
			return pi;
		}
		target.MouseEntered += () => show();
		target.MouseExited += () => hide();
		return null;
	}

	/// <summary>Inspect action: right-click on desktop, touch long-press on mobile. Returns the PointerIntent
	/// on touch (to guard a tap action), or null on desktop.</summary>
	public static PointerIntent? HookInspect(Control target, Action inspect)
	{
		if (DisplayServer.IsTouchscreenAvailable())
		{
			var pi = Attach(target);
			pi.LongPressed += () => inspect();
			return pi;
		}
		target.GuiInput += e =>
		{
			if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
				inspect();
		};
		return null;
	}
}
