/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { Provider as SlotzProvider } from "@marimo-team/react-slotz";
import { act, cleanup, render, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { Provider } from "jotai";
import { createStore } from "jotai";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { EditApp } from "../edit-app";
import { is3DModeAtom, viewStateAtom } from "../mode";
import { grid3DConfigAtom } from "../three/grid-3d-config";
import { DEFAULT_GRID_3D_CONFIG } from "@/components/editor/renderers/3d-layout/types";
import type { AppConfig, UserConfig } from "../config/config-schema";
import type { GridLayout } from "@/components/editor/renderers/grid-layout/types";
import { layoutStateAtom } from "../layout/layout";
import type { CellId } from "../cells/ids";

// Mock dependencies
vi.mock("../websocket/useMarimoKernelConnection", () => ({
  useMarimoKernelConnection: () => ({
    connection: {
      state: "open",
    },
  }),
}));

vi.mock("../hooks/useHotkey", () => ({
  useHotkey: () => {},
}));

vi.mock("../saving/filename", () => ({
  useFilename: () => "test.py",
  useUpdateFilename: () => vi.fn(),
}));

vi.mock("../network/requests", () => ({
  useRequestClient: () => ({
    sendComponentValues: vi.fn(),
    sendInterrupt: vi.fn(),
  }),
}));

vi.mock("../layout/useTogglePresenting", () => ({
  useTogglePresenting: () => vi.fn(),
}));

// Note: useLayoutState and useLayoutActions are not mocked
// They will use the actual implementation which reads from layoutStateAtom
// We control the atom state directly via store.set(layoutStateAtom, ...)
// The Provider ensures that useAtomValue and useSetAtom use the correct store

// Mock ResizeObserver for jsdom
global.ResizeObserver = vi.fn().mockImplementation(() => ({
  observe: vi.fn(),
  unobserve: vi.fn(),
  disconnect: vi.fn(),
}));

vi.mock("../cells/cells", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../cells/cells")>();
  return {
    ...actual,
    useCellActions: () => ({
      setCells: vi.fn(),
      mergeAllColumns: vi.fn(),
      collapseAllCells: vi.fn(),
      expandAllCells: vi.fn(),
    }),
    useNotebook: () => ({
      cellData: {},
      cellRuntime: {},
      cellIds: {
        inOrderIds: [],
        getColumns: () => [],
        hasOnlyOneId: () => false,
        hasOnlyOneColumn: () => true,
        isEmpty: () => true,
      },
    }),
    useCellIds: () => ({
      inOrderIds: [],
      getColumns: () => [],
      hasOnlyOneId: () => false,
      hasOnlyOneColumn: () => true,
      isEmpty: () => true,
    }),
    flattenTopLevelNotebookCells: () => [],
  };
});

vi.mock("../three/scene-manager", () => ({
  SceneManager: vi.fn().mockImplementation(() => ({
    initialize: vi.fn(),
    getScene: () => null,
    getCamera: () => null,
    getControls: () => null,
    setCSS2DRenderCallback: vi.fn(),
    dispose: vi.fn(),
  })),
}));

vi.mock("../three/cell-css2d-service", () => ({
  CellCSS2DService: vi.fn().mockImplementation(() => ({
    initializeRenderer: vi.fn(),
    setScene: vi.fn(),
    render: vi.fn(),
    dispose: vi.fn(),
  })),
}));

const defaultUserConfig: UserConfig = {
  display: {
    cell_output: "below",
    code_editor_font_size: 14,
    dataframes: "rich",
    default_table_page_size: 10,
    default_table_max_columns: 10,
    default_width: "normal",
    theme: "light",
    reference_highlighting: false,
  },
  keymap: { preset: "default" },
  completion: {
    activate_on_typing: true,
    signature_hint_on_typing: false,
    copilot: false,
  },
  formatting: { line_length: 88 },
  package_management: { manager: "pip" },
  runtime: {
    auto_instantiate: false,
    default_sql_output: "native",
    auto_reload: "off",
    on_cell_change: "lazy",
    watcher_on_save: "lazy",
    reactive_tests: true,
    output_max_bytes: 1_000_000,
    std_stream_max_bytes: 1_000_000,
    pythonpath: [],
    dotenv: [".env"],
  },
  server: {
    browser: "default",
    follow_symlink: false,
  },
  save: { autosave: "off", autosave_delay: 1000, format_on_save: false },
  ai: {},
};

