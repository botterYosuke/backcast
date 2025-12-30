/* Copyright 2026 Marimo. All rights reserved. */

import { usePrevious } from "@dnd-kit/utilities";
import { TooltipProvider } from "@radix-ui/react-tooltip";
import { useAtomValue, useSetAtom } from "jotai";
import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
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
import { CellArray } from "../components/editor/renderers/cell-array";
import { Cells3DRenderer } from "../components/editor/renderers/cells-3d-renderer";
import { CellsRenderer } from "../components/editor/renderers/cells-renderer";
import { useHotkey } from "../hooks/useHotkey";
import {
  cellIdsAtom,
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
import { viewStateAtom } from "./mode";
import { useRequestClient } from "./network/requests";
import { useFilename } from "./saving/filename";
import { lastSavedNotebookAtom } from "./saving/state";
import { useJotaiEffect } from "./state/jotai";
import { CellCSS2DService } from "./three/cell-css2d-service";
import { SceneManager } from "./three/scene-manager";
import { useMarimoKernelConnection } from "./websocket/useMarimoKernelConnection";

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
  const filename = useFilename();
  const setLastSavedNotebook = useSetAtom(lastSavedNotebookAtom);
  const { sendComponentValues, sendInterrupt } = useRequestClient();

  const isEditing = viewState.mode === "edit";
  const isPresenting = viewState.mode === "present";
  const isRunning = useAtomValue(notebookIsRunningAtom);

  // 3D表示用の状態管理
  const threeDContainerRef = useRef<HTMLDivElement>(null);
  const sceneManagerRef = useRef<SceneManager | null>(null);
  const css2DServiceRef = useRef<CellCSS2DService | null>(null);
  const [is3DMode] = useState(true); // デフォルトで3D表示を有効化
  const [is3DInitialized, setIs3DInitialized] = useState(false); // 3D初期化完了フラグ

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

    // セルコンテナを3D空間に配置
    const scene = sceneManager.getScene();
    if (scene) {
      css2DService.attachCellContainerToScene(scene, new THREE.Vector3(0, 0, 0));
    }

    // CSS2Dレンダリングのコールバックを設定
    sceneManager.setCSS2DRenderCallback((scene, camera) => {
      css2DService.render(scene, camera);
    });

    sceneManagerRef.current = sceneManager;
    css2DServiceRef.current = css2DService;
    setIs3DInitialized(true);

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
      sceneManager.dispose();
      css2DService.dispose();
      sceneManagerRef.current = null;
      css2DServiceRef.current = null;
      setIs3DInitialized(false);
    };
  }, [is3DMode]);

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
          {isEditing && (
            <div className="flex items-center justify-center container">
              <FilenameForm filename={filename} />
            </div>
          )}
        </AppHeader>

        {/* 3D表示モード */}
        {is3DMode ? (
          <div
            ref={threeDContainerRef}
            className="w-full h-full relative"
            style={{ position: "absolute", top: 0, left: 0, width: "100%", height: "100%" }}
          >
            {is3DInitialized && hasCells && sceneManagerRef.current && css2DServiceRef.current ? (
              <Cells3DRenderer
                mode={viewState.mode}
                userConfig={userConfig}
                appConfig={appConfig}
                sceneManager={sceneManagerRef.current}
                css2DService={css2DServiceRef.current}
              />
            ) : null}
          </div>
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
