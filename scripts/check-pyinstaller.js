/* Copyright 2026 Marimo. All rights reserved. */
import { execSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dirname } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const logPath = join(__dirname, '..', '.cursor', 'debug.log');

// #region agent log
const logEntry = {
  sessionId: 'debug-session',
  runId: 'check-pyinstaller',
  hypothesisId: 'A',
  location: 'scripts/check-pyinstaller.js:15',
  message: 'Starting PyInstaller diagnostic',
  data: { timestamp: new Date().toISOString() },
  timestamp: Date.now()
};
try {
  fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(logEntry)
  }).catch(() => {});
} catch (e) {}
// #endregion

const diagnostics = [];

try {
  // Check which Python is being used
  try {
    const pythonPath = execSync('where python', { encoding: 'utf-8', stdio: 'pipe' }).trim();
    diagnostics.push({ check: 'python_path', value: pythonPath });
    
    // #region agent log
    const logEntry2 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'B',
      location: 'scripts/check-pyinstaller.js:28',
      message: 'Python path found',
      data: { pythonPath },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry2)
      }).catch(() => {});
    } catch (e) {}
    // #endregion
  } catch (e) {
    diagnostics.push({ check: 'python_path', error: e.message });
  }

  // Check Python version
  try {
    const pythonVersion = execSync('python --version', { encoding: 'utf-8', stdio: 'pipe' }).trim();
    diagnostics.push({ check: 'python_version', value: pythonVersion });
    
    // #region agent log
    const logEntry3 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'C',
      location: 'scripts/check-pyinstaller.js:48',
      message: 'Python version',
      data: { pythonVersion },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry3)
      }).catch(() => {});
    } catch (e) {}
    // #endregion
  } catch (e) {
    diagnostics.push({ check: 'python_version', error: e.message });
  }

  // Try to import PyInstaller (case-sensitive)
  try {
    const pyinstallerCheck = execSync('python -m PyInstaller --version', { encoding: 'utf-8', stdio: 'pipe' }).trim();
    diagnostics.push({ check: 'pyinstaller_import_uppercase', value: pyinstallerCheck, success: true });
    
    // #region agent log
    const logEntry4 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'D',
      location: 'scripts/check-pyinstaller.js:66',
      message: 'PyInstaller (uppercase) import successful',
      data: { version: pyinstallerCheck },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry4)
      }).catch(() => {});
    } catch (e) {}
    // #endregion
  } catch (e) {
    diagnostics.push({ check: 'pyinstaller_import_uppercase', error: e.message, success: false });
    
    // #region agent log
    const logEntry5 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'D',
      location: 'scripts/check-pyinstaller.js:78',
      message: 'PyInstaller (uppercase) import failed',
      data: { error: e.message, stdout: e.stdout, stderr: e.stderr },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry5)
      }).catch(() => {});
    } catch (e2) {}
    // #endregion

    // Try lowercase pyinstaller
    try {
      const pyinstallerLower = execSync('python -m pyinstaller --version', { encoding: 'utf-8', stdio: 'pipe' }).trim();
      diagnostics.push({ check: 'pyinstaller_import_lowercase', value: pyinstallerLower, success: true });
      
      // #region agent log
      const logEntry6 = {
        sessionId: 'debug-session',
        runId: 'check-pyinstaller',
        hypothesisId: 'D',
        location: 'scripts/check-pyinstaller.js:92',
        message: 'pyinstaller (lowercase) import successful',
        data: { version: pyinstallerLower },
        timestamp: Date.now()
      };
      try {
        fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(logEntry6)
        }).catch(() => {});
      } catch (e3) {}
      // #endregion
    } catch (e2) {
      diagnostics.push({ check: 'pyinstaller_import_lowercase', error: e2.message, success: false });
    }
  }

  // Check if pyinstaller is installed via pip list
  try {
    const pipList = execSync('python -m pip list | findstr /i pyinstaller', { encoding: 'utf-8', stdio: 'pipe' }).trim();
    diagnostics.push({ check: 'pip_list_pyinstaller', value: pipList || 'not found' });
    
    // #region agent log
    const logEntry7 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'A',
      location: 'scripts/check-pyinstaller.js:110',
      message: 'pip list pyinstaller check',
      data: { pipList: pipList || 'not found' },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry7)
      }).catch(() => {});
    } catch (e) {}
    // #endregion
  } catch (e) {
    diagnostics.push({ check: 'pip_list_pyinstaller', error: e.message });
  }

  // Check if requirements.txt exists and what it says
  try {
    const fs = await import('node:fs/promises');
    const requirementsPath = join(__dirname, '..', 'requirements.txt');
    const requirementsContent = await fs.readFile(requirementsPath, 'utf-8');
    const hasPyinstaller = requirementsContent.toLowerCase().includes('pyinstaller');
    diagnostics.push({ check: 'requirements_txt_pyinstaller', value: hasPyinstaller, content: requirementsContent.split('\n').filter(l => l.toLowerCase().includes('pyinstaller')) });
    
    // #region agent log
    const logEntry8 = {
      sessionId: 'debug-session',
      runId: 'check-pyinstaller',
      hypothesisId: 'A',
      location: 'scripts/check-pyinstaller.js:126',
      message: 'requirements.txt check',
      data: { hasPyinstaller, lines: requirementsContent.split('\n').filter(l => l.toLowerCase().includes('pyinstaller')) },
      timestamp: Date.now()
    };
    try {
      fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(logEntry8)
      }).catch(() => {});
    } catch (e) {}
    // #endregion
  } catch (e) {
    diagnostics.push({ check: 'requirements_txt_pyinstaller', error: e.message });
  }

} catch (error) {
  diagnostics.push({ check: 'general_error', error: error.message });
}

// #region agent log
const logEntryFinal = {
  sessionId: 'debug-session',
  runId: 'check-pyinstaller',
  hypothesisId: 'ALL',
  location: 'scripts/check-pyinstaller.js:145',
  message: 'Diagnostics complete',
  data: { diagnostics },
  timestamp: Date.now()
};
try {
  fetch('http://127.0.0.1:7245/ingest/1109daad-ed1a-4db3-be85-23ab16a87547', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(logEntryFinal)
  }).catch(() => {});
} catch (e) {}
// #endregion

console.log('PyInstaller Diagnostic Results:');
console.log(JSON.stringify(diagnostics, null, 2));

