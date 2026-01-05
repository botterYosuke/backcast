/* Copyright 2026 Marimo. All rights reserved. */

import { usePrevious } from "@dnd-kit/utilities";
import { TooltipProvider } from "@radix-ui/react-tooltip";
import { useAtomValue, useSetAtom } from "jotai";
import * as THREE from "three";
import React, { useEffect, useRef, useState } from "react";
import { Controls } from "@/components/editor/controls/Controls";
import { AppHeader } from "@/components/editor/header/app-header";
import { FilenameForm } from "@/components/editor/header/filename-form";
import { MultiCellActionToolbar } from "@/components/editor/navigation/multi-cell-action-toolbar";
import { cn } from "@/utils/cn";
import { Paths } from "@/utils/paths";
import { AppContainer } from "../components/editor/app-container";
import {
  useRunAllCells,
  useRunStaleCells,
} from "../components/editor/cell/useRunCells";
import { AddCellButtons, CellArray } from "../components/editor/renderers/cell-array";
import { Cells3DRenderer } from "../components/editor/renderers/cells-3d-renderer";
import { CellsRenderer } from "../components/editor/renderers/cells-renderer";
import { PackageAlert } from "../components/editor/package-alert";
import { StartupLogsAlert } from "../components/editor/alerts/startup-logs-alert";
import { StdinBlockingAlert } from "../components/editor/stdin-blocking-alert";
import { ConnectingAlert } from "../components/editor/alerts/connecting-alert";
import { NotebookBanner } from "../components/editor/notebook-banner";
import { useHotkey } from "../hooks/useHotkey";
import {
  cellIdsAtom,
  columnIdsAtom,
  hasCellsAtom,
  notebookIsRunningAtom,
  numColumnsAtom,
  useCellActions,
} from "./cells/cells";
import { CellEffects } from "./cells/effects";
import type { AppConfig, UserConfig } from "./config/config-schema";
import { RuntimeState } from "./kernel/RuntimeState";
import { getSessionId } from "./kernel/session";
import { useTogglePresenting } from "./layout/useTogglePresenting";
import { is3DModeAtom, viewStateAtom } from "./mode";
import {
  cell3DViewAtom,
} from "./three/cell-3d-view";
import { useRequestClient } from "./network/requests";
import { useFilename } from "./saving/filename";
import { lastSavedNotebookAtom } from "./saving/state";
import { useJotaiEffect } from "./state/jotai";
import { CellCSS2DService } from "./three/cell-css2d-service";
import { SceneManager } from "./three/scene-manager";
import { useMarimoKernelConnection } from "./websocket/useMarimoKernelConnection";
import { flattenTopLevelNotebookCells, useNotebook } from "./cells/cells";
import { useLayoutState, useLayoutActions } from "./layout/layout";
import { GridLayoutPlugin } from "../components/editor/renderers/grid-layout/plugin";
import type { GridLayout } from "../components/editor/renderers/grid-layout/types";
import { Grid3DRenderer } from "../components/editor/renderers/grid-3d-renderer";
import { Grid3DControls } from "../components/editor/renderers/3d-layout/grid-3d-controls";
import { grid3DConfigAtom } from "./three/grid-3d-config";
import { convertGrid3DConfigToLayout } from "../components/editor/renderers/3d-layout/utils";

interface AppProps {
  /**
   * The user config.
   */
  userConfig: UserConfig;
  /**
   * The app config.
   */
  appConfig: AppConfig;
  /**
   * If true, the floating controls will be hidden.
   */
  hideControls?: boolean;
}

