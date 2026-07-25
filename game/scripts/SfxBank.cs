using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Sound-effect bank for gameplay cues. Shipped OGG/WAV assets are loaded first; the
/// procedural clips remain as a safe fallback when an asset was omitted from a partial export or
/// could not be imported.  A small voice pool lets rapid events (a multi-hit turn) overlap instead
/// of cutting each other off.
/// </summary>
public sealed class SfxBank
{
    private const int MixRate = 22050;
    private const string AssetRoot = "res://assets/audio/sfx/";
    private readonly Dictionary<string, AudioStream> _sounds = new();
    private readonly AudioStreamPlayer[] _voices;
    private int _next;

    private enum Wave { Sine, Square, Triangle }

    public SfxBank(Node owner, int voices = 6)
    {
        _voices = new AudioStreamPlayer[voices];
        for (int i = 0; i < voices; i++)
        {
            _voices[i] = new AudioStreamPlayer { VolumeDb = -9f };
            owner.AddChild(_voices[i]);
        }

        // --- combat ---
        LoadOrFallback("attack", Tone(120f, 0.12f, 10f, Wave.Square, 0.5f, noise: 0.4f));   // melee impact
        LoadOrFallback("shoot", Tone(520f, 0.12f, 9f, Wave.Triangle, 0.4f, noise: 0.15f));   // ranged launch
        LoadOrFallback("cast", Tone(784f, 0.16f, 6f, Wave.Triangle, 0.42f));                  // command cast
        LoadOrFallback("molten_slam", Tone(74f, 0.42f, 6f, Wave.Square, 0.7f, noise: 0.35f)); // 熔岩巨剑
        LoadOrFallback("spell_ward", Tone(988f, 0.34f, 5f, Wave.Triangle, 0.42f, noise: 0.08f)); // 法术护体
        LoadOrFallback("phoenix_rebirth", Tone(659f, 0.85f, 2f, Wave.Triangle, 0.5f, noise: 0.12f)); // 浴火重生
        LoadOrFallback("module_install", Tone(392f, 0.42f, 7f, Wave.Triangle, 0.34f, noise: 0.06f)); // 炮台装配,柔和棘轮
        LoadOrFallback("turret_fire", Tone(142f, 0.24f, 11f, Wave.Sine, 0.48f, noise: 0.1f)); // 轻炮
        LoadOrFallback("turret_fire_heavy", Tone(82f, 0.40f, 7f, Wave.Sine, 0.62f, noise: 0.12f)); // 贯日主炮
        // --- leader signatures: four deliberately different silhouettes, paired with the on-screen quote ---
        LoadOrFallback("leader_shield", Tone(196f, 0.46f, 4f, Wave.Triangle, 0.58f, noise: 0.05f)); // steel oath
        LoadOrFallback("leader_horn", Phrase(new[] { (196f, 0.00f, 0.42f), (294f, 0.10f, 0.58f) }, 0.48f)); // hunting call
        LoadOrFallback("leader_ember", Tone(92f, 0.62f, 3f, Wave.Sine, 0.55f, noise: 0.18f)); // ember bloom
        LoadOrFallback("leader_forge", Phrase(new[] { (110f, 0.00f, 0.22f), (165f, 0.18f, 0.22f), (220f, 0.36f, 0.36f) }, 0.44f)); // rivet cadence
        LoadOrFallback("death", Tone(90f, 0.22f, 7f, Wave.Square, 0.5f, noise: 0.55f));       // unit death
        LoadOrFallback("leaderhit", Tone(60f, 0.30f, 4f, Wave.Sine, 0.85f, noise: 0.15f));    // leader damage
        // --- flow / feedback ---
        LoadOrFallback("play", Tone(196f, 0.10f, 8f, Wave.Triangle, 0.5f));                   // deploy
        LoadOrFallback("move", Tone(392f, 0.05f, 14f, Wave.Sine, 0.35f));                     // move
        LoadOrFallback("draw", Tone(1180f, 0.045f, 20f, Wave.Triangle, 0.28f, noise: 0.3f));  // draw
        LoadOrFallback("turnstart", Tone(262f, 0.18f, 6f, Wave.Triangle, 0.45f));              // turn banner
        LoadOrFallback("tide", Tone(55f, 0.5f, 3f, Wave.Sine, 0.6f, noise: 0.1f));             // pressure tide
        LoadOrFallback("button", Tone(330f, 0.03f, 26f, Wave.Square, 0.3f));                   // UI tick
        // --- win / lose stings (short arpeggio phrases, ~2s) ---
        LoadOrFallback("victory", Phrase(new[]                                        // ascending major, resolves high
        {
            (523f, 0.00f, 0.45f), (659f, 0.16f, 0.45f), (784f, 0.32f, 0.55f), (1047f, 0.52f, 1.25f),
        }, 0.5f));
        LoadOrFallback("defeat", Phrase(new[]                                         // descending minor, settles low
        {
            (440f, 0.00f, 0.55f), (349f, 0.34f, 0.65f), (262f, 0.72f, 1.5f),
        }, 0.55f));
    }

