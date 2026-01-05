/* Copyright 2026 Marimo. All rights reserved. */
import { describe, expect, it, beforeEach, vi } from "vitest";
import { store } from "@/core/state/jotai";
import {
  type AppMode,
  getInitialAppMode,
  toggleAppMode,
  viewStateAtom,
  initialModeAtom,
  is3DModeAtom,
  runDuringPresentMode,
} from "@/core/mode";

// Mock requestAnimationFrame for tests
global.requestAnimationFrame = vi.fn((cb) => {
  setTimeout(cb, 0);
  return 1;
});

describe("mode", () => {
  beforeEach(() => {
    // Reset store before each test
    store.set(viewStateAtom, { mode: "not-set" as AppMode, cellAnchor: null });
    store.set(initialModeAtom, undefined);
    store.set(is3DModeAtom, true);
    vi.clearAllMocks();
  });

  describe("toggleAppMode", () => {
    it("should toggle from edit to present", () => {
      expect(toggleAppMode("edit")).toBe("present");
    });

    it("should toggle from present to edit", () => {
      expect(toggleAppMode("present")).toBe("edit");
    });

    it("should not toggle from read mode", () => {
      expect(toggleAppMode("read")).toBe("read");
    });

    it("should not toggle from home mode", () => {
      expect(toggleAppMode("home")).toBe("edit");
    });
  });

  describe("getInitialAppMode", () => {
    it("should return initial mode when set", () => {
      store.set(initialModeAtom, "edit");
      expect(getInitialAppMode()).toBe("edit");
    });

    it("should return home mode when set", () => {
      store.set(initialModeAtom, "home");
      expect(getInitialAppMode()).toBe("home");
    });

    it("should throw error when initial mode is not set", () => {
      store.set(initialModeAtom, undefined);
      expect(() => getInitialAppMode()).toThrow();
    });

    it("should throw error when initial mode is present", () => {
      store.set(initialModeAtom, "present");
      expect(() => getInitialAppMode()).toThrow();
    });
  });

  describe("viewStateAtom", () => {
    it("should initialize with not-set mode", () => {
      const state = store.get(viewStateAtom);
      expect(state.mode).toBe("not-set");
      expect(state.cellAnchor).toBeNull();
    });

    it("should update mode", () => {
      store.set(viewStateAtom, { mode: "edit", cellAnchor: null });
      const state = store.get(viewStateAtom);
      expect(state.mode).toBe("edit");
    });

    it("should update cellAnchor", () => {
      const cellId = "test-cell-id" as const;
      store.set(viewStateAtom, { mode: "edit", cellAnchor: cellId });
      const state = store.get(viewStateAtom);
      expect(state.cellAnchor).toBe(cellId);
    });
  });

  describe("is3DModeAtom", () => {
    it("should initialize with true", () => {
      const is3D = store.get(is3DModeAtom);
      expect(is3D).toBe(true);
    });

    it("should toggle to false", () => {
      store.set(is3DModeAtom, false);
      const is3D = store.get(is3DModeAtom);
      expect(is3D).toBe(false);
    });

    it("should toggle back to true", () => {
      store.set(is3DModeAtom, false);
      store.set(is3DModeAtom, true);
      const is3D = store.get(is3DModeAtom);
      expect(is3D).toBe(true);
    });
  });

  describe("runDuringPresentMode", () => {
    it("should run function immediately when already in present mode", async () => {
      store.set(viewStateAtom, { mode: "present", cellAnchor: null });
      let executed = false;

      await runDuringPresentMode(() => {
        executed = true;
      });

      expect(executed).toBe(true);
      expect(store.get(viewStateAtom).mode).toBe("present");
    });

    it("should switch to present mode, run function, then switch back", async () => {
      store.set(viewStateAtom, { mode: "edit", cellAnchor: null });
      let executed = false;

      await runDuringPresentMode(() => {
        executed = true;
      });

      expect(executed).toBe(true);
      expect(store.get(viewStateAtom).mode).toBe("edit");
    });

    it("should handle async function", async () => {
      store.set(viewStateAtom, { mode: "edit", cellAnchor: null });
      let executed = false;

      await runDuringPresentMode(async () => {
        await new Promise((resolve) => setTimeout(resolve, 10));
        executed = true;
      });

      expect(executed).toBe(true);
      expect(store.get(viewStateAtom).mode).toBe("edit");
    });
  });
});

