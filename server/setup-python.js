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
import AdmZip from "adm-zip";
import { createWriteStream } from "fs";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
// const projectRoot = path.resolve(__dirname, "..");
const venvPath = path.join(__dirname, "python-env");
const requirementsPath = path.join(__dirname, "requirements.txt");
const portablePythonPath = path.join(__dirname, "python-portable");

console.log("=".repeat(60));
console.log("Python Virtual Environment Setup");
console.log("=".repeat(60));
console.log();

/**
 * Download portable Python
 */
async function downloadPortablePython() {
  // Use Python 3.12.7 embeddable version
  const pythonVersion = "3.12.7";
  const pythonUrl = `https://www.python.org/ftp/python/${pythonVersion}/python-${pythonVersion}-embed-amd64.zip`;
  const zipPath = path.join(__dirname, `python-${pythonVersion}-embed-amd64.zip`);

  console.log("\n→ Downloading portable Python...");
  console.log(`  Version: ${pythonVersion}`);
  console.log(`  URL: ${pythonUrl}`);

  // Remove existing ZIP file if it exists (in case of previous failed download)
  if (fs.existsSync(zipPath)) {
    console.log("  Removing existing ZIP file...");
    fs.unlinkSync(zipPath);
  }

  // Remove existing portable Python directory if it exists
  if (fs.existsSync(portablePythonPath)) {
    console.log("  Removing existing portable Python directory...");
    fs.rmSync(portablePythonPath, { recursive: true, force: true });
  }

  try {
    const response = await fetch(pythonUrl);
    if (!response.ok) {
      throw new Error(`Failed to download Python: ${response.status} ${response.statusText}`);
    }

    const contentLength = response.headers.get("content-length");
    const totalBytes = contentLength ? parseInt(contentLength, 10) : 0;

    const fileStream = createWriteStream(zipPath);
    const reader = response.body.getReader();
    let downloadedBytes = 0;

    // Download with progress tracking
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      await new Promise((resolve, reject) => {
        fileStream.write(Buffer.from(value), (error) => {
          if (error) reject(error);
          else resolve();
        });
      });

      downloadedBytes += value.length;

      if (totalBytes > 0) {
        const percent = ((downloadedBytes / totalBytes) * 100).toFixed(1);
        process.stdout.write(`\r  Progress: ${percent}% (${(downloadedBytes / 1024 / 1024).toFixed(2)} MB / ${(totalBytes / 1024 / 1024).toFixed(2)} MB)`);
      }
    }

    // Wait for file stream to finish writing
    await new Promise((resolve, reject) => {
      fileStream.end((error) => {
        if (error) reject(error);
        else resolve();
      });
    });

    console.log("\n✓ Download completed");

    // Verify file was downloaded completely
    const stats = fs.statSync(zipPath);
    if (totalBytes > 0 && stats.size !== totalBytes) {
      throw new Error(`Download incomplete: expected ${totalBytes} bytes, got ${stats.size} bytes`);
    }

    // Extract ZIP file
    console.log("\n→ Extracting Python...");
    
    // Verify ZIP file is valid before extracting
    if (!fs.existsSync(zipPath) || stats.size === 0) {
      throw new Error("ZIP file is missing or empty");
    }

    const zip = new AdmZip(zipPath);
    zip.extractAllTo(portablePythonPath, true);
    console.log(`✓ Python extracted to: ${portablePythonPath}`);

    // Clean up ZIP file
    fs.unlinkSync(zipPath);

    // Configure python._pth for embeddable Python to enable standard library
    const pythonPthPath = path.join(portablePythonPath, "python._pth");
    
    // Create python._pth if it doesn't exist
    if (!fs.existsSync(pythonPthPath)) {
      // Create necessary directories
      if (!fs.existsSync(path.join(portablePythonPath, "Scripts"))) {
        fs.mkdirSync(path.join(portablePythonPath, "Scripts"), { recursive: true });
      }
      if (!fs.existsSync(path.join(portablePythonPath, "Lib", "site-packages"))) {
        fs.mkdirSync(path.join(portablePythonPath, "Lib", "site-packages"), { recursive: true });
      }
      
      // Create python._pth file with relative paths
      // In embeddable Python, paths in python._pth are relative to python.exe location
      let pthContent = ".\n";
      if (fs.existsSync(path.join(portablePythonPath, "python312.zip"))) {
        pthContent += "python312.zip\n";
      }
      pthContent += "Lib\n";
      pthContent += "Lib\\site-packages\n";
      pthContent += "Scripts\n";
      pthContent += "import site\n";
      
      fs.writeFileSync(pythonPthPath, pthContent);
      console.log("  Created python._pth file");
    }
    
    if (fs.existsSync(pythonPthPath)) {
      let pthContent = fs.readFileSync(pythonPthPath, "utf-8");
      
      // Rebuild the pth file with relative paths
      // In embeddable Python, paths in python._pth are relative to python.exe location
      const lines = pthContent.split("\n");
      const newLines = [];
      let hasPythonDir = false;
      let hasStdlibZip = false;
      let hasLibDir = false;
      let hasLibSitePackagesDir = false;
      let hasScriptsDir = false;
      let hasImportSite = false;
      
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === "." || trimmed === portablePythonPath.replace(/\\/g, "/") || trimmed === portablePythonPath.replace(/\\/g, "\\")) {
          hasPythonDir = true;
          newLines.push(".");
        } else if (trimmed === "python312.zip" || trimmed.includes("python312.zip")) {
          hasStdlibZip = true;
          newLines.push("python312.zip");
        } else if (trimmed === "Lib" || trimmed === "Lib\\site-packages" || trimmed === "Lib/site-packages") {
          if (trimmed === "Lib") {
            hasLibDir = true;
            newLines.push("Lib");
          } else {
            hasLibSitePackagesDir = true;
            newLines.push("Lib\\site-packages");
          }
        } else if (trimmed === "Scripts" || trimmed.includes("Scripts")) {
          hasScriptsDir = true;
          newLines.push("Scripts");
        } else if (trimmed === "import site") {
          hasImportSite = true;
          newLines.push("import site");
        } else if (trimmed.startsWith("#") || trimmed === "") {
          // Keep comments and empty lines
          newLines.push(line);
        } else if (!trimmed.includes("import") && trimmed.length > 0) {
          // Keep other paths (convert to relative if needed)
          if (path.isAbsolute(trimmed)) {
            // Convert absolute path to relative
            const relPath = path.relative(portablePythonPath, trimmed).replace(/\\/g, "\\");
            newLines.push(relPath);
          } else {
            newLines.push(line);
          }
        }
      }
      
      // Add missing paths in correct order
      if (!hasPythonDir) {
        newLines.unshift(".");
      }
      if (!hasStdlibZip && fs.existsSync(path.join(portablePythonPath, "python312.zip"))) {
        const pythonDirIndex = newLines.indexOf(".");
        if (pythonDirIndex >= 0) {
          newLines.splice(pythonDirIndex + 1, 0, "python312.zip");
        } else {
          newLines.unshift("python312.zip");
        }
      }
      if (!hasLibDir) {
        newLines.push("Lib");
      }
      if (!hasLibSitePackagesDir) {
        newLines.push("Lib\\site-packages");
      }
      if (!hasScriptsDir) {
        newLines.push("Scripts");
      }
      if (!hasImportSite) {
        newLines.push("import site");
      }
      
      pthContent = newLines.join("\n");
      fs.writeFileSync(pythonPthPath, pthContent);
      console.log("  Configured python._pth for standard library access");
    }

    // Return path to python.exe
    const pythonExePath = path.join(portablePythonPath, "python.exe");
    return pythonExePath;
  } catch (error) {
    // Clean up on error
    if (fs.existsSync(zipPath)) {
      fs.unlinkSync(zipPath);
    }
    throw error;
  }
}

