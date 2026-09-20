namespace GraveyardKeeperAccessibility;

/// <summary>
/// A tiny procedural sound engine for the fishing reel mini-game — the one place in this mod where
/// speech is the wrong tool.
///
/// Reeling a fish in is a continuous tracking task: a fish drifts up and down a bar several times a
/// second and the player has to keep a moving window over it. Text-to-speech cannot describe that
/// fast enough to act on (and it would mask itself), so the position is carried by a TONE instead,
/// exactly the way stardew-access sonifies Stardew Valley's bobber bar: pitch says where the fish is
/// relative to your bar, and short blips mark the events (progress milestones, hitting the top or
/// bottom, the bite itself).
///
/// The game's own audio cannot be reused for this — Sounds.PlaySound goes through MasterAudio, which
/// de-duplicates a sound to once per frame and gives no pitch control — so we synthesise our own:
/// one looping sine clip whose AudioSource.pitch is set every frame, and one short enveloped sine
/// played as a one-shot at whatever pitch an event calls for. Two clips, built once on first use.
///
/// Volume follows the game's MASTER volume slider but deliberately NOT the effects slider: these are
/// accessibility cues, not game sound effects, and a player who turns effects down to hear their
/// screen reader over the world ambience still needs to hear the fish.
///
/// Everything is wrapped so that a failure to create the audio objects disables the sonar for good
/// (_broken) instead of throwing once per frame inside the fishing UI.
/// </summary>
internal static class FishingSonar
{
    private const int SampleRate = 44100;

    // The carrier. Low enough to leave most of the blips clearly above it, high enough to stay
    // audible over the looping reel sound the game plays throughout the pull.
    private const float ToneHz = 330f;

    // Pulse rate of the "you are not on the fish" tremolo, in Hz.
    private const float TremoloHz = 9f;

    // Base loudness before the master-volume scaling. The tone sits under the blips on purpose: it
    // plays continuously, they do not.
    private const float ToneGain = 0.35f;
    private const float BlipGain = 0.55f;

    // Fade applied when the tone starts and stops, in seconds. Cutting a sine off mid-cycle clicks.
    private const float FadeTime = 0.05f;

    private static ManualLogSource _log;
    private static bool _broken;

    private static GameObject _host;
    private static AudioSource _tone;
    private static AudioSource _blip;

    private static float _tremoloPhase;
    private static float _fade;        // 0 = silent, 1 = full; ramped by Tick
    private static bool _wantTone;     // SetTone called this frame => fade towards 1, else towards 0
    private static float _targetPitch = 1f;
    private static bool _steady = true;

    // A one-slot delay line, so an event can ask for a second blip a moment after the first
    // ("ding-ding" for the bite, a double tick for the danger warning) without needing a coroutine.
    private static float _pendingHz;
    private static float _pendingAt;
    private static float _pendingVol;

    internal static void Init(ManualLogSource log) => _log = log;

    // ── Public API ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the carrier for this frame. <paramref name="semitones"/> is the offset from the base
    /// pitch (positive = fish above your bar); <paramref name="steady"/> plays it unbroken, which is
    /// what "the fish is inside the bar, you are scoring" sounds like — anything else pulses.
    /// Call every frame while the reel game is running; stop calling it and the tone fades out.
    /// </summary>
    internal static void SetTone(float semitones, bool steady)
    {
        if (!Ensure()) return;
        _wantTone = true;
        _steady = steady;
        _targetPitch = Mathf.Clamp(Mathf.Pow(2f, semitones / 12f), 0.25f, 3f);
    }

    /// <summary>Fades the carrier out (over the next few Tick calls) and drops any pending blip.</summary>
    internal static void Release()
    {
        _wantTone = false;
        _pendingHz = 0f;
    }

    /// <summary>Immediate silence — for leaving the fishing UI altogether, where no more ticks come.</summary>
    internal static void StopNow()
    {
        _wantTone = false;
        _pendingHz = 0f;
        _fade = 0f;
        try
        {
            if (_tone != null) { _tone.volume = 0f; _tone.Stop(); }
        }
        catch { }
    }

    /// <summary>A short enveloped sine at <paramref name="hz"/>. Low pitches ring longer, which is
    /// what makes the 130 Hz "you are pinned at the bottom" bump read as a thud and the 1320 Hz one
    /// as a tick. Pitch is capped at 3x the clip, so 1320 Hz is as high as a blip goes.</summary>
    internal static void Blip(float hz, float volume = 1f)
    {
        if (!Ensure()) return;
        try
        {
            _blip.pitch = Mathf.Clamp(hz / 440f, 0.25f, 3f);
            _blip.PlayOneShot(_blip.clip, Mathf.Clamp01(volume * BlipGain * MasterVolume()));
        }
        catch (Exception ex) { Fail("blip", ex); }
    }

    /// <summary>Queues a second blip <paramref name="delay"/> seconds from now (one slot only).</summary>
    internal static void BlipAfter(float delay, float hz, float volume = 1f)
    {
        _pendingHz = hz;
        _pendingVol = volume;
        _pendingAt = Time.unscaledTime + delay;
    }

