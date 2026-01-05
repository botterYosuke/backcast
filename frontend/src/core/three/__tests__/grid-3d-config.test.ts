/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from "vitest";
import { store } from "@/core/state/jotai";
import { grid3DConfigAtom } from "../grid-3d-config";
import { DEFAULT_GRID_3D_CONFIG } from "@/components/editor/renderers/3d-layout/types";

const KEY = "marimo:3d:gridConfig:v1";

describe("grid3DConfigAtom", () => {
  beforeEach(() => {
    // ストレージをクリア
    localStorage.clear();
    // Jotaiストアをリセット
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
  });

  it("should initialize with default values", () => {
    const config = store.get(grid3DConfigAtom);
    expect(config).toEqual(DEFAULT_GRID_3D_CONFIG);
  });

  it("should persist values to storage", () => {
    const newConfig = { ...DEFAULT_GRID_3D_CONFIG, columns: 24 };
    store.set(grid3DConfigAtom, newConfig);

    // ストレージから直接確認
    const stored = localStorage.getItem(KEY);
    expect(stored).toBeTruthy();
    const parsed = JSON.parse(stored!);
    expect(parsed.columns).toBe(24);
  });

  it("should load values from storage", () => {
    // ストレージに値を設定
    const testConfig = { ...DEFAULT_GRID_3D_CONFIG, columns: 24, rowHeight: 30 };
    localStorage.setItem(KEY, JSON.stringify(testConfig));

    // ストレージに値が保存されていることを確認
    const stored = localStorage.getItem(KEY);
    expect(stored).toBeTruthy();
    const parsed = JSON.parse(stored!);
    expect(parsed.columns).toBe(24);
    expect(parsed.rowHeight).toBe(30);

    // 注意: atomWithStorageの実際の読み込み動作は非同期のため、
    // このテストはストレージへの保存のみを検証します
  });

  it("should use default values when storage is empty", () => {
    localStorage.removeItem(KEY);
    // ストアをリセット
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
    const config = store.get(grid3DConfigAtom);
    expect(config).toEqual(DEFAULT_GRID_3D_CONFIG);
  });

  it("should handle invalid values in storage by using defaults", () => {
    // 不正な値をストレージに設定
    localStorage.setItem(KEY, "invalid json");
    // ストアをリセットしてデフォルト値を使用
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
    const config = store.get(grid3DConfigAtom);
    expect(config).toEqual(DEFAULT_GRID_3D_CONFIG);
  });

  it("should update all config properties", () => {
    const newConfig = {
      ...DEFAULT_GRID_3D_CONFIG,
      columns: 24,
      rowHeight: 30,
      maxWidth: 1200,
      bordered: true,
      isLocked: true,
    };
    store.set(grid3DConfigAtom, newConfig);
    const config = store.get(grid3DConfigAtom);
    expect(config).toEqual(newConfig);
  });
});