    public void Play(string name)
    {
        if (!_sounds.TryGetValue(name, out var wav))
            return;
        var voice = _voices[_next];
        _next = (_next + 1) % _voices.Length;
        voice.Stream = wav;
        voice.Play();
    }

    private void LoadOrFallback(string name, AudioStreamWav fallback)
    {
        foreach (string ext in new[] { ".ogg", ".wav" })
        {
            string path = AssetRoot + name + ext;
            if (ResourceLoader.Exists(path))
            {
                var stream = ResourceLoader.Load<AudioStream>(path);
                if (stream != null)
                {
                    _sounds[name] = stream;
                    return;
                }
            }
        }

        _sounds[name] = fallback;
    }

    private static AudioStreamWav Tone(float freq, float dur, float decay, Wave wave, float amp, float noise = 0f)
    {
        int samples = (int)(dur * MixRate);
        var data = new byte[samples * 2];
        uint rng = 0x1234567u;

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / MixRate;
            float phase = freq * t;
            float w = wave switch
            {
                Wave.Square => Mathf.Sin(phase * Mathf.Tau) >= 0f ? 1f : -1f,
                Wave.Triangle => 2f * Mathf.Abs(2f * (phase - Mathf.Floor(phase + 0.5f))) - 1f,
                _ => Mathf.Sin(phase * Mathf.Tau),
            };
            if (noise > 0f)
            {
                rng = rng * 1664525u + 1013904223u;
                float n = ((rng >> 8) & 0xFFFF) / 32768f - 1f;
                w = Mathf.Lerp(w, n, noise);
            }
            float env = Mathf.Exp(-decay * t);
            short s = (short)(Mathf.Clamp(w * env * amp, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return Wav(data);
    }

    /// <summary>Sum several decaying triangle notes into one buffer — a tiny arpeggio for the win/lose stings.</summary>
    private static AudioStreamWav Phrase((float Freq, float Start, float Dur)[] notes, float amp)
    {
        float total = 0f;
        foreach (var n in notes)
            total = Mathf.Max(total, n.Start + n.Dur);
        int samples = (int)(total * MixRate);
        var buf = new float[samples];

        foreach (var (freq, start, dur) in notes)
        {
            int s0 = (int)(start * MixRate);
            int sn = (int)(dur * MixRate);
            for (int i = 0; i < sn && s0 + i < samples; i++)
            {
                float t = (float)i / MixRate;
                float phase = freq * t;
                float w = 2f * Mathf.Abs(2f * (phase - Mathf.Floor(phase + 0.5f))) - 1f; // triangle
                float atk = Mathf.Min(1f, t / 0.02f);   // quick attack ramp
                buf[s0 + i] += w * atk * Mathf.Exp(-3.0f * t);
            }
        }

        var data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(Mathf.Clamp(buf[i] * amp, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return Wav(data);
    }

    private static AudioStreamWav Wav(byte[] data) => new()
    {
        Format = AudioStreamWav.FormatEnum.Format16Bits,
        MixRate = MixRate,
        Stereo = false,
        Data = data,
    };
}
