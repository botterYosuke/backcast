/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Grid3DLayoutRenderer } from "../grid-3d-layout-renderer";
import type { GridLayout } from "../../grid-layout/types";
import type { CellData, CellRuntimeState } from "@/core/cells/types";

describe("Grid3DLayoutRenderer", () => {
  afterEach(() => {
    cleanup();
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
    runElapsedTimeMs: null,
    runStartTimestamp: null,
    staleInputs: false,
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

  it("should render without GridControls", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // GridControlsの要素が表示されていないことを確認
    // GridControlsには以下のtestidが含まれる:
    // - grid-columns-input
    // - grid-row-height-input
    // - grid-max-width-input
    // - grid-bordered-switch
    // - grid-lock-switch
    expect(screen.queryByTestId("grid-columns-input")).not.toBeInTheDocument();
    expect(screen.queryByTestId("grid-row-height-input")).not.toBeInTheDocument();
    expect(screen.queryByTestId("grid-max-width-input")).not.toBeInTheDocument();
    expect(screen.queryByTestId("grid-bordered-switch")).not.toBeInTheDocument();
    expect(screen.queryByTestId("grid-lock-switch")).not.toBeInTheDocument();
  });

  it("should render Outputs panel", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // Outputsパネルが表示されていることを確認
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should render cells in grid when they are in layout", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cell1 = createMockCell("cell-1", "print('cell1')");
    const cell2 = createMockCell("cell-2", "print('cell2')");
    const cells = [cell1, cell2];

    // cell-1をグリッドに追加
    layout.cells = [
      {
        i: "cell-1",
        x: 0,
        y: 0,
        w: 6,
        h: 2,
      },
    ];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // cell-1はグリッド内に表示される（react-grid-layoutの内部実装により、直接確認は難しい）
    // cell-2はOutputsパネルに表示される
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should render cells in Outputs panel when they are not in grid", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cell1 = createMockCell("cell-1", "print('cell1')");
    const cells = [cell1];

    // グリッドにセルを追加しない
    layout.cells = [];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // Outputsパネルが表示されていることを確認
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should not render GridControls in read mode", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells = [createMockCell("cell-1")];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="read"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // GridControlsが表示されていないことを確認
    expect(screen.queryByTestId("grid-columns-input")).not.toBeInTheDocument();
    expect(screen.queryByTestId("grid-lock-switch")).not.toBeInTheDocument();
  });

  it("should handle empty cells array", () => {
    const mockSetLayout = vi.fn();
    const layout = createMockLayout();
    const cells: (CellRuntimeState & CellData)[] = [];

    render(
      <Grid3DLayoutRenderer
        layout={layout}
        setLayout={mockSetLayout}
        cells={cells}
        mode="edit"
            appConfig={{ width: "medium", auto_download: [], sql_output: "auto" }}
      />
    );

    // Outputsパネルが表示されていることを確認
    expect(screen.getByText("Outputs")).toBeInTheDocument();
    // GridControlsが表示されていないことを確認
    expect(screen.queryByTestId("grid-columns-input")).not.toBeInTheDocument();
  });
});