const defaultAppConfig: AppConfig = {
  width: "medium",
  app_title: null,
  auto_download: [],
  sql_output: "auto",
};

const createMockGridLayout = (overrides?: Partial<GridLayout>): GridLayout => ({
  columns: 12,
  rowHeight: 20,
  maxWidth: undefined,
  bordered: false,
  cells: [],
  scrollableCells: new Set<CellId>(),
  cellSide: new Map<CellId, "top" | "left" | "right" | "bottom">(),
  ...overrides,
});

describe("EditApp Grid Sync (2D ↔ 3D)", () => {
  let store: ReturnType<typeof createStore>;

  beforeEach(() => {
    cleanup();
    store = createStore();
    
    // Initialize atoms
    store.set(is3DModeAtom, false);
    store.set(viewStateAtom, { mode: "edit", cellAnchor: null });
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
    
    // Initialize layout state with a grid layout
    const initialGridLayout = createMockGridLayout({
      columns: 12,
      rowHeight: 20,
      maxWidth: 1000,
      bordered: false,
    });
    store.set(layoutStateAtom, {
      selectedLayout: "grid",
      layoutData: {
        grid: initialGridLayout,
      },
    });

    vi.clearAllMocks();
  });

  it("should sync grid settings from 2D to 3D when switching to 3D mode", async () => {
    // Setup: 2Dモードでグリッド設定を変更
    const gridLayout2D = createMockGridLayout({
      columns: 24,
      rowHeight: 30,
      maxWidth: 1500,
      bordered: true,
      cells: [{ i: "cell-1", x: 0, y: 0, w: 4, h: 2 }],
      scrollableCells: new Set(["cell-1" as CellId]),
    });
    
    store.set(layoutStateAtom, {
      selectedLayout: "grid",
      layoutData: {
        grid: gridLayout2D,
      },
    });

    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Wait for initial render
    await waitFor(() => {
      expect(store.get(layoutStateAtom).layoutData.grid).toBeDefined();
    });

    // Switch to 3D mode
    await act(async () => {
      store.set(is3DModeAtom, true);
    });

    // Wait for sync to complete
    await waitFor(() => {
      const grid3DConfig = store.get(grid3DConfigAtom);
      expect(grid3DConfig.columns).toBe(24);
      expect(grid3DConfig.rowHeight).toBe(30);
      expect(grid3DConfig.maxWidth).toBe(1500);
      expect(grid3DConfig.bordered).toBe(true);
    }, { timeout: 3000 });

    // Verify that Rows is not synced (should keep default or previous value)
    const grid3DConfig = store.get(grid3DConfigAtom);
    expect(grid3DConfig.rows).toBe(DEFAULT_GRID_3D_CONFIG.rows);
  });

  it("should sync grid settings from 3D to 2D when switching to 2D mode", async () => {
    // Setup: 3Dモードでグリッド設定を変更
    const grid3DConfig = {
      ...DEFAULT_GRID_3D_CONFIG,
      columns: 18,
      rowHeight: 25,
      maxWidth: 1200,
      bordered: true,
      rows: 10, // 3D専用設定
      isLocked: true, // 同期対象外
    };
    
    store.set(grid3DConfigAtom, grid3DConfig);
    store.set(is3DModeAtom, true);

    // Setup: 2Dモードのグリッドレイアウトにセル配置情報を追加
    const gridLayout2D = createMockGridLayout({
      columns: 12, // 古い値（同期される）
      rowHeight: 20, // 古い値（同期される）
      maxWidth: 1000, // 古い値（同期される）
      bordered: false, // 古い値（同期される）
      cells: [{ i: "cell-1", x: 0, y: 0, w: 4, h: 2 }],
      scrollableCells: new Set(["cell-1" as CellId]),
      cellSide: new Map([["cell-1" as CellId, "top"]]),
    });
    
    store.set(layoutStateAtom, {
      selectedLayout: "grid",
      layoutData: {
        grid: gridLayout2D,
      },
    });

    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Wait for initial render
    await waitFor(() => {
      expect(store.get(is3DModeAtom)).toBe(true);
    });

    // Switch to 2D mode
    await act(async () => {
      store.set(is3DModeAtom, false);
    });

    // Wait for sync to complete
    await waitFor(() => {
      const layoutState = store.get(layoutStateAtom);
      const grid = layoutState.layoutData.grid;
      expect(grid).toBeDefined();
      if (grid) {
        expect(grid.columns).toBe(18);
        expect(grid.rowHeight).toBe(25);
        expect(grid.maxWidth).toBe(1200);
        expect(grid.bordered).toBe(true);
      }
    }, { timeout: 3000 });

    // Verify that cell placement data is preserved
    const layoutState = store.get(layoutStateAtom);
    const grid = layoutState.layoutData.grid;
    if (grid) {
      expect(grid.cells).toEqual([{ i: "cell-1", x: 0, y: 0, w: 4, h: 2 }]);
      expect(grid.scrollableCells.has("cell-1" as CellId)).toBe(true);
      expect(grid.cellSide.get("cell-1" as CellId)).toBe("top");
    }
  });

  it("should not sync Rows, isLocked, or cell placement data", async () => {
    // Setup: 3Dモードで設定を変更
    const grid3DConfig = {
      ...DEFAULT_GRID_3D_CONFIG,
      columns: 18,
      rowHeight: 25,
      maxWidth: 1200,
      bordered: true,
      rows: 10, // 3D専用設定（同期されない）
      isLocked: true, // 同期されない
    };
    
    store.set(grid3DConfigAtom, grid3DConfig);
    store.set(is3DModeAtom, true);

    // Setup: 2Dモードのグリッドレイアウト
    const gridLayout2D = createMockGridLayout({
      columns: 12,
      rowHeight: 20,
      maxWidth: 1000,
      bordered: false,
      cells: [{ i: "cell-1", x: 0, y: 0, w: 4, h: 2 }],
      scrollableCells: new Set(["cell-1" as CellId]),
    });
    
    store.set(layoutStateAtom, {
      selectedLayout: "grid",
      layoutData: {
        grid: gridLayout2D,
      },
    });

    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Switch to 2D mode
    await act(async () => {
      store.set(is3DModeAtom, false);
    });

    // Wait for sync
    await waitFor(() => {
      const layoutState = store.get(layoutStateAtom);
      const grid = layoutState.layoutData.grid;
      expect(grid).toBeDefined();
    }, { timeout: 3000 });

    // Verify that Rows is not in GridLayout (2D doesn't have rows)
    const layoutState = store.get(layoutStateAtom);
    const grid = layoutState.layoutData.grid;
    if (grid) {
      // GridLayout doesn't have rows property
      expect("rows" in grid).toBe(false);
    }

    // Verify that isLocked is not synced (it's in grid3DConfig, not GridLayout)
    // GridLayout doesn't have isLocked property
    if (grid) {
      expect("isLocked" in grid).toBe(false);
    }

    // Verify that cell placement data is preserved
    if (grid) {
      expect(grid.cells).toEqual([{ i: "cell-1", x: 0, y: 0, w: 4, h: 2 }]);
      expect(grid.scrollableCells.has("cell-1" as CellId)).toBe(true);
    }
  });

  it("should skip sync when grid layout is not initialized", async () => {
    // Setup: グリッドレイアウトが未初期化
    store.set(layoutStateAtom, {
      selectedLayout: "grid",
      layoutData: {
        grid: undefined,
      },
    });

    store.set(is3DModeAtom, false);
    const initialGrid3DConfig = { ...store.get(grid3DConfigAtom) };

    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Switch to 3D mode
    await act(async () => {
      store.set(is3DModeAtom, true);
    });

    // Wait a bit to ensure sync would have happened if grid was initialized
    await waitFor(() => {
      expect(store.get(is3DModeAtom)).toBe(true);
    }, { timeout: 3000 });

    // Verify that grid3DConfig was not updated (should remain at initial value)
    const grid3DConfig = store.get(grid3DConfigAtom);
    expect(grid3DConfig.columns).toBe(initialGrid3DConfig.columns);
    expect(grid3DConfig.rowHeight).toBe(initialGrid3DConfig.rowHeight);
    expect(grid3DConfig.maxWidth).toBe(initialGrid3DConfig.maxWidth);
    expect(grid3DConfig.bordered).toBe(initialGrid3DConfig.bordered);
  });
});

