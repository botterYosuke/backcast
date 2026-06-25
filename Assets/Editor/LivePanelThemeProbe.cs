// LivePanelThemeProbe.cs — ADR-0028 (THROWAWAY AFK gate: live panel body re-themes on appearance switch)
//
// Headless, Python-FREE gate for the review #1 fix: LivePanelTileView (the 4 base dock panels —
// orders / positions / run_result / buying_power) bakes hakoniwa_text at Build and, being a plain class,
// can't self-subscribe like ChartView/DepthLadderView. So a LIVE Dark→Light switch would leave its body
// text starlight-white on the now-white Light card (invisible) unless the owner re-applies. This proves
// LivePanelTileView.ApplyTheme() re-reads the active hakoniwa_text (the owner wires it from
// BackcastWorkspaceRoot.ApplyViewportTheme on ThemeService.Changed; that wiring is HITL/read-verified). Run:
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod LivePanelThemeProbe.Run -logFile <log>
//   # expect: [LIVEPANEL-THEME PASS] ... / exit=0

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class LivePanelThemeProbe
{
    static readonly List<GameObject> _spawned = new List<GameObject>();

    public static void Run()
    {
        ProbeAssert.Reset();
        try
        {
            ThemeService.ResetForTests();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var bodyGo = new GameObject("body", typeof(RectTransform));
            _spawned.Add(bodyGo);
            var body = (RectTransform)bodyGo.transform;

            var view = new LivePanelTileView(_ => "");
            view.Build(body, font);

            var text = (body.Find("content") as RectTransform)?.GetComponent<Text>();
            ProbeAssert.True(text != null, "content Text built into the panel body");

            // built dark (default): body text == dark hakoniwa_text (starlight).
            ProbeAssert.Eq(text.color, Theme.Dark().colors.hakoniwa_text, "dark: body text == dark hakoniwa_text");

            // live switch to Light + ApplyTheme: body text re-reads the LIGHT hakoniwa_text (ink), NOT stale.
            ThemeService.SetTheme(Theme.Light());
            view.ApplyTheme();
            ProbeAssert.Eq(text.color, Theme.Light().colors.hakoniwa_text, "light: body text re-themed to light hakoniwa_text (ink)");

            // non-vacuity: the two must actually differ, else the re-theme is a no-op and invisible-text bug stands.
            ProbeAssert.Ne(Theme.Light().colors.hakoniwa_text, Theme.Dark().colors.hakoniwa_text,
               "non-vacuity: light hakoniwa_text != dark (else nothing changed)");

            // and back: Dark → ApplyTheme restores the starlight value.
            ThemeService.SetTheme(Theme.Dark());
            view.ApplyTheme();
            ProbeAssert.Eq(text.color, Theme.Dark().colors.hakoniwa_text, "back-to-dark: body text re-themed to dark hakoniwa_text");
        }
        catch (Exception e)
        {
            ProbeAssert.Fails.Add("EXCEPTION: " + e);
        }
        finally
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
            ThemeService.ResetForTests();
        }

        if (ProbeAssert.Fails.Count == 0)
        {
            Debug.Log("[LIVEPANEL-THEME PASS] live panel body re-themes on appearance switch all green");
        }
        else
        {
            foreach (var f in ProbeAssert.Fails) Debug.LogError("[LIVEPANEL-THEME FAIL] " + f);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
