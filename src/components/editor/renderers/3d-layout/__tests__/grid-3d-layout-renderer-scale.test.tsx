/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { cleanup, render, act } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { afterEach, describe, expect, it, vi, beforeEach } from "vitest";
import { Grid3DLayoutRenderer } from "../grid-3d-layout-renderer";
import type { GridLayout } from "../../grid-layout/types";
import type { CellData, CellRuntimeState } from "@/core/cells/types";

describe("Grid3DLayoutRenderer - Scale handling", () => {
  afterEach(() => {
    cleanup();
    // DOMをクリーンアップ
    document.body.innerHTML = "";
  });

  beforeEach(() => {
    // タイマーをモック
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    // すべてのタイマーをクリア
    vi.clearAllTimers();
  });

  const createMockCell = (
    id: string,
    code: string = "print('test')",
  ): CellRuntimeState & CellData => ({
    id: id as any,
    name: id,
    code,
    config: {},
    output: null,
    status: "idle",
    interrupted: false,
    errored: false,
    stopped: false,
    runElapsedTime: null,
    runStartTime: null,
    lastRunTime: null,
    staleInputs: new Set(),
    staleOutputs: new Set(),
    serializedEditorState: null,
  });

  const createMockLayout = (): GridLayout => ({
    columns: 24,
    rowHeight: 20,
    maxWidth: 1000,
    bordered: false,
    cells: [],
    scrollableCells: new Set(),
    cellSide: new Map(),
  });

  it("should adjust react-grid-layout DOM size when scale is applied", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    // レンダリング
    const { container } = render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成（実際のDOM構造を模倣）
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    gridContainer.style.transform = "scale(1.84583)";
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    // 見た目サイズをモック（スケール適用後）
    const mockRect = {
      width: 1845.83,
      height: 1000,
      top: 0,
      left: 0,
      right: 1845.83,
      bottom: 1000,
      x: 0,
      y: 0,
      toJSON: vi.fn(),
    };
    reactGridLayoutElement.getBoundingClientRect = vi.fn(() => mockRect as DOMRect);
    gridContainer.appendChild(reactGridLayoutElement);

    // タイマーを進めてuseEffectを実行（初回実行と1回のインターバル実行）
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    // DOMサイズが調整されることを確認
    const expectedWidth = 1845.83 / 1.84583; // 約1000
    const expectedHeight = 1000 / 1.84583; // 約542
    expect(reactGridLayoutElement.style.width).toBe(`${expectedWidth}px`);
    expect(reactGridLayoutElement.style.height).toBe(`${expectedHeight}px`);
  });

  it("should reset DOM size when scale is 1.0", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    gridContainer.style.transform = "scale(1.0)";
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    reactGridLayoutElement.style.width = "1000px";
    reactGridLayoutElement.style.height = "500px";
    gridContainer.appendChild(reactGridLayoutElement);

    // タイマーを進めてuseEffectを実行（初回実行と1回のインターバル実行）
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    // スケールが1.0の場合、サイズがリセットされることを確認
    expect(reactGridLayoutElement.style.width).toBe("");
    expect(reactGridLayoutElement.style.height).toBe("");
  });

  it("should update DOM size when scale changes", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    const mockRect1 = {
      width: 1500,
      height: 800,
      top: 0,
      left: 0,
      right: 1500,
      bottom: 800,
      x: 0,
      y: 0,
      toJSON: vi.fn(),
    };
    reactGridLayoutElement.getBoundingClientRect = vi.fn(() => mockRect1 as DOMRect);
    gridContainer.appendChild(reactGridLayoutElement);

    // 最初のスケール: 1.5
    gridContainer.style.transform = "scale(1.5)";
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    const expectedWidth1 = 1500 / 1.5; // 1000
    expect(reactGridLayoutElement.style.width).toBe(`${expectedWidth1}px`);

    // スケールを変更: 2.0
    gridContainer.style.transform = "scale(2.0)";
    const mockRect2 = {
      width: 2000,
      height: 1600,
      top: 0,
      left: 0,
      right: 2000,
      bottom: 1600,
      x: 0,
      y: 0,
      toJSON: vi.fn(),
    };
    reactGridLayoutElement.getBoundingClientRect = vi.fn(() => mockRect2 as DOMRect);

    // インターバルが実行されるまで待機（100ms）
    await act(async () => {
      await vi.advanceTimersByTimeAsync(100);
    });

    const expectedWidth2 = 2000 / 2.0; // 1000
    expect(reactGridLayoutElement.style.width).toBe(`${expectedWidth2}px`);
  });

  it("should handle invalid scale values", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    gridContainer.style.transform = "scale(0)"; // 無効なスケール
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    reactGridLayoutElement.style.width = "1000px";
    reactGridLayoutElement.style.height = "500px";
    gridContainer.appendChild(reactGridLayoutElement);

    // タイマーを進めてuseEffectを実行（初回実行と1回のインターバル実行）
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    // 無効なスケールの場合、サイズがリセットされることを確認
    expect(reactGridLayoutElement.style.width).toBe("");
    expect(reactGridLayoutElement.style.height).toBe("");
  });

  it("should handle NaN scale values", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    gridContainer.style.transform = "scale(NaN)"; // NaNスケール
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    reactGridLayoutElement.style.width = "1000px";
    reactGridLayoutElement.style.height = "500px";
    gridContainer.appendChild(reactGridLayoutElement);

    // タイマーを進めてuseEffectを実行（初回実行と1回のインターバル実行）
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    // NaNスケールの場合、サイズがリセットされることを確認
    expect(reactGridLayoutElement.style.width).toBe("");
    expect(reactGridLayoutElement.style.height).toBe("");
  });

  it("should not update DOM size when scale has not changed", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // DOM要素を作成
    const gridContainer = document.createElement("div");
    gridContainer.className = "grid-3d-container";
    gridContainer.style.transform = "scale(1.5)";
    document.body.appendChild(gridContainer);

    const reactGridLayoutElement = document.createElement("div");
    reactGridLayoutElement.className = "react-grid-layout";
    const mockRect = {
      width: 1500,
      height: 800,
      top: 0,
      left: 0,
      right: 1500,
      bottom: 800,
      x: 0,
      y: 0,
      toJSON: vi.fn(),
    };
    reactGridLayoutElement.getBoundingClientRect = vi.fn(() => mockRect as DOMRect);
    gridContainer.appendChild(reactGridLayoutElement);

    // 最初の実行
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });

    const expectedWidth = 1500 / 1.5; // 1000
    expect(reactGridLayoutElement.style.width).toBe(`${expectedWidth}px`);

    const firstWidth = reactGridLayoutElement.style.width;

    // スケールが同じ場合、再度実行しても更新されない
    await act(async () => {
      await vi.advanceTimersByTimeAsync(100); // 次のインターバル実行
    });

    // サイズが変更されていないことを確認
    expect(reactGridLayoutElement.style.width).toBe(firstWidth);
  });

  it("should handle missing DOM elements gracefully", async () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    // DOM要素を作成せずにレンダリング
    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
        appConfig={{ width: "normal" }}
      />
    );

    // タイマーを進めてuseEffectを実行（初回実行と1回のインターバル実行）
    // DOM要素が存在しない場合、エラーが発生しないことを確認
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0); // 初回実行
      await vi.advanceTimersByTimeAsync(100); // 1回のインターバル実行
    });
    
    // エラーが発生しないことを確認（上記のactが成功すればOK）
    expect(true).toBe(true);
  });
});

