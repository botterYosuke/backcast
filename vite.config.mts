/* Copyright 2026 Marimo. All rights reserved. */

import { codecovVitePlugin } from "@codecov/vite-plugin";
import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";
import topLevelAwait from "vite-plugin-top-level-await";
import wasm from "vite-plugin-wasm";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { readFileSync, existsSync, statSync } from "node:fs";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const isDev = process.env.NODE_ENV === "development";
const isStorybook = process.env.npm_lifecycle_script?.includes("storybook");
const isPyodide = process.env.PYODIDE === "true";

console.log("Building environment:", process.env.NODE_ENV);

const ReactCompilerConfig = {
  target: "19",
};

// Plugin to handle SVG imports from @marimo-team/llm-info/icons
const svgInlinePlugin = (): Plugin => {
  return {
    name: "svg-inline-plugin",
    resolveId(id) {
      if (id.startsWith("@marimo-team/llm-info/icons/") && id.endsWith("?inline")) {
        const svgPath = id
          .replace("@marimo-team/llm-info/icons/", "")
          .replace("?inline", "");
        return `\0svg-inline:${svgPath}`;
      }
      return null;
    },
    load(id) {
      if (id.startsWith("\0svg-inline:")) {
        const svgPath = id.replace("\0svg-inline:", "");
        const fullPath = path.resolve(__dirname, "./packages/llm-info/icons", svgPath);
        const svgContent = readFileSync(fullPath, "utf-8");
        return `export default ${JSON.stringify(svgContent)};`;
      }
      return null;
    },
  };
};