/**
 * Reconfigure python._pth after pip installation
 */
function reconfigurePythonPth(pythonCmd) {
  const pythonDir = path.dirname(pythonCmd);
  const pythonPthPath = path.join(pythonDir, "python._pth");
  
  // Ensure Scripts directory exists
  if (!fs.existsSync(path.join(pythonDir, "Scripts"))) {
    fs.mkdirSync(path.join(pythonDir, "Scripts"), { recursive: true });
  }
  
  // Ensure Lib/site-packages directory exists
  if (!fs.existsSync(path.join(pythonDir, "Lib", "site-packages"))) {
    fs.mkdirSync(path.join(pythonDir, "Lib", "site-packages"), { recursive: true });
  }
  
  if (fs.existsSync(pythonPthPath)) {
    let pthContent = fs.readFileSync(pythonPthPath, "utf-8");
    
    // Rebuild pth content with relative paths
    const lines = pthContent.split("\n");
    const newLines = [];
    let hasPythonDir = false;
    let hasStdlibZip = false;
    let hasLibDir = false;
    let hasLibSitePackagesDir = false;
    let hasScriptsDir = false;
    let hasImportSite = false;
    
    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed === "." || trimmed === pythonDir.replace(/\\/g, "/") || trimmed === pythonDir.replace(/\\/g, "\\")) {
        hasPythonDir = true;
        newLines.push(".");
      } else if (trimmed === "python312.zip" || trimmed.includes("python312.zip")) {
        hasStdlibZip = true;
        newLines.push("python312.zip");
      } else if (trimmed === "Lib" || trimmed === "Lib\\site-packages" || trimmed === "Lib/site-packages") {
        if (trimmed === "Lib") {
          hasLibDir = true;
          newLines.push("Lib");
        } else {
          hasLibSitePackagesDir = true;
          newLines.push("Lib\\site-packages");
        }
      } else if (trimmed === "Scripts" || trimmed.includes("Scripts")) {
        hasScriptsDir = true;
        newLines.push("Scripts");
      } else if (trimmed === "import site") {
        hasImportSite = true;
        newLines.push("import site");
      } else if (trimmed.startsWith("#") || trimmed === "") {
        // Keep comments and empty lines
        newLines.push(line);
      } else if (!trimmed.includes("import") && trimmed.length > 0) {
        // Convert absolute paths to relative
        if (path.isAbsolute(trimmed)) {
          const relPath = path.relative(pythonDir, trimmed).replace(/\\/g, "\\");
          newLines.push(relPath);
        } else {
          newLines.push(line);
        }
      }
    }
    
    // Add missing paths in correct order
    if (!hasPythonDir) {
      newLines.unshift(".");
    }
    if (!hasStdlibZip && fs.existsSync(path.join(pythonDir, "python312.zip"))) {
      const pythonDirIndex = newLines.indexOf(".");
      if (pythonDirIndex >= 0) {
        newLines.splice(pythonDirIndex + 1, 0, "python312.zip");
      } else {
        newLines.unshift("python312.zip");
      }
    }
    if (!hasLibDir) {
      newLines.push("Lib");
    }
    if (!hasLibSitePackagesDir) {
      newLines.push("Lib\\site-packages");
    }
    if (!hasScriptsDir) {
      newLines.push("Scripts");
    }
    if (!hasImportSite) {
      newLines.push("import site");
    }
    
    pthContent = newLines.join("\n");
    fs.writeFileSync(pythonPthPath, pthContent);
    console.log("  Updated python._pth to include Scripts and site-packages directories");
  } else {
    // Create python._pth if it doesn't exist
    let pthContent = ".\n";
    if (fs.existsSync(path.join(pythonDir, "python312.zip"))) {
      pthContent += "python312.zip\n";
    }
    pthContent += "Lib\n";
    pthContent += "Lib\\site-packages\n";
    pthContent += "Scripts\n";
    pthContent += "import site\n";
    fs.writeFileSync(pythonPthPath, pthContent);
    console.log("  Created python._pth file");
  }
}

