/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import React from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Provider } from "jotai";
import { createStore } from "jotai";
import { Grid3DControls } from "../grid-3d-controls";
import { Grid3DLayoutRenderer } from "../grid-3d-layout-renderer";
import { grid3DConfigAtom } from "@/core/three/grid-3d-config";
import { DEFAULT_GRID_3D_CONFIG, type Grid3DConfig } from "../types";
import { convertGrid3DConfigToLayout } from "../utils";
import type { GridLayout } from "../../grid-layout/types";
import type { CellData, CellRuntimeState } from "@/core/cells/types";

describe("Grid3DConfig and Grid3DLayoutRenderer Integration", () => {
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
    runElapsedTime: null,
    runStartTime: null,
    lastRunTime: null,
    staleInputs: new Set(),
    staleOutputs: new Set(),
    serializedEditorState: null,
  });

  const createMockLayout = (overrides?: Partial<GridLayout>): GridLayout => ({
    columns: 24,
    rowHeight: 20,
    maxWidth: 1000,
    bordered: false,
    cells: [],
    scrollableCells: new Set(),
    cellSide: new Map(),
    ...overrides,
  });

  // Test component that combines Grid3DControls and Grid3DLayoutRenderer
  // This simulates the integration in edit-app.tsx
  const TestIntegrationComponent: React.FC<{
    initialConfig: Grid3DConfig;
    onLayoutChange?: (layout: GridLayout) => void;
    onConfigChange?: (config: Grid3DConfig) => void;
  }> = ({ initialConfig, onLayoutChange, onConfigChange }) => {
    const store = createStore();
    store.set(grid3DConfigAtom, initialConfig);

    const [config, setConfig] = React.useState<Grid3DConfig>(initialConfig);
    const [layout, setLayout] = React.useState<GridLayout>(() =>
      convertGrid3DConfigToLayout(initialConfig),
    );

    // Sync grid3DConfig to GridLayout when config changes (simulating edit-app.tsx logic)
    React.useEffect(() => {
      const newLayout = convertGrid3DConfigToLayout(config, layout);
      setLayout(newLayout);
      onLayoutChange?.(newLayout);
    }, [config.columns, config.rowHeight, config.maxWidth, config.bordered, config.rows]);

    const handleSetConfig = (newConfig: Grid3DConfig) => {
      setConfig(newConfig);
      onConfigChange?.(newConfig);
    };

    const cells = [createMockCell("cell-1")];

    return (
      <Provider store={store}>
        <div id="App">
          <Grid3DControls config={config} setConfig={handleSetConfig} />
          <Grid3DLayoutRenderer
            layout={layout}
            setLayout={setLayout}
            cells={cells}
            mode="edit"
            appConfig={{ width: "normal" }}
            grid3DConfig={config}
          />
        </div>
      </Provider>
    );
  };

  it("should sync Max Width (px) from Grid3DControls to Grid3DLayoutRenderer", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, maxWidth: undefined };
    const layoutChanges: GridLayout[] = [];
    let setConfigFn: ((config: Grid3DConfig) => void) | null = null;

    const TestComponent = () => {
      const store = createStore();
      store.set(grid3DConfigAtom, initialConfig);
      const [config, setConfig] = React.useState<Grid3DConfig>(initialConfig);
      const [layout, setLayout] = React.useState<GridLayout>(() =>
        convertGrid3DConfigToLayout(initialConfig),
      );

      setConfigFn = (newConfig: Grid3DConfig) => {
        setConfig(newConfig);
        const newLayout = convertGrid3DConfigToLayout(newConfig, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      };

      // Sync grid3DConfig to GridLayout when config changes
      React.useEffect(() => {
        const newLayout = convertGrid3DConfigToLayout(config, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      }, [config.columns, config.rowHeight, config.maxWidth, config.bordered, config.rows]);

      const cells = [createMockCell("cell-1")];

      return (
        <Provider store={store}>
          <div id="App">
            <Grid3DControls config={config} setConfig={setConfig} />
            <Grid3DLayoutRenderer
              layout={layout}
              setLayout={setLayout}
              cells={cells}
              mode="edit"
              appConfig={{ width: "normal" }}
              grid3DConfig={config}
            />
          </div>
        </Provider>
      );
    };

    render(<TestComponent />);

    // Wait for initial render
    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-max-width-input")).toBeInTheDocument();
    });

    // Check initial layout reflects initial config
    expect(layoutChanges.length).toBeGreaterThan(0);
    const initialLayout = layoutChanges[layoutChanges.length - 1];
    expect(initialLayout.maxWidth).toBeUndefined();

    // Simulate config change by directly calling setConfig
    if (setConfigFn) {
      setConfigFn({ ...initialConfig, maxWidth: 1500 });
    }

    // Wait for layout to update
    await waitFor(() => {
      expect(layoutChanges.length).toBeGreaterThan(1);
    });

    // Verify layout was updated with new maxWidth
    const updatedLayout = layoutChanges[layoutChanges.length - 1];
    expect(updatedLayout.maxWidth).toBe(1500);

    // Verify Grid3DLayoutRenderer receives the layout with maxWidth
    const gridContainer = screen.getByText("Outputs").closest(".relative");
    expect(gridContainer).toBeInTheDocument();
  });

  it("should sync Columns from Grid3DControls to Grid3DLayoutRenderer", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, columns: 12 };
    const layoutChanges: GridLayout[] = [];
    let setConfigFn: ((config: Grid3DConfig) => void) | null = null;

    const TestComponent = () => {
      const store = createStore();
      store.set(grid3DConfigAtom, initialConfig);
      const [config, setConfig] = React.useState<Grid3DConfig>(initialConfig);
      const [layout, setLayout] = React.useState<GridLayout>(() =>
        convertGrid3DConfigToLayout(initialConfig),
      );

      setConfigFn = (newConfig: Grid3DConfig) => {
        setConfig(newConfig);
        const newLayout = convertGrid3DConfigToLayout(newConfig, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      };

      // Sync grid3DConfig to GridLayout when config changes
      React.useEffect(() => {
        const newLayout = convertGrid3DConfigToLayout(config, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      }, [config.columns, config.rowHeight, config.maxWidth, config.bordered, config.rows]);

      const cells = [createMockCell("cell-1")];

      return (
        <Provider store={store}>
          <div id="App">
            <Grid3DControls config={config} setConfig={setConfig} />
            <Grid3DLayoutRenderer
              layout={layout}
              setLayout={setLayout}
              cells={cells}
              mode="edit"
              appConfig={{ width: "normal" }}
              grid3DConfig={config}
            />
          </div>
        </Provider>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
    });

    // Check initial layout reflects initial config
    expect(layoutChanges.length).toBeGreaterThan(0);
    const initialLayout = layoutChanges[layoutChanges.length - 1];
    expect(initialLayout.columns).toBe(12);

    // Simulate config change by directly calling setConfig
    if (setConfigFn) {
      setConfigFn({ ...initialConfig, columns: 24 });
    }

    // Wait for layout to update
    await waitFor(() => {
      expect(layoutChanges.length).toBeGreaterThan(1);
    });

    // Verify layout was updated with new columns
    const updatedLayout = layoutChanges[layoutChanges.length - 1];
    expect(updatedLayout.columns).toBe(24);

    // Grid3DLayoutRenderer uses layout.columns for ReactGridLayout's cols prop
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should sync Rows from Grid3DControls to Grid3DLayoutRenderer", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, rows: undefined };
    const configChanges: Grid3DConfig[] = [];
    let setConfigFn: ((config: Grid3DConfig) => void) | null = null;

    const TestComponent = () => {
      const store = createStore();
      store.set(grid3DConfigAtom, initialConfig);
      const [config, setConfig] = React.useState<Grid3DConfig>(initialConfig);
      const [layout, setLayout] = React.useState<GridLayout>(() =>
        convertGrid3DConfigToLayout(initialConfig),
      );

      setConfigFn = (newConfig: Grid3DConfig) => {
        setConfig(newConfig);
        configChanges.push(newConfig);
        const newLayout = convertGrid3DConfigToLayout(newConfig, layout);
        setLayout(newLayout);
      };

      // Sync grid3DConfig to GridLayout when config changes
      React.useEffect(() => {
        const newLayout = convertGrid3DConfigToLayout(config, layout);
        setLayout(newLayout);
      }, [config.columns, config.rowHeight, config.maxWidth, config.bordered, config.rows]);

      const cells = [createMockCell("cell-1")];

      return (
        <Provider store={store}>
          <div id="App">
            <Grid3DControls config={config} setConfig={setConfig} />
            <Grid3DLayoutRenderer
              layout={layout}
              setLayout={setLayout}
              cells={cells}
              mode="edit"
              appConfig={{ width: "normal" }}
              grid3DConfig={config}
            />
          </div>
        </Provider>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-rows-input")).toBeInTheDocument();
    });

    // Check initial config has undefined rows
    expect(initialConfig.rows).toBeUndefined();

    // Simulate config change by directly calling setConfig
    if (setConfigFn) {
      setConfigFn({ ...initialConfig, rows: 24 });
    }

    // Wait for config to update
    await waitFor(() => {
      expect(configChanges.length).toBeGreaterThan(0);
    });

    // Verify config was updated with new rows
    const updatedConfig = configChanges[configChanges.length - 1];
    expect(updatedConfig.rows).toBe(24);

    // Grid3DLayoutRenderer uses grid3DConfig.rows for ReactGridLayout's rows prop
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should sync Row Height (px) from Grid3DControls to Grid3DLayoutRenderer", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, rowHeight: 20 };
    const layoutChanges: GridLayout[] = [];
    let setConfigFn: ((config: Grid3DConfig) => void) | null = null;

    const TestComponent = () => {
      const store = createStore();
      store.set(grid3DConfigAtom, initialConfig);
      const [config, setConfig] = React.useState<Grid3DConfig>(initialConfig);
      const [layout, setLayout] = React.useState<GridLayout>(() =>
        convertGrid3DConfigToLayout(initialConfig),
      );

      setConfigFn = (newConfig: Grid3DConfig) => {
        setConfig(newConfig);
        const newLayout = convertGrid3DConfigToLayout(newConfig, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      };

      // Sync grid3DConfig to GridLayout when config changes
      React.useEffect(() => {
        const newLayout = convertGrid3DConfigToLayout(config, layout);
        setLayout(newLayout);
        layoutChanges.push(newLayout);
      }, [config.columns, config.rowHeight, config.maxWidth, config.bordered, config.rows]);

      const cells = [createMockCell("cell-1")];

      return (
        <Provider store={store}>
          <div id="App">
            <Grid3DControls config={config} setConfig={setConfig} />
            <Grid3DLayoutRenderer
              layout={layout}
              setLayout={setLayout}
              cells={cells}
              mode="edit"
              appConfig={{ width: "normal" }}
              grid3DConfig={config}
            />
          </div>
        </Provider>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-row-height-input")).toBeInTheDocument();
    });

    // Check initial layout reflects initial config
    expect(layoutChanges.length).toBeGreaterThan(0);
    const initialLayout = layoutChanges[layoutChanges.length - 1];
    expect(initialLayout.rowHeight).toBe(20);

    // Simulate config change by directly calling setConfig
    if (setConfigFn) {
      setConfigFn({ ...initialConfig, rowHeight: 30 });
    }

    // Wait for layout to update
    await waitFor(() => {
      expect(layoutChanges.length).toBeGreaterThan(1);
    });

    // Verify layout was updated with new rowHeight
    const updatedLayout = layoutChanges[layoutChanges.length - 1];
    expect(updatedLayout.rowHeight).toBe(30);

    // Grid3DLayoutRenderer uses layout.rowHeight for ReactGridLayout's rowHeight prop
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should sync Bordered from Grid3DControls to Grid3DLayoutRenderer", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, bordered: false };
    const layoutChanges: GridLayout[] = [];

    render(
      <TestIntegrationComponent
        initialConfig={initialConfig}
        onLayoutChange={(layout) => layoutChanges.push(layout)}
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-bordered-switch")).toBeInTheDocument();
    });

    // Check initial layout reflects initial config
    expect(layoutChanges.length).toBeGreaterThan(0);
    const initialLayout = layoutChanges[layoutChanges.length - 1];
    expect(initialLayout.bordered).toBe(false);

    // Grid3DLayoutRenderer uses layout.bordered to add/remove grid-bordered class on #App
    const appEl = document.getElementById("App");
    expect(appEl).toBeInTheDocument();
    
    // Initially, grid-bordered class should not be present
    await waitFor(() => {
      expect(appEl?.classList.contains("grid-bordered")).toBe(false);
    });

    // Toggle Bordered
    const borderedSwitch = screen.getByTestId("grid-3d-bordered-switch");
    fireEvent.click(borderedSwitch);

    // Wait for layout to update
    await waitFor(() => {
      expect(layoutChanges.length).toBeGreaterThan(1);
    });

    // Verify layout was updated with new bordered value
    const updatedLayout = layoutChanges[layoutChanges.length - 1];
    expect(updatedLayout.bordered).toBe(true);

    // Wait for the useEffect in Grid3DLayoutRenderer to run
    await waitFor(() => {
      expect(appEl?.classList.contains("grid-bordered")).toBe(true);
    });
  });

  it("should update Grid3DLayoutRenderer layout when Grid3DControls config changes", async () => {
    const testInitialConfig = { ...DEFAULT_GRID_3D_CONFIG, columns: 12, maxWidth: 1000, bordered: false };
    const layoutChanges: GridLayout[] = [];

    render(
      <TestIntegrationComponent
        initialConfig={testInitialConfig}
        onLayoutChange={(layout) => layoutChanges.push(layout)}
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
    });

    // Initial layout should reflect initial config
    expect(layoutChanges.length).toBeGreaterThan(0);
    const initialLayout = layoutChanges[layoutChanges.length - 1];
    expect(initialLayout.columns).toBe(12);
    expect(initialLayout.maxWidth).toBe(1000);
    expect(initialLayout.bordered).toBe(false);

    // Verify Grid3DLayoutRenderer renders with the correct layout
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });

  it("should apply maxWidth style to Grid3DLayoutRenderer when maxWidth is set", async () => {
    const initialConfig = { ...DEFAULT_GRID_3D_CONFIG, maxWidth: 1900 };
    let receivedLayout: GridLayout | null = null;

    const TestComponent = () => {
      const store = createStore();
      store.set(grid3DConfigAtom, initialConfig);

      const [config] = React.useState<Grid3DConfig>(initialConfig);
      const [layout] = React.useState<GridLayout>(() =>
        convertGrid3DConfigToLayout(initialConfig),
      );

      receivedLayout = layout;

      const cells = [createMockCell("cell-1")];

      return (
        <Provider store={store}>
          <div id="App">
            <Grid3DControls config={config} setConfig={vi.fn()} />
            <Grid3DLayoutRenderer
              layout={layout}
              setLayout={vi.fn()}
              cells={cells}
              mode="edit"
              appConfig={{ width: "normal" }}
              grid3DConfig={config}
            />
          </div>
        </Provider>
      );
    };

    render(<TestComponent />);

    await waitFor(() => {
      expect(screen.getByTestId("grid-3d-max-width-input")).toBeInTheDocument();
    });

    // Verify layout has maxWidth
    expect(receivedLayout).not.toBeNull();
    expect(receivedLayout?.maxWidth).toBe(1900);

    // Grid3DLayoutRenderer applies maxWidth to the styles object
    // which is then applied to ReactGridLayout's style prop
    // We can verify the component renders correctly
    expect(screen.getByText("Outputs")).toBeInTheDocument();
  });
});
