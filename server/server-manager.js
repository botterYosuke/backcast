/* Copyright 2026 Marimo. All rights reserved. */

import { spawn } from "child_process";
import net from "node:net";
import path from "node:path";
import fs from "node:fs";
import { getServerDir, getPythonRuntimeDir } from "../electron/utils/paths.js";
import { logInfo, logError, logWarn, logDebug } from "../electron/utils/logger.js";

/**
 * Server Manager for Python backend server
 * 
 * This module manages the lifecycle of the Python marimo server.
 */
export class ServerManager {
  constructor() {
    this.serverProcess = null;
    this.serverURL = null;
    this.status = "stopped"; // "stopped" | "starting" | "running" | "error"
    this.port = null;
    this.pythonPath = null;
    this.logs = []; // Array of { timestamp, type: "stdout" | "stderr", message }
    this.restartAttempts = 0;
    this.maxRestartAttempts = 3;
    this.statusChangeCallbacks = [];
    this.isStopping = false; // Flag to prevent handleServerCrash on graceful stop
  }

  /**
   * Find available port in the range 2718-2728
   */
  async findAvailablePort(startPort = 2718, endPort = 2728) {
    for (let port = startPort; port <= endPort; port++) {
      const isAvailable = await this.checkPortAvailable(port);
      if (isAvailable) {
        return port;
      }
    }
    throw new Error(`No available port found in range ${startPort}-${endPort}`);
  }

  /**
   * Check if a port is available
   */
  checkPortAvailable(port) {
    return new Promise((resolve) => {
      const server = net.createServer();
      server.listen(port, () => {
        server.once("close", () => resolve(true));
        server.close();
      });
      server.on("error", () => resolve(false));
    });
  }

  /**
   * Find Python executable
   */
  async findPythonExecutable() {
    // First, try to find Python in the embedded runtime
    const pythonRuntimeDir = getPythonRuntimeDir();
    const pythonExe = process.platform === "win32" ? "python.exe" : "python3";
    const embeddedPythonPath = path.join(pythonRuntimeDir, pythonExe);

    if (fs.existsSync(embeddedPythonPath)) {
      logInfo(`Found embedded Python at: ${embeddedPythonPath}`);
      return embeddedPythonPath;
    }

    // Fallback to system Python
    const systemPython = process.platform === "win32" ? "python.exe" : "python3";
    logInfo(`Using system Python: ${systemPython}`);
    return systemPython;
  }

  /**
   * Check if marimo is installed
   */
  async checkMarimoInstalled(pythonPath) {
    return new Promise((resolve) => {
      const checkProcess = spawn(pythonPath, ["-m", "marimo", "--version"], {
        stdio: "pipe",
      });

      // Ignore output, only check exit code
      checkProcess.stdout.on("data", () => {});
      checkProcess.stderr.on("data", () => {});

      checkProcess.on("close", (code) => {
        resolve(code === 0);
      });

      checkProcess.on("error", () => {
        resolve(false);
      });

      // Timeout after 5 seconds
      setTimeout(() => {
        checkProcess.kill();
        resolve(false);
      }, 5000);
    });
  }

  /**
   * Perform health check on the server
   */
  async checkHealth(url, maxAttempts = 30, intervalMs = 1000) {
    const healthUrl = `${url}/healthz`;
    logDebug(`Checking health at: ${healthUrl}`);

    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      try {
        // Use AbortController for better compatibility
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 2000);

        let response;
        try {
          response = await fetch(healthUrl, {
            method: "GET",
            signal: controller.signal,
          });
        } finally {
          clearTimeout(timeoutId);
        }

        if (response.ok) {
          logInfo(`Server health check passed (attempt ${attempt + 1})`);
          return true;
        }
      } catch (error) {
        // Ignore errors and retry
        logDebug(`Health check attempt ${attempt + 1} failed: ${error.message}`);
      }

