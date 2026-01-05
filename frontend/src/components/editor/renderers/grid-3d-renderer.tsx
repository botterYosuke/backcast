/* Copyright 2026 Marimo. All rights reserved. */

import { createPortal } from "react-dom";
import React, { useEffect, useState } from "react";
import * as THREE from "three";
import { CellCSS2DService } from "@/core/three/cell-css2d-service";
import { SceneManager } from "@/core/three/scene-manager";
import { Grid3DLayoutRenderer } from "./3d-layout/grid-3d-layout-renderer";
import type { GridLayout } from "./grid-layout/types";
import type { Grid3DConfig } from "./3d-layout/types";
import type { AppConfig, UserConfig } from "@/core/config/config-schema";
import type { AppMode } from "@/core/mode";
import type { CellData, CellRuntimeState } from "@/core/cells/types";

interface Grid3DRendererProps {
  mode: AppMode;
  userConfig: UserConfig;
  appConfig: AppConfig;
  sceneManager: SceneManager;
  css2DService: CellCSS2DService;
  layout: GridLayout;
  setLayout: (layout: GridLayout) => void;
  cells: (CellRuntimeState & CellData)[];
  grid3DConfig?: Grid3DConfig;
}

/**
 * Grid3DRenderer
 *
 * Gridレイアウトを3D空間に配置するコンポーネント
 * - Grid3DLayoutRendererをCSS2DRendererで表示
 * - 1つのCSS2DObjectとしてGrid全体を表示
 * - セルを追加するロジックは含めない（Grid3DLayoutRendererが内部で処理）
 */
export const Grid3DRenderer: React.FC<Grid3DRendererProps> = ({
  mode,
  appConfig,
  sceneManager,
  css2DService,
  layout,
  setLayout,
  cells,
  grid3DConfig,
}) => {
  const [gridContainer, setGridContainer] = useState<HTMLDivElement | null>(null);

  // CellCSS2DServiceからグリッドコンテナを取得
  useEffect(() => {
    const container = css2DService.getGridContainer();
    if (container) {
      setGridContainer(container);
    } else {
      console.warn(
        "Grid container is not available. Make sure initializeRenderer() is called first.",
      );
    }
  }, [css2DService]);

  // グリッドコンテナをシーンにアタッチ
  useEffect(() => {
    if (!gridContainer) {
      return;
    }

    const scene = sceneManager.getScene();
    if (!scene) {
      return;
    }

    // 既にアタッチされている場合はスキップ
    if (!css2DService.getGridCSS2DObject()) {
      css2DService.attachGridContainerToScene(scene, new THREE.Vector3(0, 0, 0));
    }
  }, [gridContainer, sceneManager, css2DService]);

  // Gridコンテナが準備できていない場合は何も表示しない
  if (!gridContainer) {
    return null;
  }

  // Grid3DLayoutRendererをCSS2Dコンテナ内にレンダリング
  return createPortal(
    <Grid3DLayoutRenderer
      appConfig={appConfig}
      mode={mode}
      cells={cells}
      layout={layout}
      setLayout={setLayout}
      grid3DConfig={grid3DConfig}
      sceneManager={sceneManager}
    />,
    gridContainer,
  );
};

