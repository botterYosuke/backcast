/* Copyright 2026 Marimo. All rights reserved. */

import { atomWithStorage } from "jotai/utils";
import { adaptForLocalStorage } from "@/utils/storage/jotai";
import type { CellId } from "../cells/ids";

const KEY = "marimo:3d:cellPositions:v1";

/**
 * 3D空間におけるセルの位置情報
 */
export interface Cell3DPosition {
  x: number;
  y: number;
  z: number;
}

/**
 * 3Dモードのセル位置情報を管理するatom
 * Map<CellId, Cell3DPosition>の形式で保存
 */
export const cell3DPositionsAtom = atomWithStorage<Map<CellId, Cell3DPosition>>(
  KEY,
  new Map(),
  adaptForLocalStorage({
    toSerializable: (value: Map<CellId, Cell3DPosition>) => {
      return [...value.entries()];
    },
    fromSerializable: (value: [CellId, Cell3DPosition][]) => {
      return new Map(value);
    },
  }),
);

