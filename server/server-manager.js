/* Copyright 2026 Marimo. All rights reserved. */

import { spawn, execSync } from "child_process";
import net from "node:net";
import path from "node:path";
import fs from "node:fs";
import { getServerDir, getPythonRuntimeDir, getVenvDir, getVenvPythonPath } from "../electron/utils/paths.js";
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
   * Find system Python executable from environment variables
   * Used for creating virtual environments
   * If system Python is not found, checks for existing virtual environment
   */
  async findSystemPython() {
    // 1. Check PYTHON environment variable (direct path to Python executable)
    if (process.env.PYTHON) {
      const pythonPath = process.env.PYTHON;
      if (fs.existsSync(pythonPath)) {
        try {
          execSync(`"${pythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
          logInfo(`Found system Python from PYTHON environment variable: ${pythonPath}`);
          return pythonPath;
        } catch {
          logWarn(`Python from PYTHON environment variable is not executable: ${pythonPath}`);
        }
      } else {
        logWarn(`Python path from PYTHON environment variable does not exist: ${pythonPath}`);
      }
    }

    // 2. Check for existing virtual environment (server/python-env/)
    logInfo("PYTHON environment variable not set. Checking for existing virtual environment...");
    const venvPythonPath = getVenvPythonPath();
    if (fs.existsSync(venvPythonPath)) {
      try {
        execSync(`"${venvPythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
        logInfo(`Found Python in existing virtual environment: ${venvPythonPath}`);
        logWarn("Using existing virtual environment Python. Note: This Python cannot create new virtual environments.");
        return venvPythonPath;
      } catch {
        logWarn(`Virtual environment Python found but not executable: ${venvPythonPath}`);
      }
    }

    // 3. No Python found
    throw new Error(
      `System Python executable not found. Please set PYTHON environment variable to point to your Python installation, ` +
      `or ensure the virtual environment exists at: ${getVenvDir()}`
    );
  }

  /**
   * Find Python executable
   */
  async findPythonExecutable() {
    // First, try to find Python in the virtual environment
    const venvPythonPath = getVenvPythonPath();
    if (fs.existsSync(venvPythonPath)) {
      // Verify the Python executable works
      try {
        execSync(`"${venvPythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
        logInfo(`Found Python in virtual environment at: ${venvPythonPath}`);
        return venvPythonPath;
      } catch {
        logWarn(`Virtual environment Python found but not executable: ${venvPythonPath}. Falling back...`);
      }
    }

    // Second, try to find Python in the embedded runtime
    const pythonRuntimeDir = getPythonRuntimeDir();
    const pythonExe = process.platform === "win32" ? "python.exe" : "python3";
    const embeddedPythonPath = path.join(pythonRuntimeDir, pythonExe);

    if (fs.existsSync(embeddedPythonPath)) {
      logInfo(`Found embedded Python at: ${embeddedPythonPath}`);
      return embeddedPythonPath;
    }

    // On Windows, try to find Python using 'where' command
    if (process.platform === "win32") {
      try {
        const result = execSync("where python", { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
        const pythonPath = result.trim().split("\n")[0]?.trim();
        if (pythonPath && fs.existsSync(pythonPath)) {
          logInfo(`Found Python in PATH: ${pythonPath}`);
          return pythonPath;
        } else if (pythonPath) {
          logWarn(`Python path found but file doesn't exist: ${pythonPath}`);
        }
      } catch (error) {
        logWarn(`Failed to find Python using 'where' command: ${error.message}`);
      }

      // Also try 'python3' on Windows (some installations use python3.exe)
      try {
        const result = execSync("where python3", { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
        const pythonPath = result.trim().split("\n")[0]?.trim();
        if (pythonPath && fs.existsSync(pythonPath)) {
          logInfo(`Found Python3 in PATH: ${pythonPath}`);
          return pythonPath;
        }
      } catch {
        logDebug("Failed to find Python3 using 'where' command");
      }
    }

    // Fallback: try to verify python.exe exists by spawning it
    const systemPython = process.platform === "win32" ? "python.exe" : "python3";
    try {
      // Try to run python --version to verify it exists
      execSync(`${systemPython} --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
      logInfo(`Using system Python: ${systemPython}`);
      return systemPython;
    } catch {
      throw new Error(`Python executable not found. Please install Python or ensure it's in your PATH. Tried: ${systemPython}`);
    }
  }

  /**
   * Create a virtual environment
   */
  async createVirtualEnvironment(systemPythonPath, venvPath) {
    return new Promise((resolve, reject) => {
      logInfo(`Creating virtual environment at: ${venvPath}`);
      let stdoutOutput = "";
      let stderrOutput = "";
      let isResolved = false;

      const createProcess = spawn(systemPythonPath, ["-m", "venv", venvPath], {
        stdio: ["ignore", "pipe", "pipe"],
        shell: true,
        env: {
          ...process.env,
          PYTHONUNBUFFERED: "1",
        },
      });

      createProcess.stdout.on("data", (data) => {
        const message = data.toString();
        stdoutOutput += message;
        this.addLog("stdout", message);
        logDebug(`[venv creation stdout] ${message.trim()}`);
      });

      createProcess.stderr.on("data", (data) => {
        const message = data.toString();
        stderrOutput += message;
        this.addLog("stderr", message);
        logDebug(`[venv creation stderr] ${message.trim()}`);
      });

      // Timeout after 60 seconds
      const timeout = setTimeout(() => {
        if (!isResolved) {
          isResolved = true;
          createProcess.kill();
          logError("Virtual environment creation timed out");
          reject(new Error("Virtual environment creation timed out after 60 seconds"));
        }
      }, 60000);

      createProcess.on("close", (code) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);

        if (code === 0) {
          logInfo("Virtual environment created successfully");
          resolve();
        } else {
          const errorMessage = `Virtual environment creation failed with code ${code}. ${stderrOutput.trim()}${stdoutOutput ? `\n${stdoutOutput.trim()}` : ""}`;
          logError(errorMessage);
          reject(new Error(errorMessage));
        }
      });

      createProcess.on("error", (error) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);
        logError(`Failed to create virtual environment: ${error.message}`);
        reject(error);
      });
    });
  }

  /**
   * Install dependencies in the virtual environment
   */
  async installDependencies(venvPythonPath) {
    return new Promise((resolve, reject) => {
      // Verify Python executable exists
      if (!fs.existsSync(venvPythonPath)) {
        const errorMessage = `Python executable not found: ${venvPythonPath}`;
        logError(errorMessage);
        reject(new Error(errorMessage));
        return;
      }

      // Verify Python executable is executable
      try {
        execSync(`"${venvPythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
      } catch (error) {
        const errorMessage = `Python executable is not executable: ${venvPythonPath}. ${error.message}`;
        logError(errorMessage);
        reject(new Error(errorMessage));
        return;
      }

      const serverDir = getServerDir();
      const requirementsPath = path.join(serverDir, "requirements.txt");

      if (!fs.existsSync(requirementsPath)) {
        logWarn(`requirements.txt not found at: ${requirementsPath}`);
        resolve(); // Not an error, just skip installation
        return;
      }

      logInfo(`Installing dependencies from: ${requirementsPath}`);
      let stdoutOutput = "";
      let stderrOutput = "";
      let isResolved = false;

      const installProcess = spawn(venvPythonPath, ["-m", "pip", "install", "-r", requirementsPath], {
        cwd: serverDir,
        stdio: ["ignore", "pipe", "pipe"],
        shell: true,
        env: {
          ...process.env,
          PYTHONUNBUFFERED: "1",
        },
      });

      installProcess.stdout.on("data", (data) => {
        const message = data.toString();
        stdoutOutput += message;
        this.addLog("stdout", message);
        logDebug(`[pip install stdout] ${message.trim()}`);
      });

      installProcess.stderr.on("data", (data) => {
        const message = data.toString();
        stderrOutput += message;
        this.addLog("stderr", message);
        logDebug(`[pip install stderr] ${message.trim()}`);
      });

      // Timeout after 300 seconds
      const timeout = setTimeout(() => {
        if (!isResolved) {
          isResolved = true;
          installProcess.kill();
          logError("Dependency installation timed out");
          reject(new Error("Dependency installation timed out after 300 seconds"));
        }
      }, 300000);

      installProcess.on("close", (code) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);

        if (code === 0) {
          logInfo("Dependencies installed successfully");
          resolve();
        } else {
          const errorMessage = `Dependency installation failed with code ${code}. ${stderrOutput.trim()}${stdoutOutput ? `\n${stdoutOutput.trim()}` : ""}`;
          logError(errorMessage);
          reject(new Error(errorMessage));
        }
      });

      installProcess.on("error", (error) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);
        logError(`Failed to install dependencies: ${error.message}`);
        reject(error);
      });
    });
  }

  /**
   * Wait for file to exist with retries
   */
  async waitForFile(filePath, maxRetries = 10, intervalMs = 500) {
    for (let i = 0; i < maxRetries; i++) {
      if (fs.existsSync(filePath)) {
        return true;
      }
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
    return false;
  }

  /**
   * Verify virtual environment and ensure it exists
   * @returns {Promise<boolean>} Returns true if virtual environment was just created, false if it already existed
   */
  async ensureVirtualEnvironment() {
    const venvDir = getVenvDir();
    const venvPythonPath = getVenvPythonPath();
    let wasCreated = false;

    // Check if virtual environment directory exists
    if (!fs.existsSync(venvDir)) {
      logInfo("Virtual environment does not exist. Creating...");
      const systemPythonPath = await this.findSystemPython();
      await this.createVirtualEnvironment(systemPythonPath, venvDir);
      wasCreated = true;
      
      // Wait for Python executable to be created
      logDebug("Waiting for virtual environment Python executable to be created...");
      const pythonExists = await this.waitForFile(venvPythonPath);
      if (!pythonExists) {
        throw new Error(`Virtual environment Python executable not found after creation: ${venvPythonPath}`);
      }
      
      // Install dependencies after creating virtual environment
      await this.installDependencies(venvPythonPath);
      return wasCreated;
    }

    // Check if Python executable exists in virtual environment
    if (!fs.existsSync(venvPythonPath)) {
      logWarn("Virtual environment Python executable not found. Recreating virtual environment...");
      // Remove corrupted virtual environment
      try {
        fs.rmSync(venvDir, { recursive: true, force: true });
        logInfo("Removed corrupted virtual environment");
      } catch (error) {
        logError(`Failed to remove corrupted virtual environment: ${error.message}`);
        throw new Error(`Failed to remove corrupted virtual environment: ${error.message}`);
      }
      // Recreate virtual environment
      const systemPythonPath = await this.findSystemPython();
      await this.createVirtualEnvironment(systemPythonPath, venvDir);
      wasCreated = true;
      
      // Wait for Python executable to be created
      logDebug("Waiting for virtual environment Python executable to be created...");
      const pythonExists = await this.waitForFile(venvPythonPath);
      if (!pythonExists) {
        throw new Error(`Virtual environment Python executable not found after recreation: ${venvPythonPath}`);
      }
      
      await this.installDependencies(venvPythonPath);
      return wasCreated;
    }

    // Verify Python executable works
    try {
      execSync(`"${venvPythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
      logInfo("Virtual environment is valid");
      return wasCreated;
    } catch {
      logWarn("Virtual environment Python executable is not working. Recreating...");
      // Remove corrupted virtual environment
      try {
        fs.rmSync(venvDir, { recursive: true, force: true });
        logInfo("Removed corrupted virtual environment");
      } catch (rmError) {
        logError(`Failed to remove corrupted virtual environment: ${rmError.message}`);
        throw new Error(`Failed to remove corrupted virtual environment: ${rmError.message}`);
      }
      // Recreate virtual environment
      const systemPythonPath = await this.findSystemPython();
      await this.createVirtualEnvironment(systemPythonPath, venvDir);
      wasCreated = true;
      
      // Wait for Python executable to be created
      logDebug("Waiting for virtual environment Python executable to be created...");
      const pythonExists = await this.waitForFile(venvPythonPath);
      if (!pythonExists) {
        throw new Error(`Virtual environment Python executable not found after recreation: ${venvPythonPath}`);
      }
      
      await this.installDependencies(venvPythonPath);
      return wasCreated;
    }
  }

  /**
   * Check if marimo is installed
   */
  async checkMarimoInstalled(pythonPath) {
    return new Promise((resolve) => {
      let stderrOutput = "";
      let isResolved = false;
      const checkProcess = spawn(pythonPath, ["-m", "marimo", "--version"], {
        stdio: ["ignore", "pipe", "pipe"],
        shell: true, // Use shell PATH on Windows
      });

      // Collect stdout (version info)
      checkProcess.stdout.on("data", (data) => {
        logDebug(`[marimo version check stdout] ${data.toString().trim()}`);
      });

      // Collect stderr (error messages)
      checkProcess.stderr.on("data", (data) => {
        const message = data.toString();
        stderrOutput += message;
        logDebug(`[marimo version check stderr] ${message.trim()}`);
      });

      // Timeout after 5 seconds
      const timeout = setTimeout(() => {
        if (!isResolved) {
          isResolved = true;
          checkProcess.kill();
          logWarn("marimo version check timed out");
          resolve(false);
        }
      }, 5000);

      checkProcess.on("close", (code) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);

        if (code === 0) {
          logInfo("marimo is installed");
          resolve(true);
        } else {
          logWarn(`marimo version check failed with code ${code}`);
          if (stderrOutput) {
            logDebug(`marimo version check stderr: ${stderrOutput.trim()}`);
          }
          resolve(false);
        }
      });

      checkProcess.on("error", (error) => {
        if (isResolved) {
          return;
        }
        isResolved = true;
        clearTimeout(timeout);
        logError(`Failed to check marimo installation: ${error.message}`);
        resolve(false);
      });
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

      // Ensure virtual environment exists and is valid
      // This will create the virtual environment if it doesn't exist (using findSystemPython)
      // If it already exists, it will skip system Python search and use the existing venv
      const venvWasCreated = await this.ensureVirtualEnvironment();

      // Get Python executable from virtual environment
      // After ensureVirtualEnvironment(), the venv should always exist and be valid
      const venvPythonPath = getVenvPythonPath();
      if (!fs.existsSync(venvPythonPath)) {
        throw new Error(`Virtual environment Python executable not found: ${venvPythonPath}. This should not happen after ensureVirtualEnvironment().`);
      }

      // Verify it works
      try {
        execSync(`"${venvPythonPath}" --version`, { encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] });
        this.pythonPath = venvPythonPath;
        logInfo(`Using Python from virtual environment: ${venvPythonPath}`);
      } catch (error) {
        throw new Error(`Virtual environment Python executable is not working: ${venvPythonPath}. ${error.message}`);
      }

      if (!this.pythonPath) {
        throw new Error("Python executable not found");
      }

      // Check if marimo is installed
      const marimoInstalled = await this.checkMarimoInstalled(this.pythonPath);
      if (!marimoInstalled) {
        // If virtual environment was just created, dependencies should already be installed
        if (venvWasCreated) {
          logWarn("marimo is not installed even after virtual environment creation. This may indicate an installation issue.");
          throw new Error("marimo installation failed during virtual environment setup. Please check the logs.");
        }
        
        logInfo("marimo is not installed. Installing dependencies...");
        // Install dependencies (which includes marimo)
        await this.installDependencies(this.pythonPath);
        // Verify installation
        const marimoInstalledAfter = await this.checkMarimoInstalled(this.pythonPath);
        if (!marimoInstalledAfter) {
          logError("marimo installation failed. Please check the logs.");
          throw new Error("marimo installation failed. Please check the logs.");
        }
      }

      // Find available port
      this.port = await this.findAvailablePort();
      logInfo(`Using port: ${this.port}`);

      // Start the server process
      const serverDir = getServerDir();
      const serverUrl = `http://127.0.0.1:${this.port}`;
      this.serverURL = serverUrl;

      // Create a temporary notebook file if it doesn't exist
      const notebookPath = path.join(serverDir, "backcast.py");
      if (!fs.existsSync(notebookPath)) {
        logInfo(`Creating default notebook file: ${notebookPath}`);
        fs.writeFileSync(notebookPath, "# Backcast Notebook\n\n", "utf-8");
      }

      const args = [
        "-m",
        "marimo",
        "edit",
        "backcast.py",
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
        shell: true, // Use shell PATH on Windows to find python.exe
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

