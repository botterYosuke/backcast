/* Copyright 2026 Marimo. All rights reserved. */

import { app } from "electron";
import fs from "node:fs";
import path from "node:path";
import { getUserDataDir } from "./paths.js";

let logFile = null;

/**
 * Initialize logger
 */
export function initLogger() {
  const logDir = path.join(getUserDataDir(), "logs");
  if (!fs.existsSync(logDir)) {
    fs.mkdirSync(logDir, { recursive: true });
  }

  const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
  logFile = path.join(logDir, `backcast-${timestamp}.log`);

  // Write initial log
  writeLog("=== Backcast Application Started ===");
  writeLog(`App Version: ${app.getVersion()}`);
  writeLog(`Electron Version: ${process.versions.electron}`);
  writeLog(`Node Version: ${process.versions.node}`);
}

/**
 * Write log message to file
 */
function writeLog(message) {
  if (!logFile) return;

  const timestamp = new Date().toISOString();
  const logMessage = `[${timestamp}] ${message}\n`;

  try {
    fs.appendFileSync(logFile, logMessage);
  } catch (error) {
    console.error("Failed to write log:", error);
  }
}

/**
 * Log info message
 */
export function logInfo(message) {
  const logMessage = `[INFO] ${message}`;
  console.log(logMessage);
  writeLog(logMessage);
}

/**
 * Log error message
 */
export function logError(message, error = null) {
  const logMessage = `[ERROR] ${message}${error ? `: ${error.message}` : ""}`;
  console.error(logMessage);
  if (error) {
    console.error(error.stack);
    writeLog(`${logMessage}\n${error.stack}`);
  } else {
    writeLog(logMessage);
  }
}

/**
 * Log warning message
 */
export function logWarn(message) {
  const logMessage = `[WARN] ${message}`;
  console.warn(logMessage);
  writeLog(logMessage);
}

/**
 * Log debug message
 */
export function logDebug(message) {
  const logMessage = `[DEBUG] ${message}`;
  console.debug(logMessage);
  writeLog(logMessage);
}