/**
 * Install pip using ensurepip
 */
function installPip(pythonCmd) {
  return new Promise((resolve, reject) => {
    console.log("\n→ Installing pip...");
    
    // Try ensurepip first (built-in module)
    const ensurepipProcess = spawn(pythonCmd, ["-m", "ensurepip", "--upgrade"], {
      stdio: "inherit"
    });
    
    ensurepipProcess.on("close", (code) => {
      if (code === 0) {
        console.log("✓ pip installed");
        resolve();
      } else {
        // If ensurepip failed, try downloading get-pip.py
        console.log("  ensurepip not available, trying get-pip.py...");
        installPipWithGetPip(pythonCmd)
          .then(resolve)
          .catch(reject);
      }
    });
    
    ensurepipProcess.on("error", (error) => {
      console.log("  ensurepip not available, trying get-pip.py...");
      installPipWithGetPip(pythonCmd)
        .then(resolve)
        .catch(reject);
    });
  });
}

/**
 * Install pip using get-pip.py
 */
async function installPipWithGetPip(pythonCmd) {
  return new Promise((resolve, reject) => {
    console.log("  Downloading get-pip.py...");
    
    // Download get-pip.py
    const getPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    const getPipPath = path.join(__dirname, "get-pip.py");
    
    fetch(getPipUrl)
      .then(response => {
        if (!response.ok) {
          throw new Error(`Failed to download get-pip.py: ${response.status}`);
        }
        return response.text();
      })
      .then(content => {
        fs.writeFileSync(getPipPath, content);
        console.log("  Downloaded get-pip.py");
        
        // Run get-pip.py
        const pipProcess = spawn(pythonCmd, [getPipPath], {
          stdio: "inherit"
        });
        
        pipProcess.on("close", (code) => {
          // Clean up get-pip.py
          if (fs.existsSync(getPipPath)) {
            fs.unlinkSync(getPipPath);
          }
          
          if (code === 0) {
            console.log("✓ pip installed");
            // Reconfigure python._pth after pip installation
            reconfigurePythonPth(pythonCmd);
            resolve();
          } else {
            console.error(`✗ Failed to install pip (exit code: ${code})`);
            reject(new Error("pip installation failed"));
          }
        });
        
        pipProcess.on("error", (error) => {
          if (fs.existsSync(getPipPath)) {
            fs.unlinkSync(getPipPath);
          }
          console.error(`✗ Error: ${error.message}`);
          reject(error);
        });
      })
      .catch(reject);
  });
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

    // Try venv first, fall back to virtualenv if venv is not available
    const pythonArgs = ["-m", "venv", venvPath];
    const venvProcess = spawn(pythonCmd, pythonArgs, {
      stdio: "inherit",
      shell: path.isAbsolute(pythonCmd) ? false : undefined
    });

    venvProcess.on("close", (code) => {
      if (code === 0) {
        console.log("✓ Virtual environment created");
        resolve();
      } else {
        // If venv failed, try using virtualenv
        console.log("  venv module not available, trying virtualenv...");
        installVirtualenvAndCreate(pythonCmd)
          .then(resolve)
          .catch(reject);
      }
    });

    venvProcess.on("error", (error) => {
      console.log("  venv module not available, trying virtualenv...");
      installVirtualenvAndCreate(pythonCmd)
        .then(resolve)
        .catch(reject);
    });
  });
}

