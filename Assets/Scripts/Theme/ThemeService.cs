// ThemeService.cs — issue #44 "theme（配色）システム"
//
// The single app-global theme owner — the Unity analogue of TTWR's Bevy `Theme` Resource +
// `ThemePlugin` (findings 0018):
//   * Current  : lazy-default dark (= init_resource::<Theme>() + Default for Theme = dark()).
//                Every surface reads colors from here.
//   * SetTheme : swaps the active theme and fires Changed.
//   * Changed  : backcast-ORIGINAL propagation seam (TTWR has no swap; it rides Bevy's
//                per-frame change-detection, which Unity lacks for already-built UI). Each
//                themed surface subscribes and re-applies via its ApplyTheme().
//
// State lives in a static, which Unity's per-Play domain reload resets — so HITL/AFK Play
// sessions stay isolated, matching TTWR's "fresh world per test".

using System;

public static class ThemeService
{
    static Theme _current;

    public static Theme Current => _current ?? (_current = Theme.Dark());

    public static event Action Changed;

    public static void SetTheme(Theme theme)
    {
        _current = theme ?? throw new ArgumentNullException(nameof(theme));
        Changed?.Invoke();
    }

    // Test/probe hook: restore the pristine lazy-dark default and drop subscribers so probe
    // sections don't leak global state into each other (domain reload does this between Plays,
    // but a single batchmode probe process needs an explicit reset).
    public static void ResetForTests()
    {
        _current = null;
        Changed = null;
    }
}