    /// <summary>
    /// Drives the fades and the delayed blip. Called every frame the fishing UI is open — including
    /// the frames after the reel game ends, which is what lets the tone fade instead of clicking off.
    /// </summary>
    internal static void Tick()
    {
        if (_broken || _tone == null) return;
        try
        {
            float dt = Time.unscaledDeltaTime;

            if (_pendingHz > 0f && Time.unscaledTime >= _pendingAt)
            {
                float hz = _pendingHz;
                _pendingHz = 0f;
                Blip(hz, _pendingVol);
            }

            float step = (FadeTime > 0f) ? dt / FadeTime : 1f;
            _fade = Mathf.Clamp01(_fade + (_wantTone ? step : -step));

            if (_fade <= 0f)
            {
                if (_tone.isPlaying) { _tone.volume = 0f; _tone.Stop(); }
                _wantTone = false;
                return;
            }

            if (!_tone.isPlaying)
            {
                _tremoloPhase = Mathf.PI * 1.5f;   // start a pulse at its quiet point, not mid-beat
                _tone.Play();
            }

            // Out of the bar the tone pulses; inside it runs unbroken. That contrast is the single
            // most important cue in the whole mini-game — it says "you are scoring right now" without
            // the player having to judge a pitch at all.
            float amp = 1f;
            if (!_steady)
            {
                _tremoloPhase += dt * TremoloHz * 2f * Mathf.PI;
                if (_tremoloPhase > Mathf.PI * 2f) _tremoloPhase -= Mathf.PI * 2f;
                amp = 0.25f + 0.55f * (0.5f + 0.5f * Mathf.Sin(_tremoloPhase));
            }

            _tone.pitch = _targetPitch;
            _tone.volume = Mathf.Clamp01(ToneGain * amp * _fade * MasterVolume());

            // One frame of "wanted" at a time: whoever drives the reel game has to keep asking.
            _wantTone = false;
        }
        catch (Exception ex) { Fail("tick", ex); }
    }

    // ── Setup ───────────────────────────────────────────────────────────────────────────────────

    private static bool Ensure()
    {
        if (_broken) return false;
        if (_tone != null && _blip != null) return true;
        try
        {
            _host = new GameObject("GK_AccessibilityFishingSonar");
            _host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_host);

            _tone = _host.AddComponent<AudioSource>();
            Configure(_tone);
            _tone.clip = MakeSine("gk_a11y_tone", ToneHz, 1f, envelope: false);
            _tone.loop = true;

            _blip = _host.AddComponent<AudioSource>();
            Configure(_blip);
            // Configure leaves volume at 0 so the LOOPING tone cannot blast at full level on its
            // first Play, in the frame before Tick assigns it one. The blip source must not keep
            // that, and this line is the whole reason the cues were inaudible: PlayOneShot's
            // volumeScale is MULTIPLIED by AudioSource.volume, so a source sitting at 0 plays every
            // one-shot at absolute silence. The tone was fine because Tick writes its volume each
            // frame; the bite ding, the progress blips, the bottom thud and the danger tick were
            // all being synthesised, pitched and played into nothing. Blip() passes the real level
            // as the volumeScale, so this stays at unity gain.
            _blip.volume = 1f;
            // 440 Hz reference: Blip() pitches this clip to whatever it needs, so its length scales
            // with it too (a 1320 Hz tick lasts 40 ms, a 130 Hz thud 400 ms).
            _blip.clip = MakeSine("gk_a11y_blip", 440f, 0.12f, envelope: true);

            _log?.LogInfo("[FISHING] Sonar audio ready (procedural tone + blips)");
            return true;
        }
        catch (Exception ex)
        {
            Fail("setup", ex);
            return false;
        }
    }

    private static void Configure(AudioSource src)
    {
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f;          // 2D: this is an interface cue, not a thing in the world
        src.bypassEffects = true;
        src.bypassListenerEffects = true;
        src.bypassReverbZones = true;
        // We do our own master-volume scaling (see MasterVolume) rather than riding whatever the
        // game's audio engine has done to the listener, so the cues cannot be silenced by a filter
        // the mod does not know about.
        src.ignoreListenerVolume = true;
        src.volume = 0f;
        src.priority = 0;
    }

    /// <summary>
    /// A sine clip holding a whole number of cycles, so looping it is seamless. With
    /// <paramref name="envelope"/> the amplitude follows a half-sine over the clip, which removes the
    /// click a one-shot would otherwise start and end with.
    /// </summary>
    private static AudioClip MakeSine(string name, float hz, float seconds, bool envelope)
    {
        int cycles = Mathf.Max(1, Mathf.RoundToInt(hz * seconds));
        int samples = Mathf.Max(2, Mathf.RoundToInt(SampleRate * cycles / hz));
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;                        // 0..1 across the clip
            float v = Mathf.Sin(2f * Mathf.PI * cycles * t);
            if (envelope) v *= Mathf.Sin(Mathf.PI * t);
            data[i] = v;
        }
        var clip = AudioClip.Create(name, samples, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // The game's master slider, 0-100. Deliberately not multiplied by volume_sfx: see the class
    // summary. A muted game still means silence, which is the one setting a player cannot mistake.
    private static float MasterVolume()
    {
        try { return Mathf.Clamp01(GameSettings.me.volume_master / 100f); }
        catch { return 1f; }
    }

    private static void Fail(string where, Exception ex)
    {
        _broken = true;
        _log?.LogWarning($"[FISHING] Sonar disabled after {where} error: {ex.Message}");
        try { if (_tone != null) _tone.Stop(); } catch { }
    }
}