      // Wait before next attempt
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }

    return false;
  }

  /**
   * Add log entry
   */
  addLog(type, message) {
    const timestamp = new Date().toISOString();
    this.logs.push({ timestamp, type, message });
    // Keep only last 1000 log entries
    if (this.logs.length > 1000) {
      this.logs.shift();
    }
  }

  /**
   * Update status and notify callbacks
   */
  setStatus(newStatus) {
    if (this.status !== newStatus) {
      const oldStatus = this.status;
      this.status = newStatus;
      logInfo(`Server status changed: ${oldStatus} -> ${newStatus}`);
      this.notifyStatusChange();
    }
  }

  /**
   * Notify status change callbacks
   */
  notifyStatusChange() {
    const status = this.getStatus();
    this.statusChangeCallbacks.forEach((callback) => {
      try {
        callback(status);
      } catch (error) {
        logError("Error in status change callback", error);
      }
    });
  }

  /**
   * Register status change callback
   */
  onStatusChange(callback) {
    this.statusChangeCallbacks.push(callback);
    return () => {
      const index = this.statusChangeCallbacks.indexOf(callback);
      if (index > -1) {
        this.statusChangeCallbacks.splice(index, 1);
      }
    };
  }

  /**
   * Start the Python server
   */
  async start() {
    if (this.status === "starting" || this.status === "running") {
      logWarn("Server is already starting or running");
      return;
    }

    try {
      this.setStatus("starting");
      logInfo("Starting Python server...");

      // Find Python executable
      this.pythonPath = await this.findPythonExecutable();
      if (!this.pythonPath) {
        throw new Error("Python executable not found");
      }

      // Check if marimo is installed
      const marimoInstalled = await this.checkMarimoInstalled(this.pythonPath);
      if (!marimoInstalled) {
        logWarn("marimo is not installed. Attempting to start anyway...");
        // In Phase 4, we'll handle installation automatically
      }

      // Find available port
      this.port = await this.findAvailablePort();
      logInfo(`Using port: ${this.port}`);

      // Start the server process
      const serverDir = getServerDir();
      const serverUrl = `http://127.0.0.1:${this.port}`;
      this.serverURL = serverUrl;

      const args = [
        "-m",
        "marimo",
        "edit",
        "--port",
        String(this.port),
        "--host",
        "127.0.0.1",
        "--headless", // Don't launch a browser
      ];

      logDebug(`Starting Python process: ${this.pythonPath} ${args.join(" ")}`);

      this.serverProcess = spawn(this.pythonPath, args, {
        cwd: serverDir,
        stdio: ["ignore", "pipe", "pipe"],
        env: {
          ...process.env,
          PYTHONUNBUFFERED: "1", // Ensure unbuffered output
        },
      });

      // Capture stdout
      this.serverProcess.stdout.on("data", (data) => {
        const message = data.toString();
        this.addLog("stdout", message);
        logDebug(`[Server stdout] ${message.trim()}`);
      });

      // Capture stderr
      this.serverProcess.stderr.on("data", (data) => {
        const message = data.toString();
        this.addLog("stderr", message);
        logWarn(`[Server stderr] ${message.trim()}`);
      });

      // Handle process exit
      this.serverProcess.on("exit", (code, signal) => {
        logInfo(`Server process exited with code ${code}, signal ${signal}`);
        this.serverProcess = null;

        // Only handle crash if not stopping gracefully
        if (!this.isStopping && (this.status === "running" || this.status === "starting")) {
          // Unexpected exit
          this.setStatus("error");
          this.handleServerCrash();
        } else if (this.isStopping) {
          // Graceful shutdown completed
          this.isStopping = false;
        }
      });

      // Handle process errors
      this.serverProcess.on("error", (error) => {
        logError("Failed to start server process", error);
        this.setStatus("error");
        this.serverProcess = null;
      });

      // Wait for server to be ready
      logInfo("Waiting for server to be ready...");
      const isHealthy = await this.checkHealth(serverUrl);

      if (isHealthy) {
        this.setStatus("running");
        this.restartAttempts = 0; // Reset restart attempts on successful start
        logInfo(`Server started successfully at ${serverUrl}`);
      } else {
        throw new Error("Server health check failed");
      }
    } catch (error) {
      logError("Failed to start server", error);
      this.setStatus("error");
      this.serverProcess = null;
      this.serverURL = null;
      this.port = null;

      // Attempt automatic restart if within retry limit
      if (this.restartAttempts < this.maxRestartAttempts) {
        await this.handleServerCrash();
      } else {
        throw error;
      }
    }
  }

  /**
   * Handle server crash with automatic restart
   */
  async handleServerCrash() {
    if (this.restartAttempts >= this.maxRestartAttempts) {
      logError(
        `Max restart attempts (${this.maxRestartAttempts}) reached. Stopping automatic restart.`
      );
      return;
    }

    this.restartAttempts++;
    const backoffMs = Math.min(1000 * Math.pow(2, this.restartAttempts - 1), 10000); // Exponential backoff, max 10s
    logWarn(
      `Server crashed. Attempting restart ${this.restartAttempts}/${this.maxRestartAttempts} after ${backoffMs}ms...`
    );

    await new Promise((resolve) => setTimeout(resolve, backoffMs));

    try {
      await this.start();
    } catch (error) {
      logError("Automatic restart failed", error);
    }
  }

  /**
   * Stop the Python server
   */
  async stop() {
    if (this.status === "stopped") {
      logWarn("Server is already stopped");
      return;
    }

    logInfo("Stopping Python server...");
    this.isStopping = true; // Set flag to prevent handleServerCrash
    this.setStatus("stopped");

    if (!this.serverProcess) {
      logWarn("Server process not found");
      this.isStopping = false;
      return;
    }

    return new Promise((resolve) => {
      const childProcess = this.serverProcess;
      this.serverProcess = null;

      // Try graceful shutdown first
      // Windows doesn't support SIGTERM, use kill() without signal
      if (process.platform === "win32") {
        // On Windows, kill() without signal sends SIGTERM equivalent
        childProcess.kill();
      } else {
        childProcess.kill("SIGTERM");
      }

      // Force kill after timeout
      const timeout = setTimeout(() => {
        logWarn("Server did not terminate gracefully, forcing kill...");
        if (process.platform === "win32") {
          // On Windows, kill() without signal is the only option
          childProcess.kill();
        } else {
          childProcess.kill("SIGKILL");
        }
        this.isStopping = false;
        resolve();
      }, 5000);

      childProcess.on("exit", () => {
        clearTimeout(timeout);
        logInfo("Server stopped successfully");
        this.serverURL = null;
        this.port = null;
        this.isStopping = false;
        resolve();
      });
    });
  }

  /**
   * Get server status
   */
  getStatus() {
    return {
      status: this.status,
      url: this.serverURL,
    };
  }

  /**
   * Get server logs
   */
  getLogs() {
    return this.logs;
  }

  /**
   * Clear server logs
   */
  clearLogs() {
    this.logs = [];
  }
}

