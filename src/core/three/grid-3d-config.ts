/* Copyright 2026 Marimo. All rights reserved. */

import { atomWithStorage } from "jotai/utils";
import { jotaiJsonStorage } from "@/utils/storage/jotai";
import {
  DEFAULT_GRID_3D_CONFIG,
  type Grid3DConfig,
} from "@/components/editor/renderers/3d-layout/types";

const KEY = "marimo:3d:gridConfig:v1";

/**
 * 3Dモード用のグリッド設定を管理するatom
 * 設定はlocalStorageに永続化される
 */
export const grid3DConfigAtom = atomWithStorage<Grid3DConfig>(
  KEY,
  DEFAULT_GRID_3D_CONFIG,
  jotaiJsonStorage,
);