// Plugin to handle JSON imports from @marimo-team/llm-info
const llmInfoJsonPlugin = (): Plugin => {
  const generatedDir = path.resolve(__dirname, "./packages/llm-info/data/generated");
  const modelsJsonPath = path.resolve(generatedDir, "models.json");
  const providersJsonPath = path.resolve(generatedDir, "providers.json");
  
  return {
    name: "llm-info-json-plugin",
    enforce: "pre", // Run before builtin plugins
    buildStart() {
      // Verify files exist at build start
      if (!existsSync(modelsJsonPath)) {
        throw new Error(
          `[llm-info-json-plugin] models.json not found at ${modelsJsonPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`,
        );
      }
      if (!existsSync(providersJsonPath)) {
        throw new Error(
          `[llm-info-json-plugin] providers.json not found at ${providersJsonPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`,
        );
      }
      
      // Verify files are not empty
      const modelsStats = statSync(modelsJsonPath);
      const providersStats = statSync(providersJsonPath);
      if (modelsStats.size === 0) {
        throw new Error(
          `[llm-info-json-plugin] models.json is empty at ${modelsJsonPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`,
        );
      }
      if (providersStats.size === 0) {
        throw new Error(
          `[llm-info-json-plugin] providers.json is empty at ${providersJsonPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`,
        );
      }
      
      // Verify JSON validity
      try {
        const modelsContent = readFileSync(modelsJsonPath, "utf-8");
        JSON.parse(modelsContent);
      } catch (error) {
        throw new Error(
          `[llm-info-json-plugin] Invalid JSON in models.json: ${error instanceof Error ? error.message : String(error)}`,
        );
      }
      
      try {
        const providersContent = readFileSync(providersJsonPath, "utf-8");
        JSON.parse(providersContent);
      } catch (error) {
        throw new Error(
          `[llm-info-json-plugin] Invalid JSON in providers.json: ${error instanceof Error ? error.message : String(error)}`,
        );
      }
    },
    resolveId(id, importer) {
      // Handle package exports
      if (id === "@marimo-team/llm-info/models.json" || id.endsWith("@marimo-team/llm-info/models.json")) {
        return `\0llm-info-json:models.json`;
      }
      if (id === "@marimo-team/llm-info/providers.json" || id.endsWith("@marimo-team/llm-info/providers.json")) {
        return `\0llm-info-json:providers.json`;
      }
      
      // Handle absolute file paths
      const normalizedId = path.normalize(id);
      const normalizedModelsPath = path.normalize(modelsJsonPath);
      const normalizedProvidersPath = path.normalize(providersJsonPath);
      
      if (normalizedId === normalizedModelsPath) {
        return `\0llm-info-json:models.json`;
      }
      if (normalizedId === normalizedProvidersPath) {
        return `\0llm-info-json:providers.json`;
      }
      
      // Handle relative paths that resolve to these files
      if (id.includes("llm-info/data/generated/models.json") || id.includes("llm-info\\data\\generated\\models.json")) {
        let resolved: string;
        if (id.startsWith(".") || id.startsWith("/") || /^[A-Z]:/.test(id)) {
          resolved = path.resolve(importer ? path.dirname(importer) : __dirname, id);
        } else {
          resolved = path.resolve(__dirname, id);
        }
        const normalizedResolved = path.normalize(resolved);
        if (normalizedResolved === normalizedModelsPath || normalizedResolved.endsWith(path.join("packages", "llm-info", "data", "generated", "models.json"))) {
          return `\0llm-info-json:models.json`;
        }
        // Also check if it's the actual file by checking existence
        if (existsSync(resolved) && resolved === modelsJsonPath) {
          return `\0llm-info-json:models.json`;
        }
      }
      
      if (id.includes("llm-info/data/generated/providers.json") || id.includes("llm-info\\data\\generated\\providers.json")) {
        let resolved: string;
        if (id.startsWith(".") || id.startsWith("/") || /^[A-Z]:/.test(id)) {
          resolved = path.resolve(importer ? path.dirname(importer) : __dirname, id);
        } else {
          resolved = path.resolve(__dirname, id);
        }
        const normalizedResolved = path.normalize(resolved);
        if (normalizedResolved === normalizedProvidersPath || normalizedResolved.endsWith(path.join("packages", "llm-info", "data", "generated", "providers.json"))) {
          return `\0llm-info-json:providers.json`;
        }
        // Also check if it's the actual file by checking existence
        if (existsSync(resolved) && resolved === providersJsonPath) {
          return `\0llm-info-json:providers.json`;
        }
      }
      
      // Last resort: check if the resolved path matches our files
      // Only check if the ID contains our target paths to avoid processing other JSON files
      if (id.endsWith(".json") && (id.includes("llm-info") || importer?.includes("llm-info"))) {
        let resolved: string;
        if (importer) {
          resolved = path.resolve(path.dirname(importer), id);
        } else if (id.startsWith(".") || id.startsWith("/") || /^[A-Z]:/.test(id)) {
          resolved = path.resolve(__dirname, id);
        } else {
          resolved = id;
        }
        const normalizedResolved = path.normalize(resolved);
        if (normalizedResolved === normalizedModelsPath) {
          return `\0llm-info-json:models.json`;
        }
        if (normalizedResolved === normalizedProvidersPath) {
          return `\0llm-info-json:providers.json`;
        }
      }
      
      return null;
    },
    load(id) {
      // Handle virtual IDs from resolveId
      if (id.startsWith("\0llm-info-json:")) {
        const jsonFile = id.replace("\0llm-info-json:", "");
        const fullPath = path.resolve(
          __dirname,
          "./packages/llm-info/data/generated",
          jsonFile,
        );
        
        // Check if file exists
        if (!existsSync(fullPath)) {
          const error = `JSON file not found: ${fullPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`;
          console.error(`[llm-info-json-plugin] ${error}`);
          throw new Error(error);
        }
        
        // Check if file is empty
        const stats = statSync(fullPath);
        if (stats.size === 0) {
          const error = `JSON file is empty: ${fullPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`;
          console.error(`[llm-info-json-plugin] ${error}`);
          throw new Error(error);
        }
        
        const jsonContent = readFileSync(fullPath, "utf-8").trim();
        
        // Validate JSON content is not empty after trimming
        if (!jsonContent) {
          const error = `JSON file contains no valid content: ${fullPath}. Please run 'pnpm --filter @marimo-team/llm-info codegen' first.`;
          console.error(`[llm-info-json-plugin] ${error}`);
          throw new Error(error);
        }
        
        // Validate JSON format
        try {
          JSON.parse(jsonContent);
        } catch (error) {
          const errorMsg = `Invalid JSON in file ${fullPath}: ${error instanceof Error ? error.message : String(error)}. Please run 'pnpm --filter @marimo-team/llm-info codegen' to regenerate.`;
          console.error(`[llm-info-json-plugin] ${errorMsg}`);
          console.error(`[llm-info-json-plugin] File content preview (first 200 chars): ${jsonContent.substring(0, 200)}`);
          throw new Error(errorMsg);
        }
        
        return `export default ${jsonContent};`;
      }
      
      // Also handle actual file paths that might bypass resolveId
      // This is important because package.json exports may resolve to actual file paths
      // Only process files that are actually our target JSON files
      if (id && typeof id === "string" && id.endsWith(".json")) {
        // First check if this is one of our target files by path matching
        const isModelsJson = id.includes("llm-info/data/generated/models.json") || 
                            id.includes("llm-info\\data\\generated\\models.json") ||
                            id.endsWith("models.json") && id.includes("llm-info");
        const isProvidersJson = id.includes("llm-info/data/generated/providers.json") || 
                                id.includes("llm-info\\data\\generated\\providers.json") ||
                                id.endsWith("providers.json") && id.includes("llm-info");
        
        // If it's not one of our target files, return null immediately
        if (!isModelsJson && !isProvidersJson) {
          return null;
        }
        
        // Normalize paths for comparison
        let resolvedPath: string | null = null;
        
        // Try to resolve the path
        if (existsSync(id)) {
          resolvedPath = path.resolve(id);
        } else if (id.startsWith(".") || id.startsWith("/") || /^[A-Z]:/.test(id)) {
          const attempted = path.resolve(__dirname, id);
          if (existsSync(attempted)) {
            resolvedPath = path.resolve(attempted);
          }
        }
        
        if (resolvedPath) {
          const normalizedResolved = path.normalize(resolvedPath);
          const normalizedModelsPath = path.normalize(modelsJsonPath);
          const normalizedProvidersPath = path.normalize(providersJsonPath);
          
          // Check if this is one of our target files
          if (normalizedResolved === normalizedModelsPath || 
              normalizedResolved.includes("llm-info/data/generated/models.json") ||
              normalizedResolved.includes("llm-info\\data\\generated\\models.json")) {
            if (!existsSync(modelsJsonPath)) {
              return null;
            }
            const jsonContent = readFileSync(modelsJsonPath, "utf-8").trim();
            try {
              JSON.parse(jsonContent);
            } catch (error) {
              console.error(`[llm-info-json-plugin] Invalid JSON in ${modelsJsonPath}: ${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
            return `export default ${jsonContent};`;
          }
          
          if (normalizedResolved === normalizedProvidersPath || 
              normalizedResolved.includes("llm-info/data/generated/providers.json") ||
              normalizedResolved.includes("llm-info\\data\\generated\\providers.json")) {
            if (!existsSync(providersJsonPath)) {
              return null;
            }
            const jsonContent = readFileSync(providersJsonPath, "utf-8").trim();
            try {
              JSON.parse(jsonContent);
            } catch (error) {
              console.error(`[llm-info-json-plugin] Invalid JSON in ${providersJsonPath}: ${error instanceof Error ? error.message : String(error)}`);
              throw error;
            }
            return `export default ${jsonContent};`;
          }
        }
      }
      
      return null;
    },
  };
};

// https://vitejs.dev/config/
export default defineConfig({
  // This allows for a dynamic <base> tag in index.html
  base: "./",
  server: {
    host: "localhost",
    port: 3000,
    headers: isPyodide
      ? {
          "Cross-Origin-Opener-Policy": "same-origin",
          "Cross-Origin-Embedder-Policy": "require-corp",
        }
      : {},
  },
  define: {
    "import.meta.env.VITE_MARIMO_VERSION": process.env.VITE_MARIMO_VERSION
      ? JSON.stringify(process.env.VITE_MARIMO_VERSION)
      : JSON.stringify("latest"),
    "process.env.NODE_ENV": JSON.stringify(process.env.NODE_ENV),
  },
  build: {
    minify: isDev ? false : "oxc", // default is "oxc"
    sourcemap: isDev,
  },
  assetsInclude: ["**/*.svg"],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
      "@marimo-team/llm-info/icons": path.resolve(__dirname, "./packages/llm-info/icons"),
    },
    tsconfigPaths: true,
    dedupe: [
      "react",
      "react-dom",
      "@emotion/react",
      "@emotion/cache",
      "@codemirror/view",
      "@codemirror/state",
    ],
    conditions: ["import", "module", "browser", "default"],
  },
  experimental: {
    enableNativePlugin: true,
  },
  worker: {
    format: "es",
  },
  plugins: [
    svgInlinePlugin(),
    llmInfoJsonPlugin(),
    react({
      babel: {
        presets: ["@babel/preset-typescript"],
        plugins: [
          ["@babel/plugin-proposal-decorators", { legacy: true }],
          ["babel-plugin-react-compiler", ReactCompilerConfig],
        ],
      },
    }),
    codecovVitePlugin({
      enableBundleAnalysis: process.env.CODECOV_TOKEN !== undefined,
      bundleName: "backcast",
      uploadToken: process.env.CODECOV_TOKEN,
    }),
    wasm(),
    topLevelAwait(),
  ],
});
