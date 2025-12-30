/* Copyright 2026 Marimo. All rights reserved. */

import { spawn, ChildProcess } from "child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "fs";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

/**
 * ServerManager manages the Python backend server process
 */
export class ServerManager {
  constructor(appRoot) {
    this.appRoot = appRoot;
    this.serverProcess = null;
    this.status = { status: "stopped", url: null };
    this.statusCallbacks = [];
    this.logs = [];
    this.serverPort = 2718;
    this.serverHost = "127.0.0.1";
  }

  /**
   * Get the path to the server executable
   */
  getServerExecutablePath() {
    // PyInstallerでビルドした実行可能ファイルのパス
    if (process.platform === "win32") {
      return path.join(this.appRoot, "server", "dist", "backcast-server", "backcast-server.exe");
    } else if (process.platform === "darwin") {
      return path.join(this.appRoot, "server", "dist", "backcast-server", "backcast-server");
    } else {
      return path.join(this.appRoot, "server", "dist", "backcast-server", "backcast-server");
    }
  }

  /**
   * Get the server script path (development only)
   */
  getServerScriptPath() {
    return path.join(__dirname, "run.py");
  }

  /**
   * Get the server URL
   */
  getServerUrl() {
    return `http://${this.serverHost}:${this.serverPort}`;
  }

  /**
   * Check if we're in packaged mode
   */
  isPackaged() {
    // Check if PyInstaller executable exists
    try {
      const exePath = this.getServerExecutablePath();
      return fs.existsSync(exePath);
    } catch {
      return false;
    }
  }

  /**
   * Start the server
   */
  async start() {
    if (this.serverProcess) {
      console.warn("Server is already running");
      return;
    }

    const isPackaged = this.isPackaged();
    let command;
    let args = [];
    let cwd;

    if (isPackaged) {
      // Production: PyInstallerでビルドした実行可能ファイルを実行
      command = this.getServerExecutablePath();
      args = [];
      cwd = path.dirname(command);
    } else {
      // Development: Pythonスクリプトを実行
      command = "python";
      args = [this.getServerScriptPath()];
      cwd = __dirname;
    }

    // Set environment variables
    const env = {
      ...process.env,
      ENV: isPackaged ? "production" : "development",
      PORT: String(this.serverPort),
      HOST: this.serverHost,
    };

    this.log(`Starting server: ${command} ${args.join(" ")}`);

    try {
      this.serverProcess = spawn(command, args, {
        env,
        cwd,
        stdio: ["ignore", "pipe", "pipe"],
      });

      this.serverProcess.stdout?.on("data", (data) => {
        const message = data.toString();
        this.log(`[SERVER] ${message.trim()}`);
      });

      this.serverProcess.stderr?.on("data", (data) => {
        const message = data.toString();
        this.log(`[SERVER ERROR] ${message.trim()}`);
      });

      this.serverProcess.on("exit", (code, signal) => {
        this.log(`Server process exited with code ${code} and signal ${signal}`);
        this.serverProcess = null;
        this.updateStatus({ status: "stopped", url: null });
      });

      this.serverProcess.on("error", (error) => {
        this.log(`Server process error: ${error.message}`);
        this.serverProcess = null;
        this.updateStatus({ status: "error", url: null });
      });

      // Wait a bit for server to start
      await new Promise((resolve) => setTimeout(resolve, 2000));

      // Check if process is still running
      if (this.serverProcess && !this.serverProcess.killed) {
        this.updateStatus({ status: "running", url: this.getServerUrl() });
      } else {
        this.updateStatus({ status: "error", url: null });
      }
    } catch (error) {
      this.log(`Failed to start server: ${error.message}`);
      this.serverProcess = null;
      this.updateStatus({ status: "error", url: null });
      throw error;
    }
  }

  /**
   * Stop the server
   */
  async stop() {
    if (!this.serverProcess) {
      return;
    }

    this.log("Stopping server...");

    return new Promise((resolve) => {
      if (this.serverProcess) {
        this.serverProcess.on("exit", () => {
          this.serverProcess = null;
          this.updateStatus({ status: "stopped", url: null });
          resolve();
        });

        // Try graceful shutdown first
        if (process.platform === "win32") {
          // Windows: taskkillを使用
          spawn("taskkill", ["/pid", this.serverProcess.pid.toString(), "/F", "/T"]);
        } else {
          // Unix系: SIGTERMを送信
          this.serverProcess.kill("SIGTERM");
        }

        // Force kill after timeout
        setTimeout(() => {
          if (this.serverProcess) {
            if (process.platform === "win32") {
              spawn("taskkill", ["/pid", this.serverProcess.pid.toString(), "/F", "/T"]);
            } else {
              this.serverProcess.kill("SIGKILL");
            }
            this.serverProcess = null;
            this.updateStatus({ status: "stopped", url: null });
            resolve();
          }
        }, 5000);
      } else {
        resolve();
      }
    });
  }

  /**
   * Get current status
   */
  getStatus() {
    return { ...this.status };
  }

  /**
   * Register status change callback
   */
  onStatusChange(callback) {
    this.statusCallbacks.push(callback);
  }

  /**
   * Update status and notify callbacks
   */
  updateStatus(status) {
    this.status = status;
    this.statusCallbacks.forEach((callback) => {
      try {
        callback(this.status);
      } catch (error) {
        console.error("Error in status callback:", error);
      }
    });
  }

  /**
   * Get logs
   */
  getLogs() {
    return [...this.logs];
  }

  /**
   * Add log entry
   */
  log(message) {
    const timestamp = new Date().toISOString();
    this.logs.push({ timestamp, message });
    // Keep only last 1000 logs
    if (this.logs.length > 1000) {
      this.logs.shift();
    }
    console.log(`[ServerManager] ${message}`);
  }
}
