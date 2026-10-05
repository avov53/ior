import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    assetsInlineLimit: 0,
    rollupOptions: {
      input: fileURLToPath(new URL("./app.html", import.meta.url)),
      output: {
        manualChunks(id) {
          return id.includes("node_modules") && !id.endsWith(".svg") ? "vendor" : undefined;
        }
      }
    }
  }
});
