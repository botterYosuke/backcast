#!/usr/bin/env node

/**
 * Python仮想環境セットアップスクリプト
 * 
 * このスクリプトは開発時やビルド時に実行され、
 * プロジェクト専用のPython仮想環境を作成します。
 * 
 * 使用方法:
 *   node scripts/setup-python.js
 */

import { spawn, execSync } from "child_process";
import path from "path";
import fs from "fs";
import { fileURLToPath } from "url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
// const projectRoot = path.resolve(__dirname, "..");
const venvPath = path.join(__dirname, "python-env");
const requirementsPath = path.join(__dirname, "requirements.txt");

console.log("=".repeat(60));
console.log("Python Virtual Environment Setup");
console.log("=".repeat(60));
console.log();

/**
 * Find system Python
 */
function findPython() {
  const candidates = process.platform === "win32" 
    ? ["python", "python3", "py"]
    : ["python3", "python"];

  for (const cmd of candidates) {
    try {
      const version = execSync(`${cmd} --version`, { 
        encoding: "utf-8",
        stdio: ["ignore", "pipe", "pipe"]
      });
      
      console.log(`✓ Found Python: ${cmd}`);
      console.log(`  Version: ${version.trim()}`);
      return cmd;
    } catch (error) {
      // Continue to next candidate
    }
  }

  console.error("✗ Python not found!");
  console.error("\nPlease install Python 3.8 or later from:");
  console.error("  https://www.python.org/downloads/");
  console.error("\nMake sure to check 'Add Python to PATH' during installation.");
  process.exit(1);
}

/**
 * Create virtual environment
 */
function createVenv(pythonCmd) {
  return new Promise((resolve, reject) => {
    console.log("\n→ Creating virtual environment...");
    console.log(`  Location: ${venvPath}`);

    // Remove existing venv if it exists
    if (fs.existsSync(venvPath)) {
      console.log("  Removing existing virtual environment...");
      fs.rmSync(venvPath, { recursive: true, force: true });
    }

    const venvProcess = spawn(pythonCmd, ["-m", "venv", venvPath], {
      stdio: "inherit"
    });

    venvProcess.on("close", (code) => {
      if (code === 0) {
        console.log("✓ Virtual environment created");
        resolve();
      } else {
        console.error(`✗ Failed to create virtual environment (exit code: ${code})`);
        reject(new Error("Virtual environment creation failed"));
      }
    });

    venvProcess.on("error", (error) => {
      console.error(`✗ Error: ${error.message}`);
      reject(error);
    });
  });
}

/**
 * Install packages
 */
function installPackages() {
  return new Promise((resolve, reject) => {
    console.log("\n→ Installing packages...");

    const venvPythonPath = process.platform === "win32"
      ? path.join(venvPath, "Scripts", "python.exe")
      : path.join(venvPath, "bin", "python");

    let args;
    if (fs.existsSync(requirementsPath)) {
      console.log(`  From: ${requirementsPath}`);
      args = ["-m", "pip", "install", "-r", requirementsPath];
    } else {
      console.log("  Installing: marimo>=0.8.0");
      args = ["-m", "pip", "install", "marimo>=0.8.0"];
    }

    const installProcess = spawn(venvPythonPath, args, {
      stdio: "inherit"
    });

    installProcess.on("close", (code) => {
      if (code === 0) {
        console.log("✓ Packages installed successfully");
        resolve();
      } else {
        console.error(`✗ Failed to install packages (exit code: ${code})`);
        reject(new Error("Package installation failed"));
      }
    });

    installProcess.on("error", (error) => {
      console.error(`✗ Error: ${error.message}`);
      reject(error);
    });
  });
}

/**
 * Verify installation
 */
function verifyInstallation() {
  return new Promise((resolve, reject) => {
    console.log("\n→ Verifying installation...");

    const venvPythonPath = process.platform === "win32"
      ? path.join(venvPath, "Scripts", "python.exe")
      : path.join(venvPath, "bin", "python");

    const verifyProcess = spawn(venvPythonPath, ["-m", "marimo", "--version"], {
      stdio: ["ignore", "pipe", "pipe"]
    });

    let output = "";

    verifyProcess.stdout.on("data", (data) => {
      output += data.toString();
    });

    verifyProcess.on("close", (code) => {
      if (code === 0) {
        console.log(`✓ marimo verified: ${output.trim()}`);
        resolve();
      } else {
        console.error("✗ marimo verification failed");
        reject(new Error("marimo verification failed"));
      }
    });

    verifyProcess.on("error", (error) => {
      console.error(`✗ Error: ${error.message}`);
      reject(error);
    });
  });
}

/**
 * Main setup function
 */
async function setup() {
  try {
    // Find Python
    const pythonCmd = findPython();

    // Create virtual environment
    await createVenv(pythonCmd);

    // Install packages
    await installPackages();

    // Verify installation
    await verifyInstallation();

    console.log("\n" + "=".repeat(60));
    console.log("✓ Setup completed successfully!");
    console.log("=".repeat(60));
    console.log("\nVirtual environment location:");
    console.log(`  ${venvPath}`);
    console.log("\nYou can now run the application:");
    console.log("  npm start");
    console.log();

  } catch (error) {
    console.error("\n" + "=".repeat(60));
    console.error("✗ Setup failed!");
    console.error("=".repeat(60));
    console.error(`\nError: ${error.message}`);
    console.error("\nPlease check the error message above and try again.");
    console.error("If the problem persists, please report it at:");
    console.error("  https://github.com/your-repo/backcast/issues");
    process.exit(1);
  }
}

// Run setup
setup();