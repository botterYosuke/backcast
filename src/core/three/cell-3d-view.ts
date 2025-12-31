/* Copyright 2026 Marimo. All rights reserved. */

import { atomWithStorage } from "jotai/utils";
import { jotaiJsonStorage } from "@/utils/storage/jotai";

const KEY = "marimo:3d:cameraView:v1";

/**
 * 3Dモードのカメラ視点情報
 */
export interface Cell3DViewState {
  position: {
    x: number;
    y: number;
    z: number;
  };
  target: {
    x: number;
    y: number;
    z: number;
  };
}

/**
 * 3Dモードのカメラ視点情報を管理するatom
 */
export const cell3DViewAtom = atomWithStorage<Cell3DViewState | null>(
  KEY,
  null,
  jotaiJsonStorage,
);

