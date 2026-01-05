/* Copyright 2026 Marimo. All rights reserved. */

declare module "*.svg" {
  const content: string;
  export default content;
}

declare module "*.svg?inline" {
  const content: string;
  export default content;
}

declare module "*.png?inline" {
  const content: string;
  export default content;
}

// Electron API type definitions
interface ServerStatus {
  status: "stopped" | "starting" | "running" | "error";
  url: string | null;
  port?: number | null;
}

interface ElectronAPI {
  isElectron: boolean;
  getServerURL: () => Promise<string | null>;
  getServerStatus: () => Promise<ServerStatus>;
  restartServer: () => Promise<{ success: boolean; message: string }>;
  onServerStatusChange: (
    callback: (status: ServerStatus) => void,
  ) => () => void;
  getServerLogs: () => Promise<string[]>;
}

declare global {
  interface Window {
    electronAPI?: ElectronAPI;
  }
}