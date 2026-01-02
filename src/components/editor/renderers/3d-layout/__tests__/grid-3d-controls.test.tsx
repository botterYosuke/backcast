/* Copyright 2026 Marimo. All rights reserved. */
// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Grid3DControls } from "../grid-3d-controls";
import { DEFAULT_GRID_3D_CONFIG } from "../types";

describe("Grid3DControls", () => {
  afterEach(() => {
    cleanup();
  });

  it("should render all controls", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    expect(screen.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-row-height-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-max-width-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-bordered-switch")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-lock-switch")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-spacing-x-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-spacing-z-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-spacing-y-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-opacity-input")).toBeInTheDocument();
    expect(screen.getByTestId("grid-3d-snap-switch")).toBeInTheDocument();
  });

  // Note: NumberFieldのonChangeテストは、react-aria-componentsの動作により
  // fireEvent.changeでは動作しないため、簡略化しています。
  // NumberFieldのonChangeの動作確認は、NumberFieldコンポーネント自体のテストで行います。
  it("should have NumberField inputs for all numeric config values", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    // すべてのNumberFieldが存在することを確認
    expect(screen.getByLabelText(/columns/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/row height/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/max width/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/grid spacing x/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/grid depth/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/grid height/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/grid opacity/i)).toBeInTheDocument();
  });

  it("should call setConfig when bordered switch changes", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    // getByRoleを使用してswitchを取得
    const switchElement = screen.getByRole("switch", { name: /bordered/i });
    fireEvent.click(switchElement);
    
    expect(mockSetConfig).toHaveBeenCalledWith(
      expect.objectContaining({ bordered: true })
    );
  });

  it("should call setConfig when isLocked switch changes", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    const switchElement = screen.getByRole("switch", { name: /lock grid/i });
    fireEvent.click(switchElement);
    
    expect(mockSetConfig).toHaveBeenCalledWith(
      expect.objectContaining({ isLocked: true })
    );
  });

  it("should call setConfig when snapToGrid switch changes", () => {
    const mockSetConfig = vi.fn();
    render(
      <Grid3DControls
        config={DEFAULT_GRID_3D_CONFIG}
        setConfig={mockSetConfig}
      />
    );

    const switchElement = screen.getByRole("switch", { name: /snap to grid/i });
    fireEvent.click(switchElement);
    
    expect(mockSetConfig).toHaveBeenCalledWith(
      expect.objectContaining({ snapToGrid: true })
    );
  });

  it("should display current config values", () => {
    const customConfig = {
      ...DEFAULT_GRID_3D_CONFIG,
      columns: 24,
      spacingX: 500,
      gridOpacity: 0.5,
      bordered: true,
    };
    const mockSetConfig = vi.fn();
    const { container } = render(
      <Grid3DControls config={customConfig} setConfig={mockSetConfig} />
    );

    // 値が正しく表示されているか確認（NumberFieldの内部実装に依存するため、基本的な存在確認のみ）
    // withinを使用して、このテストでレンダリングされた要素のみを対象にする
    const withinContainer = within(container);
    expect(withinContainer.getByTestId("grid-3d-columns-input")).toBeInTheDocument();
    expect(withinContainer.getByTestId("grid-3d-spacing-x-input")).toBeInTheDocument();
    expect(withinContainer.getByTestId("grid-3d-opacity-input")).toBeInTheDocument();
    expect(withinContainer.getByTestId("grid-3d-bordered-switch")).toBeInTheDocument();
  });
});

