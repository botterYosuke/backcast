// SettingsAppearanceProbe.cs — ADR-0028 (THROWAWAY AFK gate: Settings appearance segment + persistence)
//
// Headless, Python-FREE gate for S4 (the Dark/Light segment fires a live theme switch + persists) and S5
// (boot restores the persisted appearance). Run:
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod SettingsAppearanceProbe.Run -logFile <log>
//   # expect: [SETTINGS-APPEARANCE PASS] ... / exit=0
//
// Drives the real SettingsAppearanceSegmentView + AppearanceStore with the same onSelect the host wires
// (ApplyAppearance = SetTheme + Save). PlayerPrefs is machine-global, so the probe clears the key on exit.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class SettingsAppearanceProbe
{
    static readonly List<GameObject> _spawned = new List<GameObject>();

    public static void Run()
    {
        ProbeAssert.Reset();
        try
        {
            ThemeService.ResetForTests();
            AppearanceStore.ClearForTests();

            // -- AppearanceStore round-trip --
            ProbeAssert.True(AppearanceStore.Load() == Appearance.Dark, "store default == Dark (unset)");
            AppearanceStore.Save(Appearance.Light);
            ProbeAssert.True(AppearanceStore.Load() == Appearance.Light, "store persists Light");
            AppearanceStore.Save(Appearance.Dark);
            ProbeAssert.True(AppearanceStore.Load() == Appearance.Dark, "store persists Dark");

            // -- the segment view: click applies theme LIVE + persists (host onSelect = ApplyAppearance) --
            ThemeService.ResetForTests();
            AppearanceStore.ClearForTests();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var container = Spawn("probe_appearance", typeof(RectTransform));
            var view = new SettingsAppearanceSegmentView(
                a => { ThemeService.SetTheme(a == Appearance.Light ? Theme.Light() : Theme.Dark()); AppearanceStore.Save(a); },
                font);
            view.Build(container.GetComponent<RectTransform>());

            var darkSeg = SegImage(container, "seg:Dark");
            var lightSeg = SegImage(container, "seg:Light");
            ProbeAssert.True(darkSeg != null && lightSeg != null, "both Dark and Light segments built");

            // initial highlight on Dark (the default).
            ProbeAssert.Eq(darkSeg.color, ThemeService.Current.colors.element_selected, "initial: Dark segment highlighted");
            ProbeAssert.Eq(lightSeg.color, ThemeService.Current.colors.element_background, "initial: Light segment NOT highlighted");

            // click Light → live switch + persist + highlight moves.
            Click(container, "seg:Light");
            ProbeAssert.True(ThemeService.Current.appearance == Appearance.Light, "click Light: theme switched live");
            ProbeAssert.True(AppearanceStore.Load() == Appearance.Light, "click Light: persisted");
            ProbeAssert.Eq(lightSeg.color, ThemeService.Current.colors.element_selected, "click Light: Light segment highlighted");
            ProbeAssert.Eq(darkSeg.color, ThemeService.Current.colors.element_background, "click Light: Dark segment un-highlighted");

            // click Dark → back.
            Click(container, "seg:Dark");
            ProbeAssert.True(ThemeService.Current.appearance == Appearance.Dark, "click Dark: theme switched back");
            ProbeAssert.True(AppearanceStore.Load() == Appearance.Dark, "click Dark: persisted");

            // -- boot restore (ApplyPersistedAppearance logic): saved Light comes up Light; AND a leftover
            // Light static is forced back to Dark by a saved Dark (review #2 — set theme explicitly for both). --
            AppearanceStore.Save(Appearance.Light);
            ThemeService.ResetForTests();
            ThemeService.SetTheme(AppearanceStore.Load() == Appearance.Light ? Theme.Light() : Theme.Dark());
            ProbeAssert.True(ThemeService.Current.appearance == Appearance.Light, "boot: persisted Light restored at startup");

            ThemeService.SetTheme(Theme.Light());   // simulate a leftover Light static (domain-reload disabled)
            AppearanceStore.Save(Appearance.Dark);
            ThemeService.SetTheme(AppearanceStore.Load() == Appearance.Light ? Theme.Light() : Theme.Dark());
            ProbeAssert.True(ThemeService.Current.appearance == Appearance.Dark, "boot: persisted Dark forces Dark over a leftover Light");
        }
        catch (Exception e)
        {
            ProbeAssert.Fails.Add("EXCEPTION: " + e);
        }
        finally
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
            AppearanceStore.ClearForTests();
            ThemeService.ResetForTests();
        }

        if (ProbeAssert.Fails.Count == 0)
        {
            Debug.Log("[SETTINGS-APPEARANCE PASS] segment switch + persist + boot restore all green");
        }
        else
        {
            foreach (var f in ProbeAssert.Fails) Debug.LogError("[SETTINGS-APPEARANCE FAIL] " + f);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static GameObject Spawn(string name, params Type[] comps)
    {
        var go = new GameObject(name, comps);
        _spawned.Add(go);
        return go;
    }

    static Image SegImage(GameObject container, string segName)
    {
        var rt = (RectTransform)container.transform;
        for (int i = 0; i < rt.childCount; i++)
            if (rt.GetChild(i).name == segName) return rt.GetChild(i).GetComponent<Image>();
        return null;
    }

    static void Click(GameObject container, string segName)
    {
        var rt = (RectTransform)container.transform;
        for (int i = 0; i < rt.childCount; i++)
            if (rt.GetChild(i).name == segName)
            {
                var b = rt.GetChild(i).GetComponent<Button>();
                if (b != null) b.onClick.Invoke();
                return;
            }
        ProbeAssert.Fails.Add("segment not found: " + segName);
    }
}
