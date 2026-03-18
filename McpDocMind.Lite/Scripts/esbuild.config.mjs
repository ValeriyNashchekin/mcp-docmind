import { build } from "esbuild";
import { resolve, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));

await build({
  entryPoints: [resolve(__dirname, "extract-ts-api.js")],
  bundle: true,
  platform: "node",
  target: "node18",
  format: "cjs",
  outfile: resolve(__dirname, "..", "Resources", "extract-ts-api.bundle.js"),
  minify: true,
  banner: { js: "#!/usr/bin/env node" },
});

console.log("Bundle built: Resources/extract-ts-api.bundle.js");
