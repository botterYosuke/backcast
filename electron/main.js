/* Copyright 2026 Marimo. All rights reserved. */

import { app, BrowserWindow, ipcMain } from "electron";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { initLogger, logInfo, logError, logWarn } from "./utils/logger.js";
import { getAppRoot } from "./utils/paths.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Initialize logger
initLogger();

let mainWindow = null;
let serverManager = null;

/**
 * Create the main application window
 */
function createWindow() {
  logInfo("Creating main window...");

  mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: false, // Required for preload script
    },
    icon: path.join(getAppRoot(), "public", "logo.png"),
  });

  // Load the app
  if (app.isPackaged) {
    // Production: load from dist
    mainWindow.loadFile(path.join(getAppRoot(), "dist", "index.html"));
  } else {
    // Development: load from Vite dev server
    mainWindow.loadURL("http://localhost:3000");
  }

  // Open DevTools in development
  if (!app.isPackaged) {
    mainWindow.webContents.openDevTools();
  }

  mainWindow.on("closed", () => {
    mainWindow = null;
  });

  logInfo("Main window created");
}

/**
 * Initialize server manager (will be implemented in Phase 2)
 */
async function initServerManager() {
  logInfo("Initializing server manager...");
  // TODO: Implement server manager in Phase 2
  // serverManager = new ServerManager();
  // await serverManager.start();
}

/**
 * Cleanup on app quit
 */
async function cleanup() {
  logInfo("Cleaning up...");
  if (serverManager) {
    // await serverManager.stop();
  }
}

// App event handlers
app.whenReady().then(async () => {
  logInfo("App ready");
  await initServerManager();
  createWindow();

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});

app.on("before-quit", async () => {
  await cleanup();
});

// IPC handlers
ipcMain.handle("server:get-url", async () => {
  // TODO: Implement in Phase 2
  return "http://localhost:2718";
});

ipcMain.handle("server:get-status", async () => {
  // TODO: Implement in Phase 2
  return { status: "stopped", url: null };
});

ipcMain.handle("server:restart", async () => {
  // TODO: Implement in Phase 2
  logWarn("Server restart requested (not implemented yet)");
  return { success: false, message: "Not implemented yet" };
});

ipcMain.handle("server:get-logs", async () => {
  // TODO: Implement in Phase 2
  return [];
});

// Error handling
process.on("uncaughtException", (error) => {
  logError("Uncaught exception", error);
});

process.on("unhandledRejection", (reason, promise) => {
  logError("Unhandled rejection", new Error(String(reason)));
});

