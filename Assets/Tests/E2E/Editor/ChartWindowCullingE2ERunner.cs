using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class ChartWindowCullingE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    const string IID = "7203.TSE";
    static readonly Vector2 size = new Vector2(520f, 360f);

    [MenuItem("E2E/Chart Window Culling")]
    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_PureVisibility()
                ?? Section2_RootCanvasToggle();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART WINDOW CULLING PASS] issue #186: off-viewport chart body Canvas disables and re-enables on return.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART WINDOW CULLING FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_PureVisibility()
    {
        var view = CanvasView.Identity();
        var viewport = new Vector2(800f, 600f);
        if (!BackcastWorkspaceRoot.IsCanvasLogicalRectVisible(Vector2.zero, size, view, viewport))
            return "CULL-01: origin chart should be visible in identity view";
        if (BackcastWorkspaceRoot.IsCanvasLogicalRectVisible(new Vector2(10000f, 0f), size, view, viewport))
            return "CULL-01: far-right chart should be culled";
        if (!BackcastWorkspaceRoot.IsCanvasLogicalRectVisible(new Vector2(399f, 0f), size, view, viewport))
            return "CULL-01: edge-overlapping chart should still be visible";
        
        Debug.Log("[E2E CULL-01 PASS] pure logical rect visibility distinguishes visible / offscreen / edge overlap.");
        return null;
    }

    static string Section2_RootCanvasToggle()
    {
        var spawned = new List<GameObject>();
        try
        {
            var rootGo = new GameObject("culling_root", typeof(BackcastWorkspaceRoot));
            spawned.Add(rootGo);
            var root = rootGo.GetComponent<BackcastWorkspaceRoot>();
            
            Type ty = typeof(BackcastWorkspaceRoot);
            var viewportGo = NewRect("viewport", spawned);
            var viewport = (RectTransform)viewportGo.transform;
            viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 0.5f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.sizeDelta = new Vector2(800f, 600f);
            var contentGo = NewRect("content", spawned);
            var content = (RectTransform)contentGo.transform;
            content.SetParent(viewport, false);
            content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 0.5f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            var canvas = new InfiniteCanvasController(content);
            canvas.ApplyView(CanvasView.Identity());
            var layerGo = NewRect("dock_layer", spawned);
            var layer = (RectTransform)layerGo.transform;
            var catalog = FloatingWindowCatalog.Default();
            var dock = new FloatingWindowController(
                layer,
                catalog,
                (spec, id) =>
                {
                    var go = NewRect(id, spawned);
                    return (RectTransform)go.transform;
                },
                go => UnityEngine.Object.DestroyImmediate(go));
            
            RectTransform win = dock.Spawn(
                FloatingWindowCatalog.KIND_CHART,
                DockShape.ChartId(IID),
                0f, 0f, size.x, size.y,
                visible: true);
            if (win == null) return "CULL-02: failed to spawn chart window";
            
            SetField(ty, root, "_viewport", viewport);
            SetField(ty, root, "_canvas", canvas);
            SetField(ty, root, "_dockWindows", dock);
            
            var buildChart = ty.GetMethod("BuildChartContent", BF);
            var updateCull = ty.GetMethod("UpdateChartWindowCulling", BF);
            if (buildChart == null || updateCull == null)
                return "CULL-02: BuildChartContent / UpdateChartWindowCulling not found";
                
            buildChart.Invoke(root, new object[] { IID, win });
            
            Canvas bodyCanvas = win.GetComponent<Canvas>();
            if (bodyCanvas == null)
                return "CULL-02: chart body did not get a sub-Canvas";
                
            updateCull.Invoke(root, null);
            if (!bodyCanvas.enabled)
                return "CULL-02: visible chart Canvas was disabled at identity view";
                
            canvas.ApplyView(new CanvasView(10000f, 0f, 1f));
            updateCull.Invoke(root, null);
            if (bodyCanvas.enabled)
                return "CULL-02: offscreen chart Canvas stayed enabled";
                
            canvas.ApplyView(CanvasView.Identity());
            updateCull.Invoke(root, null);
            if (!bodyCanvas.enabled)
                return "CULL-02: chart Canvas did not re-enable after returning onscreen";
                
            Debug.Log("[E2E CULL-02 PASS] BackcastWorkspaceRoot toggles chart body Canvas offscreen -> disabled, onscreen -> enabled.");
            return null;
        }
        finally
        {
            for (int i = spawned.Count - 1; i >= 0; i--)
                if (spawned[i] != null) UnityEngine.Object.DestroyImmediate(spawned[i]);
        }
    }

    static GameObject NewRect(string name, List<GameObject> spawned)
    {
        var go = new GameObject(name, typeof(RectTransform));
        spawned.Add(go);
        return go;
    }

    static void SetField(Type ty, object target, string name, object value)
    {
        var f = ty.GetField(name, BF);
        if (f == null) throw new MissingFieldException(ty.Name, name);
        f.SetValue(target, value);
    }
}
