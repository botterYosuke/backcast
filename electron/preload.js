/* Copyright 2026 Marimo. All rights reserved. */

import { contextBridge, ipcRenderer } from "electron";

/**
 * Expose protected methods that allow the renderer process to use
 * the ipcRenderer without exposing the entire object
 */
contextBridge.exposeInMainWorld("electronAPI", {
  /**
   * Check if running in Electron
   */
  isElectron: true,

  /**
   * Get server URL from main process
   */
  getServerURL: () => ipcRenderer.invoke("server:get-url"),

  /**
   * Get server status from main process
   */
  getServerStatus: () => ipcRenderer.invoke("server:get-status"),

  /**
   * Request server restart
   */
  restartServer: () => ipcRenderer.invoke("server:restart"),

  /**
   * Listen to server status changes
   */
  onServerStatusChange: (callback) => {
    ipcRenderer.on("server:status-changed", (_event, status) => callback(status));
    // Return cleanup function
    return () => {
      ipcRenderer.removeAllListeners("server:status-changed");
    };
  },

  /**
   * Get server logs
   */
  getServerLogs: () => ipcRenderer.invoke("server:get-logs"),
});

