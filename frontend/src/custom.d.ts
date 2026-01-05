/* Copyright 2026 Marimo. All rights reserved. */
/* eslint-disable @typescript-eslint/no-explicit-any */
declare module "*.svg" {
  const content: string | undefined;
  export default content;
}

// Stricter lib types
interface Body {
  json<T = unknown>(): Promise<T>;
}

// Stricter lib types
interface JSON {
  parse(
    text: string,
    reviver?: (this: any, key: string, value: any) => any,
  ): unknown;

  rawJSON(value: string): unknown;
}

// Improve type inference for Array.filter with BooleanConstructor
interface Array<T> {
  filter(predicate: BooleanConstructor): NonNullable<T>[];
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

export {};