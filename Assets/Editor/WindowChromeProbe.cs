// WindowChromeProbe.cs — ADR-0028 (THROWAWAY AFK gate: appearance-aware window chrome switch)
//
// Headless, Python-FREE gate proving WindowChrome.Apply swaps the floating-window chrome STRUCTURE on an
// appearance switch (dark HUD brackets ⇔ light Miro card), non-vacuously, re-paints the card surface, AND
// does NOT churn (a same-appearance re-apply keeps the existing chrome GameObjects — review #6). Run:
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod WindowChromeProbe.Run -logFile <log>
//   # expect: [WINDOWCHROME PASS] ... / exit=0
//
// OnEnable is play-mode-only, so the probe drives WindowChrome.Apply directly (mirrors ThemeProbe's
// "apply explicitly for the headless probe" discipline) rather than relying on the MonoBehaviour subscriber.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class WindowChromeProbe
{
    static readonly List<GameObject> _spawned = new List<GameObject>();

    const string HudChromeName = "HudChrome";

    public static void Run()
    {
        ProbeAssert.Reset();
        try
        {
            ThemeService.ResetForTests();
            var root = BuildFakeWindow();

            // -- dark (default): HUD chrome present, NOT rounded, no shadow, dark surface (dock case: null darkSurface) --
            WindowChrome.Apply(root, null);
            var img = root.GetComponent<Image>();
            ProbeAssert.True(Child(root, HudChromeName) != null, "dark: HUD chrome child present");
            ProbeAssert.True(img.sprite == null && img.type == Image.Type.Simple, "dark: root NOT rounded (flat panel)");
            ProbeAssert.True(root.GetComponent<Shadow>() == null, "dark: no drop shadow");
            ProbeAssert.Eq(img.color, Theme.Dark().colors.hakoniwa_panel_surface, "dark: surface == dark hakoniwa_panel_surface");

            // -- switch to light: HUD gone, rounded card + shadow, white surface --
            ThemeService.SetTheme(Theme.Light());
            WindowChrome.Apply(root, null);
            ProbeAssert.True(Child(root, HudChromeName) == null, "light: HUD chrome removed");
            ProbeAssert.True(img.sprite != null && img.type == Image.Type.Sliced, "light: root rounded (sliced sprite)");
            ProbeAssert.True(root.GetComponent<Shadow>() != null, "light: drop shadow present");
            ProbeAssert.Eq(img.color, Theme.Light().colors.hakoniwa_panel_surface, "light: surface == light hakoniwa_panel_surface (white)");
            var titleImg = (root.Find("TitleBar") as RectTransform)?.GetComponent<Image>();
            ProbeAssert.True(titleImg != null && titleImg.sprite != null, "light: title bar rounded too");
            ProbeAssert.Ne(Theme.Light().colors.hakoniwa_panel_surface, Theme.Dark().colors.hakoniwa_panel_surface,
               "non-vacuity: light surface != dark surface");

            // -- no-churn (review #6): a SECOND apply in the SAME appearance must REUSE the existing Shadow +
            // rounded sprite, not Destroy+recreate. Assert exactly one Shadow and a stable instance id. --
            var shadow1 = root.GetComponent<Shadow>();
            WindowChrome.Apply(root, null);   // same appearance (light) again
            var shadows = root.GetComponents<Shadow>();
            ProbeAssert.True(shadows.Length == 1, "no-churn: still exactly one Shadow after same-appearance re-apply");
            ProbeAssert.True(shadows.Length == 1 && shadows[0].GetInstanceID() == shadow1.GetInstanceID(),
               "no-churn: same-appearance re-apply REUSES the Shadow (no Destroy+recreate)");

            // -- switch back to dark: card reverted, HUD restored --
            ThemeService.SetTheme(Theme.Dark());
            WindowChrome.Apply(root, null);
            ProbeAssert.True(Child(root, HudChromeName) != null, "back-to-dark: HUD chrome restored");
            ProbeAssert.True(img.sprite == null && img.type == Image.Type.Simple, "back-to-dark: rounding removed");
            ProbeAssert.True(root.GetComponent<Shadow>() == null, "back-to-dark: shadow removed");
            ProbeAssert.True(titleImg != null && titleImg.sprite == null, "back-to-dark: title bar un-rounded");

            // -- no-churn in dark: the HudChrome child instance must survive a same-appearance re-apply. --
            var hud1 = Child(root, HudChromeName);
            WindowChrome.Apply(root, null);   // same appearance (dark) again
            var hud2 = Child(root, HudChromeName);
            ProbeAssert.True(hud1 != null && hud2 != null && hud1.GetInstanceID() == hud2.GetInstanceID(),
               "no-churn: same-appearance re-apply REUSES the HudChrome child (no Destroy+recreate)");

            // -- darkSurface preservation (editor/order case): a bespoke authored dark body must SURVIVE in
            // dark (not get clobbered to panel_surface) and go white in light (review F1). --
            ThemeService.ResetForTests();
            var w2 = BuildFakeWindow();
            var img2 = w2.GetComponent<Image>();
            var authored = new Color(0.0667f, 0.0863f, 0.1686f, 0.98f);   // #11162b @0.98 (order ticket body)
            WindowChrome.Apply(w2, authored);
            ProbeAssert.Eq(img2.color, authored, "dark: authored darkSurface preserved (NOT clobbered to panel_surface)");
            ThemeService.SetTheme(Theme.Light());
            WindowChrome.Apply(w2, authored);
            ProbeAssert.Eq(img2.color, Theme.Light().colors.hakoniwa_panel_surface, "light: authored window goes white card");
            ThemeService.SetTheme(Theme.Dark());
            WindowChrome.Apply(w2, authored);
            ProbeAssert.Eq(img2.color, authored, "back-to-dark: authored darkSurface restored (incl. 0.98 alpha)");

            // -- ELEVATED (FloatingWindowLayer) drop shadow: the strategy-editor / order-ticket windows ride
            // the 1.2× front plane and must read as FLOATING above the dock back plane. With elevated:true the
            // window wears a STRONGER drop shadow in BOTH appearances (dark HUD gets a shadow it otherwise lacks;
            // light card gets a deeper lift than the dock card's subtle shadow). Dock windows (elevated:false,
            // asserted above) keep the appearance-default shadow, so the front/back depth split is non-vacuous. --
            ThemeService.ResetForTests();   // back to dark
            var w3 = BuildFakeWindow();
            WindowChrome.Apply(w3, null, true);   // elevated, dark
            var sh3 = w3.GetComponent<Shadow>();
            ProbeAssert.True(sh3 != null, "elevated dark: drop shadow present (HUD otherwise has none)");
            ProbeAssert.True(sh3 != null && sh3.effectColor.a > 0.30f, "elevated dark: shadow is a strong float lift (alpha > 0.30)");
            ProbeAssert.True(sh3 != null && Mathf.Abs(sh3.effectDistance.y) > 3f, "elevated dark: shadow offset deeper than the dock card subtle (|y| > 3)");

            // no-churn: same-appearance re-apply REUSES the one Shadow (no Destroy+recreate).
            WindowChrome.Apply(w3, null, true);
            var shadows3 = w3.GetComponents<Shadow>();
            ProbeAssert.True(shadows3.Length == 1 && shadows3[0].GetInstanceID() == sh3.GetInstanceID(),
               "elevated dark no-churn: re-apply REUSES the single Shadow");

            // switch to light: still elevated, shadow SURVIVES the HUD→Card structural swap and stays strong.
            ThemeService.SetTheme(Theme.Light());
            WindowChrome.Apply(w3, null, true);
            var sh3L = w3.GetComponent<Shadow>();
            ProbeAssert.True(sh3L != null && sh3L.effectColor.a > 0.30f, "elevated light: strong float shadow survives switch (alpha > 0.30)");

            // back to dark must still carry the shadow (HUD→ no shadow only for the NON-elevated dock case).
            ThemeService.SetTheme(Theme.Dark());
            WindowChrome.Apply(w3, null, true);
            ProbeAssert.True(w3.GetComponent<Shadow>() != null, "elevated back-to-dark: float shadow retained (not stripped like dock)");
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
            Debug.Log("[WINDOWCHROME PASS] HUD ⇔ Card structural switch + surface re-paint + no-churn all green");
        }
        else
        {
            foreach (var f in ProbeAssert.Fails) Debug.LogError("[WINDOWCHROME FAIL] " + f);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // A minimal window root mirroring the *WindowFrame builders: a root Image + a "TitleBar" child Image.
    static RectTransform BuildFakeWindow()
    {
        var rootGo = new GameObject("probe_window", typeof(RectTransform), typeof(Image));
        _spawned.Add(rootGo);
        var root = (RectTransform)rootGo.transform;

        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image));
        var titleRt = (RectTransform)titleGo.transform;
        titleRt.SetParent(root, false);
        return root;
    }

    static RectTransform Child(RectTransform parent, string name) => parent.Find(name) as RectTransform;
}
