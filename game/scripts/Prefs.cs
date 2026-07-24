using System;
using System.Text.Json;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Small persistent UI preferences (<c>user://prefs.json</c>) — the deck last taken into a match per
/// surface, plus the docs/16 login state. Mirrors <see cref="Identity"/>'s user-file pattern; loaded
/// lazily.
///
/// <para>Thread-safe: <see cref="Session"/> pushes a display-name update from the WebSocket receive
/// thread while the main thread persists deck/login prefs, so every read-modify-write runs under
/// <see cref="_gate"/> and a value-equal write is skipped (records compare by value) — no torn
/// prefs.json, no redundant disk churn on unchanged Profile pushes.</para>
/// </summary>
public static class Prefs
{
    private const string Path = "user://prefs.json";
    private static readonly object _gate = new();

    private sealed record Stored
    {
        public string LastLobbyDeck { get; init; } = "";   // server deck id or built-in id
        public string LastVsAiDeck { get; init; } = "";    // built-in id or "local:<StoredDeck.Id>"
        // 同屏对战: each seat's last-picked deck (built-in id or "local:<StoredDeck.Id>"), preselected next open.
        public string LastHotseatDeck0 { get; init; } = "";
        public string LastHotseatDeck1 { get; init; } = "";
        // docs/16 login flow:
        public bool Entered { get; init; }                 // has the player chosen an entry (guest/login/register)?
        public string Nickname { get; init; } = "";        // persistent display name; hello sends it, set_name changes it
        public string LastUsername { get; init; } = "";    // prefill the login field next time (cleared on logout)
        // 新手教学关 (docs/23):
        public bool WelcomeSeen { get; init; }             // has the first-launch welcome popup been shown once?
        public bool TutorialCompleted { get; init; }       // has the player finished the tutorial at least once?
    }

    private static Stored? _cached;

    // Callers hold _gate. Reads _cached, else the file (or its .bak after an interrupted save), else defaults.
    private static Stored Load()
    {
        if (_cached is { } c)
            return c;
        Stored? stored = null;
        if (UserFile.ReadBestText(Path) is { } json)
        {
            try { stored = JsonSerializer.Deserialize<Stored>(json); }
            catch { stored = null; }
        }
        _cached = stored ?? new Stored();
        return _cached;
    }

    // Callers hold _gate. Atomic write via .tmp+rename so a mid-save crash can't truncate prefs.json.
    private static void Save(Stored stored)
    {
        _cached = stored;
        UserFile.WriteAtomic(Path, JsonSerializer.Serialize(stored));
    }

    private static T Get<T>(Func<Stored, T> read)
    {
        lock (_gate) return read(Load());
    }

    // Atomic read-modify-write; skips the disk write when the record is unchanged (value equality).
    private static void Set(Func<Stored, Stored> update)
    {
        lock (_gate)
        {
            var cur = Load();
            var next = update(cur);
            if (next != cur) Save(next);
        }
    }

    public static string LastLobbyDeck
    {
        get => Get(s => s.LastLobbyDeck);
        set => Set(s => s with { LastLobbyDeck = value });
    }

    public static string LastVsAiDeck
    {
        get => Get(s => s.LastVsAiDeck);
        set => Set(s => s with { LastVsAiDeck = value });
    }

    public static string LastHotseatDeck0
    {
        get => Get(s => s.LastHotseatDeck0);
        set => Set(s => s with { LastHotseatDeck0 = value });
    }

    public static string LastHotseatDeck1
    {
        get => Get(s => s.LastHotseatDeck1);
        set => Set(s => s with { LastHotseatDeck1 = value });
    }

    /// <summary>docs/16: set once the player picks an entry on the login page; startup skips straight to the
    /// menu (silent auto-connect) when true. Logout resets it → the login page shows again.</summary>
    public static bool Entered
    {
        get => Get(s => s.Entered);
        set => Set(s => s with { Entered = value });
    }

    /// <summary>docs/16: the persistent display name (was re-typed every connect before). hello carries it;
    /// set_name updates it. Empty → the default "玩家".</summary>
    public static string Nickname
    {
        get => Get(s => s.Nickname);
        set => Set(s => s with { Nickname = value });
    }

    public static string LastUsername
    {
        get => Get(s => s.LastUsername);
        set => Set(s => s with { LastUsername = value });
    }

    /// <summary>docs/23: set true after the first-launch 欢迎 popup is dismissed (whichever button) so it
    /// only ever appears once. The tutorial itself is re-entered from the menu / 人机 panel afterwards.</summary>
    public static bool WelcomeSeen
    {
        get => Get(s => s.WelcomeSeen);
        set => Set(s => s with { WelcomeSeen = value });
    }

    /// <summary>docs/23: set true the first time the player finishes the 新手教学关. Currently informational
    /// (the tutorial stays replayable); reserved for a future "已完成教学" mark on the entry button.</summary>
    public static bool TutorialCompleted
    {
        get => Get(s => s.TutorialCompleted);
        set => Set(s => s with { TutorialCompleted = value });
    }
}
