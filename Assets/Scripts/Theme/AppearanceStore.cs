// AppearanceStore.cs — ADR-0028 / findings 0108 D8 (appearance persistence)
//
// The app-GLOBAL persisted Dark/Light choice. Appearance is NOT per-document (a strategy/layout sidecar)
// — persisting it there would flip the theme every time a different strategy opens. So it rides a single
// PlayerPrefs key, written when the Settings Appearance segment is clicked and read at boot before the
// first theme apply (BackcastWorkspaceRoot.ApplyPersistedAppearance). Default = Dark (the shipped default).

using UnityEngine;

public static class AppearanceStore
{
    const string Key = "backcast.appearance";

    public static void Save(Appearance appearance)
    {
        PlayerPrefs.SetString(Key, appearance == Appearance.Light ? "light" : "dark");
        PlayerPrefs.Save();
    }

    // Default Dark when unset or any unexpected value (the shipped default — TTWR parity).
    public static Appearance Load() =>
        PlayerPrefs.GetString(Key, "dark") == "light" ? Appearance.Light : Appearance.Dark;

    // Test hook: drop the key so an AFK probe doesn't leave machine-global residue.
    public static void ClearForTests() => PlayerPrefs.DeleteKey(Key);
}
