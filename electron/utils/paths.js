/* Copyright 2026 Marimo. All rights reserved. */

import { app } from "electron";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

/**
 * Get the application root directory
 */
export function getAppRoot() {
  if (app.isPackaged) {
    // In production, app.getAppPath() returns the resources directory
    return path.join(app.getAppPath(), "..");
  }
  // In development, return the project root
  return path.join(__dirname, "../..");
}

/**
 * Get the server directory path
 */
export function getServerDir() {
  return path.join(getAppRoot(), "server");
}

/**
 * Get the Python runtime directory path
 */
export function getPythonRuntimeDir() {
  return path.join(getServerDir(), "python-runtime");
}

/**
 * Get the user data directory
 */
export function getUserDataDir() {
  return app.getPath("userData");
}

/**
 * Get the virtual environment directory path
 */
export function getVenvDir() {
  return path.join(getServerDir(), "venv");
}

/**
 * Get the Python executable path in the virtual environment
 */
export function getVenvPythonPath() {
  const venvDir = getVenvDir();
  if (process.platform === "win32") {
    return path.join(venvDir, "Scripts", "python.exe");
  }
  return path.join(venvDir, "bin", "python3");
}

