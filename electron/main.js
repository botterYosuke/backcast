/* Copyright 2026 Marimo. All rights reserved. */

import { app, BrowserWindow, ipcMain } from "electron";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { initLogger, logInfo, logError, logWarn } from "./utils/logger.js";
import { getAppRoot } from "./utils/paths.js";
import { ServerManager } from "../server/server-manager.js";

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
 * Initialize server manager
 */
async function initServerManager() {
  logInfo("Initializing server manager...");
  try {
    serverManager = new ServerManager();

    // Register status change callback to notify renderer
    serverManager.onStatusChange((status) => {
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send("server:status-changed", status);
      }
    });

    // Start the server
    await serverManager.start();
  } catch (error) {
    logError("Failed to initialize server manager", error);
  }
}

/**
 * Cleanup on app quit
 */
async function cleanup() {
  logInfo("Cleaning up...");
  if (serverManager) {
    try {
      await serverManager.stop();
    } catch (error) {
      logError("Error stopping server during cleanup", error);
    }
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
  if (!serverManager) {
    return null;
  }
  const status = serverManager.getStatus();
  return status.url;
});

ipcMain.handle("server:get-status", async () => {
  if (!serverManager) {
    return { status: "stopped", url: null };
  }
  return serverManager.getStatus();
});

ipcMain.handle("server:restart", async () => {
  if (!serverManager) {
    return { success: false, message: "Server manager not initialized" };
  }

  try {
    logInfo("Server restart requested");
    await serverManager.stop();
    await serverManager.start();
    return { success: true, message: "Server restarted successfully" };
  } catch (error) {
    logError("Failed to restart server", error);
    return { success: false, message: error.message };
  }
});

ipcMain.handle("server:get-logs", async () => {
  if (!serverManager) {
    return [];
  }
  return serverManager.getLogs();
});

// Error handling
process.on("uncaughtException", (error) => {
  logError("Uncaught exception", error);
});

process.on("unhandledRejection", (reason, promise) => {
  logError("Unhandled rejection", new Error(String(reason)));
});

