import { readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { rolldown } from "rolldown";

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const builtHtmlPath = resolve(projectRoot, "dist", "app.html");
const outputPath = resolve(projectRoot, "index.html");

let html = await readFile(builtHtmlPath, "utf8");

const stylesheet = html.match(/<link rel="stylesheet"[^>]*href="\.\/assets\/([^"]+\.css)"[^>]*>/);
const script = html.match(/<script type="module"[^>]*src="\.\/assets\/([^"]+\.js)"[^>]*><\/script>/);

if (!stylesheet || !script) {
  throw new Error("Could not locate the generated CSS or JavaScript bundle.");
}

const css = await readFile(resolve(projectRoot, "dist", "assets", stylesheet[1]), "utf8");
const bundle = await rolldown({
  input: resolve(projectRoot, "dist", "assets", script[1]),
  plugins: [{
    name: "inline-offline-flags",
    async transform(source, id) {
      if (!id.endsWith(".js")) return null;
      const flagUrl = /new URL\((["'`])([^"'`]+\.svg)\1,\s*import\.meta\.url\)\.href/g;
      const matches = [...source.matchAll(flagUrl)];
      if (!matches.length) return null;
      const replacements = await Promise.all(matches.map(async match => {
        const svg = await readFile(resolve(dirname(id), match[2]));
        return [match[0], JSON.stringify(`data:image/svg+xml;base64,${svg.toString("base64")}`)];
      }));
      let transformed = source;
      for (const [expression, dataUrl] of replacements) transformed = transformed.replace(expression, dataUrl);
      return transformed;
    }
  }]
});
const output = await bundle.generate({ format: "iife", name: "IorStandalone", minify: true });
await bundle.close();
const javascript = output.output.find(item => item.type === "chunk")?.code;
if (!javascript || output.output.filter(item => item.type === "chunk").length !== 1) {
  throw new Error("Standalone bundle must contain exactly one JavaScript chunk.");
}
const inlineJavascript = javascript.replaceAll("</script>", "<\\/script>");

html = html
  .replace(stylesheet[0], () => `<style>${css}</style>`)
  .replace(script[0], "")
  .replace(/<link rel="modulepreload"[^>]*>/g, "")
  .replace("</body>", () => `<script>${inlineJavascript}</script></body>`);

if (html.includes(script[0]) || html.includes(stylesheet[0])) {
  throw new Error("Standalone HTML still references source or generated asset files.");
}

new Function(javascript);
if (javascript.includes("import.meta") || javascript.includes("/assets/")) {
  throw new Error("Standalone JavaScript still references external assets.");
}

await writeFile(outputPath, html, "utf8");
console.log("Standalone design/index.html created.");
