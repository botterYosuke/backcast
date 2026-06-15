// BackcastDefaultScene.cs — issue #59 "Backcast workspace root" (default Play scene)
//
// Makes BackcastWorkspace.unity the DEFAULT scene on two of Unity's "default scene" surfaces:
//   * build:  EditorBuildSettings.scenes[0] (set by BackcastWorkspaceSceneBuilder).
//   * Play:   EditorSceneManager.playModeStartScene — pressing Play from ANY open scene enters
//             BackcastWorkspace, so ADR-0009's "single normal Play entry" holds in the Editor too.
//
// playModeStartScene is an editor session value (it resets to null on Editor restart / domain
// reload), so [InitializeOnLoad] re-asserts it every load to make the default persistent. No-op if
// the scene asset doesn't exist yet (run Tools > Backcast > Build Workspace Scene first).
//
// To run a per-part Python HITL solo, disable the BackcastWorkspaceRoot GameObject in the scene
// before Play (single Play-owner, findings 0025 §7) — Play still starts from BackcastWorkspace.

using UnityEditor;
using UnityEditor.SceneManagement;

[InitializeOnLoad]
public static class BackcastDefaultScene
{
    static BackcastDefaultScene()
    {
        // Defer: the AssetDatabase may not be queryable during the static ctor at startup.
        EditorApplication.delayCall += Apply;
    }

    public static void Apply()
    {
        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(BackcastWorkspaceSceneBuilder.ScenePath);
        if (sceneAsset == null) return;                       // not built yet → leave Play default alone
        if (EditorSceneManager.playModeStartScene != sceneAsset)
            EditorSceneManager.playModeStartScene = sceneAsset;
    }
}
