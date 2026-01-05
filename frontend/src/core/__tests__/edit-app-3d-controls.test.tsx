/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { Provider as SlotzProvider } from "@marimo-team/react-slotz";
import { cleanup, render, screen } from "@testing-library/react";
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

vi.mock("../layout/layout", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../layout/layout")>();
  return {
    ...actual,
    useLayoutState: () => ({
      layoutData: {
        grid: null,
      },
    }),
    useLayoutActions: () => ({
      setLayoutData: vi.fn(),
    }),
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

describe("EditApp Grid3DControls Integration", () => {
  let store: ReturnType<typeof createStore>;

  beforeEach(() => {
    cleanup();
    store = createStore();
    store.set(is3DModeAtom, true);
    store.set(viewStateAtom, { mode: "edit", cellAnchor: null });
    store.set(grid3DConfigAtom, DEFAULT_GRID_3D_CONFIG);
    vi.clearAllMocks();
  });

  it("should show Grid3DControls when in 3D mode", () => {
    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Grid3DControlsが表示されていることを確認
    expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
  });

  it("should hide Grid3DControls when not in 3D mode", () => {
    store.set(is3DModeAtom, false);
    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Grid3DControlsが表示されていないことを確認
    expect(screen.queryAllByTestId("grid-3d-columns-input")).toHaveLength(0);
  });

  it("should pass grid3DConfigAtom value to Grid3DControls", () => {
    const customConfig = {
      ...DEFAULT_GRID_3D_CONFIG,
      columns: 24,
    };
    store.set(grid3DConfigAtom, customConfig);

    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Grid3DControlsが表示されていることを確認
    expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
  });

  it("should hide Grid3DControls when in read mode even if is3DModeAtom is true", () => {
    store.set(viewStateAtom, { mode: "read", cellAnchor: null });
    render(
      <Provider store={store}>
        <TooltipProvider>
          <SlotzProvider>
            <EditApp userConfig={defaultUserConfig} appConfig={defaultAppConfig} />
          </SlotzProvider>
        </TooltipProvider>
      </Provider>
    );

    // Grid3DControlsが表示されていないことを確認（readモードでは3Dモードが無効）
    expect(screen.queryAllByTestId("grid-3d-columns-input")).toHaveLength(0);
  });
});

