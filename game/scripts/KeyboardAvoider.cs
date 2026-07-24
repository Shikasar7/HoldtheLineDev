using Godot;

// docs/19 A3.1 — the Android soft keyboard covers low input fields (and on this device it's ~70% of the
// screen, so shifting the whole login window can't fit a low field above it). Instead, while a LineEdit is
// focused AND the keyboard is actually up, lift just that field to the top of the screen so it's visible
// above the keyboard; drop it back the moment the keyboard hides.
//
// Lift/restore is driven by the real keyboard visibility (VirtualKeyboardGetHeight), not just focus events:
// hiding the keyboard (its collapse button / back) keeps the field focused, so a focus-only restore would
// leave the field stuck at the top. Touch-only (desktop has no virtual keyboard — Watch is a no-op there).
public partial class KeyboardAvoider : Node
{
	private const float SafeTopY = 120f; // design-space Y to park the focused field at (clear of the keyboard)

	private LineEdit _field = null!;
	private Vector2 _restPos;
	private bool _lifted;
	private bool _focused;

	/// <summary>Keep this field visible above the soft keyboard on touch devices. No-op on desktop.</summary>
	public static void Watch(LineEdit field)
	{
		if (!DisplayServer.IsTouchscreenAvailable()) return;
		var ka = new KeyboardAvoider { _field = field };
		field.AddChild(ka);
		ka.SetProcess(false);
		field.FocusEntered += ka.OnFocus;
		field.FocusExited += ka.OnBlur;
	}

	private void OnFocus() { _focused = true; SetProcess(true); }

	private void OnBlur() { _focused = false; SetProcess(false); Restore(); }

	public override void _Process(double delta)
	{
		if (!_focused || !GodotObject.IsInstanceValid(_field)) { SetProcess(false); Restore(); return; }
		bool keyboardUp = DisplayServer.VirtualKeyboardGetHeight() > 0;
		if (keyboardUp && !_lifted) Lift();
		else if (!keyboardUp && _lifted) Restore();
	}

	private void Lift()
	{
		_restPos = _field.GlobalPosition; // capture the resting spot (only called while at rest)
		if (_restPos.Y <= SafeTopY) return; // already high enough (e.g. the deck-name field at the top)
		_field.GlobalPosition = new Vector2(_restPos.X, SafeTopY);
		_lifted = true;
	}

	private void Restore()
	{
		if (!_lifted) return;
		_lifted = false;
		if (GodotObject.IsInstanceValid(_field))
			_field.GlobalPosition = _restPos;
	}
}