export const EditApp: React.FC<AppProps> = ({
  userConfig,
  appConfig,
  hideControls = false,
}) => {
  useJotaiEffect(cellIdsAtom, CellEffects.onCellIdsChange);

  const { setCells, mergeAllColumns, collapseAllCells, expandAllCells } =
    useCellActions();
  const viewState = useAtomValue(viewStateAtom);
  const numColumns = useAtomValue(numColumnsAtom);
  const hasCells = useAtomValue(hasCellsAtom);
  const columnIds = useAtomValue(columnIdsAtom);
  const filename = useFilename();
  const setLastSavedNotebook = useSetAtom(lastSavedNotebookAtom);
  const { sendComponentValues, sendInterrupt } = useRequestClient();

  const isEditing = viewState.mode === "edit";
  const isPresenting = viewState.mode === "present";
  const isRunning = useAtomValue(notebookIsRunningAtom);

  // Gridレイアウト用の状態管理（3Dモードの時のみ使用）
  const notebook = useNotebook();
  const layoutState = useLayoutState();
  const { setLayoutData } = useLayoutActions();
  const cells = flattenTopLevelNotebookCells(notebook);
  
  // GridLayoutRenderer用のsetLayoutラッパー
  // 3Dモードでは、セル配置情報のみを保存し、設定値（columns, rowHeight, maxWidth, bordered）は
  // grid3DConfigAtomに保存されるため、ここでは保存しない
  const setGridLayout = (layout: GridLayout) => {
    const currentGrid = layoutState.layoutData.grid as GridLayout | undefined;
    
    // セル配置情報のみを抽出
    const cellLayoutData: Partial<GridLayout> = {
      cells: layout.cells,
      scrollableCells: layout.scrollableCells,
      cellSide: layout.cellSide,
    };
    
    if (currentGrid) {
      // 既存のGridLayoutがある場合、セル配置情報のみを更新
      // 設定値はgrid3DConfigAtomから取得するため、ここでは保持
      setLayoutData({
        layoutView: "grid",
        data: {
          ...currentGrid,
          ...cellLayoutData,
        },
      });
    } else {
      // 初期化時は、grid3DConfigAtomから生成した設定値も含める
      // ただし、将来的にはgrid3DConfigAtomを優先する方針
      setLayoutData({ layoutView: "grid", data: layout });
    }
  };

  // 3D表示用の状態管理
  const threeDContainerRef = useRef<HTMLDivElement>(null);
  const sceneManagerRef = useRef<SceneManager | null>(null);
  const css2DServiceRef = useRef<CellCSS2DService | null>(null);
  const is3DModeFromAtom = useAtomValue(is3DModeAtom);
  const is3DMode = is3DModeFromAtom && viewState.mode === "edit"; // Editモードの時のみatomの値に従って3D表示を制御
  const [is3DInitialized, setIs3DInitialized] = useState(false); // 3D初期化完了フラグ
  const cell3DView = useAtomValue(cell3DViewAtom);
  const setCell3DView = useSetAtom(cell3DViewAtom);
  const hasRestoredViewRef = useRef(false); // 視点復元済みフラグ

  // 3Dモード用のグリッド設定
  const grid3DConfig = useAtomValue(grid3DConfigAtom);
  const setGrid3DConfig = useSetAtom(grid3DConfigAtom);

  // Initialize RuntimeState event-listeners
  useEffect(() => {
    RuntimeState.INSTANCE.start(sendComponentValues);
    return () => {
      RuntimeState.INSTANCE.stop();
    };
  }, []);

  // 3D表示の初期化
  useEffect(() => {
    if (!is3DMode) {
      setIs3DInitialized(false);
      return;
    }

    // コンテナがマウントされるまで待つ
    if (!threeDContainerRef.current) {
      return;
    }

    const container = threeDContainerRef.current;
    const sceneManager = new SceneManager();
    const css2DService = new CellCSS2DService();

    // シーンを初期化
    sceneManager.initialize(container);

    // CSS2DRendererを初期化
    const width = container.clientWidth || window.innerWidth;
    const height = container.clientHeight || window.innerHeight;
    css2DService.initializeRenderer(container, width, height);

    // シーンを設定
    const scene = sceneManager.getScene();
    if (scene) {
      css2DService.setScene(scene);
    }

    // CSS2Dレンダリングのコールバックを設定
    sceneManager.setCSS2DRenderCallback((scene, camera) => {
      css2DService.render(scene, camera);
    });

    sceneManagerRef.current = sceneManager;
    css2DServiceRef.current = css2DService;
    setIs3DInitialized(true);

    // OrbitControlsのendイベントで視点情報を保存
    const controls = sceneManager.getControls();
    let handleEnd: (() => void) | undefined;
    if (controls) {
      const camera = sceneManager.getCamera();
      handleEnd = () => {
        // sceneManagerとcontrolsは既に初期化されていることが保証されている
        if (camera && controls) {
          setCell3DView({
            position: {
              x: camera.position.x,
              y: camera.position.y,
              z: camera.position.z,
            },
            target: {
              x: controls.target.x,
              y: controls.target.y,
              z: controls.target.z,
            },
          });
        }
      };

      controls.addEventListener("end", handleEnd);
    }

    // リサイズハンドラー
    const handleResize = () => {
      if (container && sceneManager && css2DService) {
        const width = container.clientWidth || window.innerWidth;
        const height = container.clientHeight || window.innerHeight;
        css2DService.setSize(width, height);
        const scene = sceneManager.getScene();
        if (scene) {
          sceneManager.markNeedsRender();
        }
      }
    };

    window.addEventListener("resize", handleResize);

    return () => {
      window.removeEventListener("resize", handleResize);
      // OrbitControlsのendイベントリスナーを削除
      if (handleEnd && sceneManager) {
        const currentControls = sceneManager.getControls();
        if (currentControls) {
          currentControls.removeEventListener("end", handleEnd);
        }
      }
      sceneManager.dispose();
      css2DService.dispose();
      sceneManagerRef.current = null;
      css2DServiceRef.current = null;
      setIs3DInitialized(false);
      hasRestoredViewRef.current = false; // 復元フラグをリセット
    };
  }, [is3DMode, setCell3DView]);

  // 視点情報の復元（初回のみ）
  useEffect(() => {
    if (!is3DInitialized || !sceneManagerRef.current || hasRestoredViewRef.current) {
      return;
    }

    const savedView = cell3DView;
    if (savedView) {
      sceneManagerRef.current.setCameraView(
        new THREE.Vector3(
          savedView.position.x,
          savedView.position.y,
          savedView.position.z,
        ),
        new THREE.Vector3(
          savedView.target.x,
          savedView.target.y,
          savedView.target.z,
        ),
      );
      hasRestoredViewRef.current = true;
    }
  }, [is3DInitialized, cell3DView]);

  // 3Dモードが切れたら復元フラグをリセット
  useEffect(() => {
    if (!is3DMode) {
      hasRestoredViewRef.current = false;
    }
  }, [is3DMode]);

  // モード切り替え時にグリッド設定を同期
  const prevIs3DMode = usePrevious(is3DMode);
  useEffect(() => {
    // モードが切り替わった時のみ同期（初回レンダリング時は実行しない）
    if (prevIs3DMode === undefined || prevIs3DMode === is3DMode) {
      return;
    }

    if (is3DMode) {
      // 3Dモードに切り替えた時: layoutState.layoutData.grid → grid3DConfigAtom
      const currentGrid = layoutState.layoutData.grid as GridLayout | undefined;
      if (currentGrid) {
        setGrid3DConfig((prev) => ({
          ...prev,
          columns: currentGrid.columns,
          rowHeight: currentGrid.rowHeight,
          maxWidth: currentGrid.maxWidth,
          bordered: currentGrid.bordered ?? false,
        }));
      }
    } else {
      // 2Dモードに切り替えた時: grid3DConfigAtom → layoutState.layoutData.grid
      const currentGrid = layoutState.layoutData.grid as GridLayout | undefined;
      if (currentGrid) {
        setLayoutData({
          layoutView: "grid",
          data: {
            ...currentGrid,
            columns: grid3DConfig.columns,
            rowHeight: grid3DConfig.rowHeight,
            maxWidth: grid3DConfig.maxWidth,
            bordered: grid3DConfig.bordered,
          },
        });
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [is3DMode, prevIs3DMode]);

  const { connection } = useMarimoKernelConnection({
    autoInstantiate: userConfig.runtime.auto_instantiate,
    setCells: (cells, layout) => {
      setCells(cells);
      const names = cells.map((cell) => cell.name);
      const codes = cells.map((cell) => cell.code);
      const configs = cells.map((cell) => cell.config);
      setLastSavedNotebook({ names, codes, configs, layout });
    },
    sessionId: getSessionId(),
  });

  // Update document title whenever filename or app_title changes
  useEffect(() => {
    // Set document title: app_title takes precedence, then filename, then default
    document.title =
      appConfig.app_title ||
      Paths.basename(filename ?? "") ||
      "Untitled Notebook";
  }, [appConfig.app_title, filename]);

  // Delete column breakpoints if app width changes from "columns"
  const previousWidth = usePrevious(appConfig.width);
  useEffect(() => {
    if (previousWidth === "columns" && appConfig.width !== "columns") {
      mergeAllColumns();
    }
  }, [appConfig.width, previousWidth, mergeAllColumns, numColumns]);

  const runStaleCells = useRunStaleCells();
  const runAllCells = useRunAllCells();
  const togglePresenting = useTogglePresenting();

  // HOTKEYS
  useHotkey("global.runStale", () => {
    runStaleCells();
  });
  useHotkey("global.interrupt", () => {
    sendInterrupt();
  });
  useHotkey("global.hideCode", () => {
    togglePresenting();
  });
  useHotkey("global.runAll", () => {
    runAllCells();
  });
  useHotkey("global.collapseAllSections", () => {
    collapseAllCells();
  });
  useHotkey("global.expandAllSections", () => {
    expandAllCells();
  });

  const editableCellsArray = (
    <CellArray
      mode={viewState.mode}
      userConfig={userConfig}
      appConfig={appConfig}
    />
  );

  return (
    <>
      <AppContainer
        connection={connection}
        isRunning={isRunning}
        width={appConfig.width}
      >
        <AppHeader
          connection={connection}
          className={cn(
            "pt-4 sm:pt-12 pb-2 mb-4 print:hidden z-50",
            // Keep the header sticky when scrolling horizontally, for column mode
            "sticky left-0",
          )}
        >
          {isEditing && !is3DMode && (
            <div className="flex items-center justify-center container">
              <FilenameForm filename={filename} />
            </div>
          )}
        </AppHeader>

        {/* 3D表示モード */}
        {is3DMode ? (
          <>
            {/* 3Dモード用のグリッドコントロール */}
            <Grid3DControls config={grid3DConfig} setConfig={setGrid3DConfig} />
            {/* アラートとバナーは通常の2D表示として表示 */}
            <div className="m-auto pb-24 sm:pb-12 max-w-(--content-width) min-w-[400px] pr-4 relative z-50 pointer-events-none">
              <div className="pointer-events-auto">
                <PackageAlert />
                <StartupLogsAlert />
                <StdinBlockingAlert />
                <ConnectingAlert />
                <NotebookBanner width={appConfig.width} />
              </div>
            </div>
            {/* AddCellButtonsを3Dモードでも表示 */}
            {columnIds[0] && (
              <div className="relative z-50 flex justify-center">
                <AddCellButtons columnId={columnIds[0]} />
              </div>
            )}
            {/* 3D表示コンテナ */}
            <div
              ref={threeDContainerRef}
              className="w-full h-full relative"
              style={{ position: "absolute", top: 0, left: 0, width: "100%", height: "100%", zIndex: 1 }}
            >
              {is3DInitialized && hasCells && sceneManagerRef.current && css2DServiceRef.current ? (
                <>
                  <Cells3DRenderer
                    mode={viewState.mode}
                    userConfig={userConfig}
                    appConfig={appConfig}
                    sceneManager={sceneManagerRef.current}
                    css2DService={css2DServiceRef.current}
                  />
                  <Grid3DRenderer
                    mode={viewState.mode}
                    userConfig={userConfig}
                    appConfig={appConfig}
                    sceneManager={sceneManagerRef.current}
                    css2DService={css2DServiceRef.current}
                    layout={(() => {
                      // grid3DConfigAtomから設定値を取得し、layoutState.layoutData.gridのセル配置情報とマージ
                      const baseLayout = (layoutState.layoutData.grid as GridLayout) ||
                        GridLayoutPlugin.getInitialLayout(cells);
                      return convertGrid3DConfigToLayout(grid3DConfig, baseLayout);
                    })()}
                    setLayout={setGridLayout}
                    cells={cells}
                    grid3DConfig={grid3DConfig}
                  />
                </>
              ) : null}
            </div>
          </>
        ) : (
          /* 通常表示モード */
          hasCells && (
            <CellsRenderer appConfig={appConfig} mode={viewState.mode}>
              {editableCellsArray}
            </CellsRenderer>
          )
        )}
      </AppContainer>
      <MultiCellActionToolbar />
      {!hideControls && (
        <TooltipProvider>
          <Controls
            presenting={isPresenting}
            onTogglePresenting={togglePresenting}
            onInterrupt={sendInterrupt}
            onRun={runStaleCells}
            connectionState={connection.state}
            running={isRunning}
            appConfig={appConfig}
          />
        </TooltipProvider>
      )}
    </>
  );
};
