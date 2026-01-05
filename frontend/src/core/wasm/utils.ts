/* Copyright 2026 Marimo. All rights reserved. */

/**
 * Whether the current environment is Pyodide/WASM
 */
export function isWasm(): boolean {
  // Document is sometimes undefined in CI so we check to reduce flakiness
  if (typeof document === "undefined") {
    return false;
  }

  // In Electron environment, use actual Python server instead of Pyodide
  const isElectron =
    typeof window !== "undefined" &&
    typeof window.electronAPI !== "undefined";
  if (isElectron) {
    return false;
  }

  // Check for marimo-wasm element (used in web environment with Pyodide)
  return document.querySelector("marimo-wasm") !== null;
}
