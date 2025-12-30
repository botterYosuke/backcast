#!/usr/bin/env node

/**
 * Pythonランタイムセットアップスクリプト
 * 
 * このスクリプトは開発時やビルド時に実行され、
 * ポータブルPythonをダウンロードしてパッケージを直接インストールします。
 * 
 * 使用方法:
 *   node server/setup-python.js
 */

import { spawn, execSync } from "child_process";
import path from "path";
import fs from "fs";
import { fileURLToPath } from "url";
import AdmZip from "adm-zip";
import { createWriteStream } from "fs";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const pythonRuntimePath = path.join(__dirname, "python-runtime");
const requirementsPath = path.join(__dirname, "requirements.txt");

console.log("=".repeat(60));
console.log("Python Runtime Setup");
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

  // Remove existing Python runtime directory if it exists
  if (fs.existsSync(pythonRuntimePath)) {
    console.log("  Removing existing Python runtime directory...");
    fs.rmSync(pythonRuntimePath, { recursive: true, force: true });
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
    zip.extractAllTo(pythonRuntimePath, true);
    console.log(`✓ Python extracted to: ${pythonRuntimePath}`);

    // Clean up ZIP file
    fs.unlinkSync(zipPath);

    // Configure ALL ._pth files for embeddable Python to enable standard library
    // This is critical for pip and packages to work correctly
    
    // Create necessary directories
    if (!fs.existsSync(path.join(pythonRuntimePath, "Scripts"))) {
      fs.mkdirSync(path.join(pythonRuntimePath, "Scripts"), { recursive: true });
    }
    if (!fs.existsSync(path.join(pythonRuntimePath, "Lib", "site-packages"))) {
      fs.mkdirSync(path.join(pythonRuntimePath, "Lib", "site-packages"), { recursive: true });
    }
    
    // Find all ._pth files (python._pth, python312._pth, etc.)
    const pthFiles = fs.readdirSync(pythonRuntimePath)
      .filter(file => file.endsWith("._pth"))
      .map(file => path.join(pythonRuntimePath, file));
    
    // Also ensure python._pth exists
    const pythonPthPath = path.join(pythonRuntimePath, "python._pth");
    if (!pthFiles.includes(pythonPthPath)) {
      pthFiles.push(pythonPthPath);
    }
    
    // Find stdlib zip file (python312.zip, python311.zip, etc.)
    const stdlibZipFiles = fs.readdirSync(pythonRuntimePath)
      .filter(file => /^python\d+\.zip$/.test(file));
    
    const stdlibZip = stdlibZipFiles.length > 0 ? stdlibZipFiles[0] : "";
    
    // Create correct pth content
    let pthContent = "";
    if (stdlibZip) {
      pthContent += `${stdlibZip}\n`;
    }
    pthContent += `.\n`;
    pthContent += `Lib\n`;
    pthContent += `Lib\\site-packages\n`;
    pthContent += `import site\n`;
    
    // Update ALL ._pth files
    for (const pthFile of pthFiles) {
      fs.writeFileSync(pthFile, pthContent);
    }
    
    console.log(`  Configured ${pthFiles.length} ._pth file(s) for standard library access`);
    if (pthFiles.length > 1) {
      console.log(`  Files: ${pthFiles.map(f => path.basename(f)).join(", ")}`);
    }

    // Return path to python.exe
    const pythonExePath = path.join(pythonRuntimePath, "python.exe");
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
 * Reconfigure ALL ._pth files after pip installation
 * This ensures pip and other modules are accessible
 */
function reconfigurePythonPth(pythonCmd) {
  const pythonDir = path.dirname(pythonCmd);
  
  // Ensure Scripts directory exists
  if (!fs.existsSync(path.join(pythonDir, "Scripts"))) {
    fs.mkdirSync(path.join(pythonDir, "Scripts"), { recursive: true });
  }
  
  // Ensure Lib/site-packages directory exists
  if (!fs.existsSync(path.join(pythonDir, "Lib", "site-packages"))) {
    fs.mkdirSync(path.join(pythonDir, "Lib", "site-packages"), { recursive: true });
  }
  
  // Find all ._pth files
  const pthFiles = fs.readdirSync(pythonDir)
    .filter(file => file.endsWith("._pth"))
    .map(file => path.join(pythonDir, file));
  
  // Also ensure python._pth exists
  const pythonPthPath = path.join(pythonDir, "python._pth");
  if (!pthFiles.includes(pythonPthPath)) {
    pthFiles.push(pythonPthPath);
  }
  
  // Find stdlib zip file
  const stdlibZipFiles = fs.readdirSync(pythonDir)
    .filter(file => /^python\d+\.zip$/.test(file));
  
  const stdlibZip = stdlibZipFiles.length > 0 ? stdlibZipFiles[0] : "";
  
  // Create correct pth content
  let pthContent = "";
  if (stdlibZip) {
    pthContent += `${stdlibZip}\n`;
  }
  pthContent += `.\n`;
  pthContent += `Lib\n`;
  pthContent += `Lib\\site-packages\n`;
  pthContent += `import site\n`;
  
  // Update ALL ._pth files
  for (const pthFile of pthFiles) {
    fs.writeFileSync(pthFile, pthContent);
  }
  
  console.log(`  Updated ${pthFiles.length} ._pth file(s) to include Scripts and site-packages directories`);
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
            
            // Verify pip works by checking version
            const pythonDir = path.dirname(pythonCmd);
            const pipExe = path.join(pythonDir, "Scripts", "pip.exe");
            
            // Wait a moment for file system to sync
            setTimeout(() => {
              if (fs.existsSync(pipExe)) {
                console.log("  Verifying pip installation...");
                const verifyProcess = spawn(pipExe, ["--version"], {
                  stdio: ["ignore", "pipe", "pipe"]
                });
                
                let verifyOutput = "";
                verifyProcess.stdout.on("data", (data) => {
                  verifyOutput += data.toString();
                });
                
                verifyProcess.on("close", (verifyCode) => {
                  if (verifyCode === 0) {
                    console.log(`  ✓ pip verified: ${verifyOutput.trim()}`);
                    resolve();
                  } else {
                    console.error("  ⚠ pip.exe found but verification failed");
                    // Still resolve - pip might work despite verification failure
                    resolve();
                  }
                });
                
                verifyProcess.on("error", () => {
                  console.error("  ⚠ pip.exe verification error");
                  // Still resolve - pip might work
                  resolve();
                });
              } else {
                console.error("  ⚠ pip.exe not found after installation");
                reject(new Error("pip.exe not found after installation"));
              }
            }, 500);
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
 * Create pip configuration file to prevent registry access
 */
function createPipConfig() {
  console.log("  Creating pip configuration...");
  
  // Create pip.ini in python-runtime to prevent registry access
  const pipConfigDir = path.join(pythonRuntimePath, "pip");
  const pipConfigPath = path.join(pipConfigDir, "pip.ini");
  
  if (!fs.existsSync(pipConfigDir)) {
    fs.mkdirSync(pipConfigDir, { recursive: true });
  }
  
  const pipConfig = `[global]
no-cache-dir = true
disable-pip-version-check = true
no-input = true
no-warn-script-location = true
`;
  
  fs.writeFileSync(pipConfigPath, pipConfig);
  console.log(`  ✓ pip.ini created at: ${pipConfigPath}`);
}

/**
 * Install packages directly into Python runtime
 */
function installPackages() {
  return new Promise((resolve, reject) => {
    console.log("\n→ Installing packages...");

    const pythonExePath = path.join(pythonRuntimePath, "python.exe");
    const pipExePath = path.join(pythonRuntimePath, "Scripts", "pip.exe");

    if (!fs.existsSync(pythonExePath)) {
      reject(new Error(`Python executable not found: ${pythonExePath}`));
      return;
    }

    if (!fs.existsSync(pipExePath)) {
      reject(new Error(`pip executable not found: ${pipExePath}`));
      return;
    }

    // Read requirements
    if (!fs.existsSync(requirementsPath)) {
      console.log("  requirements.txt not found, installing default packages...");
      console.log("  Installing: marimo>=0.8.0");
    } else {
      console.log(`  From: ${requirementsPath}`);
    }

    // Create isolated configuration directory to avoid registry access
    const isolatedDir = path.join(pythonRuntimePath, "_isolated_config");
    const appDataPath = path.join(isolatedDir, "AppData");
    const localAppDataPath = path.join(isolatedDir, "LocalAppData");
    const tempPath = path.join(isolatedDir, "Temp");
    
    // Ensure directories exist
    [isolatedDir, appDataPath, localAppDataPath, tempPath].forEach(dir => {
      if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
      }
    });

    console.log("  Installing packages directly into Python runtime...");
    
    // Add Python runtime directory to PATH so DLLs can be found
    const basePath = process.env.PATH || "";
    const newPath = `${pythonRuntimePath};${basePath}`;
    
    // Comprehensive environment variables to avoid registry access
    const env = {
      ...process.env,
      PATH: newPath,
      USERPROFILE: isolatedDir,
      APPDATA: appDataPath,
      LOCALAPPDATA: localAppDataPath,
      TEMP: tempPath,
      TMP: tempPath,
      HOME: isolatedDir,
      PYTHONNOUSERSITE: "1",
      PYTHONIOENCODING: "utf-8",
      PYTHONHOME: "",
      PYTHONPATH: "",
      PIP_NO_CACHE_DIR: "1",
      PIP_DISABLE_PIP_VERSION_CHECK: "1",
      PIP_NO_INPUT: "1",
      PIP_NO_WARN_SCRIPT_LOCATION: "1"
    };

    // Install packages using pip
    const installArgs = ["install", "-r", requirementsPath];
    if (!fs.existsSync(requirementsPath)) {
      installArgs.splice(1, 2, "marimo>=0.8.0");
    }

    const installProcess = spawn(pipExePath, installArgs, {
      cwd: __dirname,
      stdio: "inherit",
      env: env
    });

    installProcess.on("close", (code) => {
      if (code === 0) {
        console.log("✓ Packages installed successfully");
        createPipConfig();
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

    const pythonExePath = path.join(pythonRuntimePath, "python.exe");

    const verifyProcess = spawn(pythonExePath, ["-m", "marimo", "--version"], {
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
    // Download portable Python
    const pythonCmd = await downloadPortablePython();

    // Install pip first (needed for embeddable Python)
    await installPip(pythonCmd);

    // Install packages directly into Python runtime
    await installPackages();

    // Verify installation
    await verifyInstallation();

    console.log("\n" + "=".repeat(60));
    console.log("✓ Setup completed successfully!");
    console.log("=".repeat(60));
    console.log("\nPython runtime location:");
    console.log(`  ${pythonRuntimePath}`);
    console.log("\nYou can now run the application:");
    console.log("  npm start");
    console.log();

  } catch (error) {
    console.error("\n" + "=".repeat(60));
    console.error("✗ Setup failed!");
    console.error("=".repeat(60));
    console.error(`\nError: ${error.message}`);
    console.error("\nPlease check the error message above and try again.");
    process.exit(1);
  }
}

// Run setup
setup();
