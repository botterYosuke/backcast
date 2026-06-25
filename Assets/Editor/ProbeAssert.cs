// ProbeAssert.cs — ADR-0028 (shared AFK-probe assertion helpers)
//
// The Approx / Eq / Ne / True color-and-bool asserts every headless Editor probe needs, in ONE place so a
// new probe doesn't re-roll them (SettingsAppearanceProbe / WindowChromeProbe both used byte-identical copies
// of ThemeProbe's helpers — collapsed here). Each probe calls Reset() at the top of Run(), accumulates into
// Fails, then logs PASS when Fails is empty / FAILs otherwise. Single static list is fine: probes run in
// separate batchmode processes, and Reset() clears it at the start of each Run() even if chained in-process.

using System.Collections.Generic;
using UnityEngine;

public static class ProbeAssert
{
    public static readonly List<string> Fails = new List<string>();

    public static void Reset() => Fails.Clear();

    public static bool Approx(Color a, Color b) =>
        Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) &&
        Mathf.Approximately(a.b, b.b) && Mathf.Approximately(a.a, b.a);

    public static void Eq(Color got, Color want, string msg)
    {
        if (!Approx(got, want)) Fails.Add(msg + $" (got {got}, want {want})");
    }

    public static void Ne(Color got, Color other, string msg)
    {
        if (Approx(got, other)) Fails.Add(msg + $" (both {got})");
    }

    public static void True(bool cond, string msg)
    {
        if (!cond) Fails.Add(msg);
    }
}
