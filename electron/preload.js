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
    // Create a wrapper function to maintain reference for removal
    const listener = (_event, status) => callback(status);
    ipcRenderer.on("server:status-changed", listener);
    // Return cleanup function that removes only this specific listener
    return () => {
      ipcRenderer.removeListener("server:status-changed", listener);
    };
  },

  /**
   * Get server logs
   */
  getServerLogs: () => ipcRenderer.invoke("server:get-logs"),
});