/**
 * Install virtualenv and create virtual environment
 */
function installVirtualenvAndCreate(pythonCmd) {
  return new Promise((resolve, reject) => {
    console.log("  Installing virtualenv...");
    
    // Reconfigure python._pth first to ensure pip is accessible
    reconfigurePythonPth(pythonCmd);
    
    // Use python -m pip (reconfigurePythonPth should have fixed python._pth)
    // We need to use a new process for python._pth changes to take effect
    const installProcess = spawn(pythonCmd, ["-m", "pip", "install", "virtualenv"], {
      stdio: "inherit",
      env: {
        ...process.env,
        // Ensure Python can find its modules
        PYTHONPATH: path.dirname(pythonCmd)
      }
    });
    
    installProcess.on("close", (code) => {
      if (code === 0) {
        console.log("  ✓ virtualenv installed");
        // Create virtual environment using virtualenv
        const venvProcess = spawn(pythonCmd, ["-m", "virtualenv", venvPath], {
          stdio: "inherit"
        });
        
        venvProcess.on("close", (venvCode) => {
          if (venvCode === 0) {
            console.log("✓ Virtual environment created");
            resolve();
          } else {
            console.error(`✗ Failed to create virtual environment (exit code: ${venvCode})`);
            reject(new Error("Virtual environment creation failed"));
          }
        });
        
        venvProcess.on("error", (error) => {
          console.error(`✗ Error: ${error.message}`);
          reject(error);
        });
      } else {
        console.error(`✗ Failed to install virtualenv (exit code: ${code})`);
        reject(new Error("virtualenv installation failed"));
      }
    });
    
    installProcess.on("error", (error) => {
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
    const pythonCmd = await downloadPortablePython();

    // Install pip first (needed for embeddable Python)
    await installPip(pythonCmd);

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