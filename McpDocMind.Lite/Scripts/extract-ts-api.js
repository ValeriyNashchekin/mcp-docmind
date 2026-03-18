/**
 * TypeScript API Extractor for McpDocMind.
 *
 * Uses ts-morph to parse TypeScript source / declaration files and extract
 * a structured API graph (nodes + relations) as JSON to stdout.
 *
 * Usage: node extract-ts-api.js <repoPath> [--library <name>]
 */

import { Project, SyntaxKind, Node as TsNode } from "ts-morph";
import { existsSync, readFileSync, readdirSync, statSync } from "fs";
import { resolve, join, relative, dirname, basename, sep } from "path";

// ── CLI args ────────────────────────────────────────────────────────────────

const args = process.argv.slice(2);
const repoPath = args[0];
if (!repoPath) {
  process.stderr.write("Usage: extract-ts-api.js <repoPath> [--library <name>]\n");
  process.exit(1);
}

let libraryName = "";
const libIdx = args.indexOf("--library");
if (libIdx !== -1 && args[libIdx + 1]) {
  libraryName = args[libIdx + 1];
}

// Try to detect library name from package.json
if (!libraryName) {
  const pkgPath = join(repoPath, "package.json");
  if (existsSync(pkgPath)) {
    try {
      const pkg = JSON.parse(readFileSync(pkgPath, "utf-8"));
      libraryName = pkg.name?.replace(/^@[^/]+\//, "") || "";
    } catch { /* ignore */ }
  }
  if (!libraryName) {
    libraryName = basename(repoPath);
  }
}

// ── Project setup ───────────────────────────────────────────────────────────

const ALWAYS_IGNORED_DIRS = new Set([
  "node_modules", "test", "tests", "__tests__", "__test__",
  "spec", "specs", "fixtures", "__fixtures__", "__mocks__",
  "e2e", "dist", "build", "out",
  ".git", ".github", ".vscode", ".idea",
  "coverage", "benchmark", "benchmarks",
  "cypress", "playwright",
]);

// Dirs that are ignored by default but allowed if referenced in package.json exports
const SOFT_IGNORED_DIRS = new Set(["examples", "example"]);

const IGNORED_SUFFIXES = [".test.ts", ".spec.ts", ".test.d.ts", ".spec.d.ts", ".stories.ts", ".stories.tsx"];

/** Parse package.json exports to find directories that are part of public API */
function getExportedDirs() {
  const pkgPath = join(repoPath, "package.json");
  if (!existsSync(pkgPath)) return new Set();

  try {
    const pkg = JSON.parse(readFileSync(pkgPath, "utf-8"));
    const dirs = new Set();
    const exports = pkg.exports;
    if (!exports || typeof exports !== "object") return dirs;

    // Walk all export values and extract directory prefixes
    const values = [];
    for (const [key, val] of Object.entries(exports)) {
      if (typeof val === "string") values.push(val);
      else if (typeof val === "object" && val !== null) {
        // Handle conditional exports: { import: "./...", require: "./..." }
        for (const v of Object.values(val)) {
          if (typeof v === "string") values.push(v);
        }
      }
      // Also check the key itself for path patterns
      if (key.startsWith("./")) values.push(key);
    }

    for (const v of values) {
      // Extract first directory segment: "./examples/jsm/*" → "examples"
      const match = v.match(/^\.\/([^/]+)/);
      if (match) dirs.add(match[1]);
    }

    return dirs;
  } catch {
    return new Set();
  }
}

const exportedDirs = getExportedDirs();
if (exportedDirs.size > 0) {
  process.stderr.write(`Public API dirs from package.json exports: ${[...exportedDirs].join(", ")}\n`);
}

function shouldIncludeFile(filePath) {
  const rel = relative(repoPath, filePath);
  const parts = rel.split(/[\\/]/);

  for (const p of parts) {
    if (ALWAYS_IGNORED_DIRS.has(p)) return false;
    // Soft-ignored dirs are allowed if they appear in package.json exports
    if (SOFT_IGNORED_DIRS.has(p) && !exportedDirs.has(p)) return false;
  }

  const lower = filePath.toLowerCase();
  if (IGNORED_SUFFIXES.some((s) => lower.endsWith(s))) return false;
  return true;
}

/** Find tsconfig.json with priority ordering */
function findTsConfig() {
  const candidates = [
    "tsconfig.json",
    "tsconfig.build.json",
    "src/tsconfig.json",
    "lib/tsconfig.json",
  ];
  for (const c of candidates) {
    const p = join(repoPath, c);
    if (existsSync(p)) return p;
  }
  return null;
}

const tsConfigPath = findTsConfig();

// Detect what kind of source files we have
const allFiles = findFilesRecursive(repoPath);
const dtsFiles = allFiles.filter((f) => f.endsWith(".d.ts"));
const tsFiles = allFiles.filter((f) => f.endsWith(".ts") && !f.endsWith(".d.ts"));
const jsFiles = allFiles.filter((f) => f.endsWith(".js") && !f.endsWith(".min.js"));

// Enable allowJs + checkJs when the repo uses JS with JSDoc (like three.js)
const hasTypeScript = dtsFiles.length > 0 || tsFiles.length > 0;
const useJs = !hasTypeScript && jsFiles.length > 0;

const project = new Project({
  compilerOptions: {
    allowJs: true,
    checkJs: useJs,
    // Resolve JS imports that omit .js extension
    moduleResolution: useJs ? 100 /* NodeNext */ : undefined,
  },
});

let sourceFiles;
if (tsConfigPath) {
  process.stderr.write(`Using tsconfig: ${relative(repoPath, tsConfigPath)}\n`);
  try {
    project.addSourceFilesFromTsConfig(tsConfigPath);
    sourceFiles = project.getSourceFiles().filter((sf) => shouldIncludeFile(sf.getFilePath()));
  } catch (e) {
    process.stderr.write(`Warning: Failed to load tsconfig, falling back to scan: ${e.message}\n`);
    sourceFiles = null;
  }
}

if (!sourceFiles || sourceFiles.length === 0) {
  // Fallback: scan for .d.ts first, then .ts, then .js
  // Priority: .d.ts > .ts > .js (with JSDoc)
  let filesToAdd;
  if (dtsFiles.length > 0) {
    filesToAdd = dtsFiles;
    process.stderr.write(`Found ${dtsFiles.length} .d.ts files\n`);
  } else if (tsFiles.length > 0) {
    filesToAdd = tsFiles;
    process.stderr.write(`Found ${tsFiles.length} .ts files\n`);
  } else if (jsFiles.length > 0) {
    filesToAdd = jsFiles;
    process.stderr.write(`Found ${jsFiles.length} .js files (JSDoc mode)\n`);
  } else {
    filesToAdd = [];
  }

  for (const f of filesToAdd) {
    try {
      project.addSourceFileAtPath(f);
    } catch (e) {
      process.stderr.write(`Warning: skipping ${relative(repoPath, f)}: ${e.message}\n`);
    }
  }
  sourceFiles = project.getSourceFiles().filter((sf) => shouldIncludeFile(sf.getFilePath()));
}

process.stderr.write(`Parsing ${sourceFiles.length} source files...\n`);

// ── Extraction ──────────────────────────────────────────────────────────────

const nodes = [];
const relations = [];
const seenFullNames = new Set();

function addNode(node) {
  if (seenFullNames.has(node.fullName)) return;
  seenFullNames.add(node.fullName);
  nodes.push(node);
}

function addRelation(source, target, type) {
  relations.push({ source, target, type });
}

/** Derive namespace from file path relative to repo root */
function deriveNamespace(filePath) {
  let rel = relative(repoPath, filePath).replace(/\\/g, "/");
  // Strip common prefixes
  for (const prefix of ["src/", "lib/", "dist/", "source/", "packages/"]) {
    if (rel.startsWith(prefix)) {
      rel = rel.slice(prefix.length);
      break;
    }
  }
  // Remove filename
  const dir = dirname(rel);
  if (dir === ".") return libraryName;
  // Convert path to dot-separated namespace
  const ns = dir.replace(/\//g, ".");
  return `${libraryName}.${ns}`;
}

/** Get JSDoc summary from a node */
function getJsDoc(node) {
  if (!node.getJsDocs) return "";
  const docs = node.getJsDocs();
  if (docs.length === 0) return "";
  const comment = docs[docs.length - 1].getDescription?.();
  if (!comment) return "";
  return comment.trim().replace(/\n/g, " ").slice(0, 500);
}

/** Build full name for a member */
function memberFullName(parentFullName, memberName) {
  return `${parentFullName}.${memberName}`;
}

/** Get overload-safe name for a function/method */
function getOverloadSuffix(params) {
  if (!params || params.length === 0) return "";
  // Only add suffix if there are overloads — caller checks this
  return `_${params.map((p) => p.getName?.() || "arg").join("_")}`;
}

/** Extract method/function info */
function extractCallable(decl, parentFullName, ns, isConstructor = false) {
  const name = isConstructor ? "constructor" : (decl.getName?.() || "anonymous");
  const params = decl.getParameters?.() || [];
  const paramStr = params
    .map((p) => {
      const pName = p.getName();
      const pType = p.getType?.().getText?.(p) || "any";
      const optional = p.isOptional?.() ? "?" : "";
      return `${pName}${optional}: ${pType}`;
    })
    .join(", ");

  const returnType = isConstructor ? null : (decl.getReturnType?.().getText?.(decl) || "void");
  const declaration = isConstructor
    ? `constructor(${paramStr})`
    : `${name}(${paramStr}): ${returnType}`;

  let fullName = memberFullName(parentFullName, name);
  // Handle overloads: check if this name was already seen
  if (seenFullNames.has(fullName) && params.length > 0) {
    fullName = memberFullName(parentFullName, name + getOverloadSuffix(params));
  }

  return {
    name,
    fullName,
    namespace: ns,
    nodeType: isConstructor ? "Constructor" : "Method",
    declaration,
    summary: getJsDoc(decl),
    returnType,
    parameters: paramStr || null,
    parentType: parentFullName,
  };
}

/** Extract a class declaration */
function extractClass(classDecl, filePath) {
  const name = classDecl.getName?.();
  if (!name) return;
  const ns = deriveNamespace(filePath);
  const fullName = `${ns}.${name}`;

  // Type params
  const typeParams = classDecl.getTypeParameters?.() || [];
  const typeParamStr = typeParams.length > 0
    ? `<${typeParams.map((tp) => tp.getText()).join(", ")}>`
    : "";
  const displayName = `${name}${typeParamStr}`;

  // Extends
  const baseClass = classDecl.getBaseClass?.();
  const baseClassName = baseClass?.getName?.();
  const extendsClause = classDecl.getExtends?.()?.getText?.() || "";

  // Implements
  const implementsClauses = classDecl.getImplements?.() || [];

  // Declaration string
  let declStr = `class ${displayName}`;
  if (extendsClause) declStr += ` extends ${extendsClause}`;
  if (implementsClauses.length > 0) declStr += ` implements ${implementsClauses.map((i) => i.getText()).join(", ")}`;

  addNode({
    name: displayName,
    fullName,
    namespace: ns,
    nodeType: "Class",
    declaration: declStr,
    summary: getJsDoc(classDecl),
    returnType: null,
    parameters: null,
    parentType: null,
  });

  // Relations
  if (baseClassName) {
    const baseFullName = `${deriveNamespace(baseClass.getSourceFile().getFilePath())}.${baseClassName}`;
    addRelation(fullName, baseFullName, "InheritsFrom");
  }
  for (const impl of implementsClauses) {
    const implText = impl.getText().replace(/<.*>/, ""); // strip generics for display
    // Try to resolve the full name by finding the declaration
    try {
      const implType = impl.getType?.();
      const implSymbol = implType?.getSymbol?.() || implType?.getAliasSymbol?.();
      const implDecl = implSymbol?.getDeclarations?.()?.[0];
      if (implDecl) {
        const implSourceFile = implDecl.getSourceFile();
        const implName = implSymbol.getName();
        const implFullName = `${deriveNamespace(implSourceFile.getFilePath())}.${implName}`;
        addRelation(fullName, implFullName, "Implements");
      } else {
        addRelation(fullName, implText, "Implements");
      }
    } catch {
      addRelation(fullName, implText, "Implements");
    }
  }

  // Members
  extractClassMembers(classDecl, fullName, ns);
}

/** Extract members of a class or interface */
function extractClassMembers(decl, parentFullName, ns) {
  // Methods
  for (const method of decl.getMethods?.() || []) {
    if (isPrivate(method)) continue;
    const node = extractCallable(method, parentFullName, ns);
    addNode(node);
    addRelation(parentFullName, node.fullName, "Contains");
  }

  // Properties
  for (const prop of decl.getProperties?.() || []) {
    if (isPrivate(prop)) continue;
    const name = prop.getName();
    const fullName = memberFullName(parentFullName, name);
    const propType = prop.getType?.().getText?.(prop) || "any";

    addNode({
      name,
      fullName,
      namespace: ns,
      nodeType: "Property",
      declaration: `${name}: ${propType}`,
      summary: getJsDoc(prop),
      returnType: propType,
      parameters: null,
      parentType: parentFullName,
    });
    addRelation(parentFullName, fullName, "Contains");
  }

  // Constructors (class only)
  for (const ctor of decl.getConstructors?.() || []) {
    const node = extractCallable(ctor, parentFullName, ns, true);
    addNode(node);
    addRelation(parentFullName, node.fullName, "Contains");
  }

  // Getters/Setters
  for (const acc of decl.getGetAccessors?.() || []) {
    if (isPrivate(acc)) continue;
    const name = acc.getName();
    const fullName = memberFullName(parentFullName, name);
    if (seenFullNames.has(fullName)) continue;
    const retType = acc.getReturnType?.().getText?.(acc) || "any";

    addNode({
      name,
      fullName,
      namespace: ns,
      nodeType: "Property",
      declaration: `get ${name}: ${retType}`,
      summary: getJsDoc(acc),
      returnType: retType,
      parameters: null,
      parentType: parentFullName,
    });
    addRelation(parentFullName, fullName, "Contains");
  }
}

/** Extract an interface */
function extractInterface(ifaceDecl, filePath) {
  const name = ifaceDecl.getName?.();
  if (!name) return;
  const ns = deriveNamespace(filePath);
  const fullName = `${ns}.${name}`;

  const typeParams = ifaceDecl.getTypeParameters?.() || [];
  const typeParamStr = typeParams.length > 0
    ? `<${typeParams.map((tp) => tp.getText()).join(", ")}>`
    : "";
  const displayName = `${name}${typeParamStr}`;

  const extendsClauses = ifaceDecl.getExtends?.() || [];
  let declStr = `interface ${displayName}`;
  if (extendsClauses.length > 0) declStr += ` extends ${extendsClauses.map((e) => e.getText()).join(", ")}`;

  addNode({
    name: displayName,
    fullName,
    namespace: ns,
    nodeType: "Interface",
    declaration: declStr,
    summary: getJsDoc(ifaceDecl),
    returnType: null,
    parameters: null,
    parentType: null,
  });

  // Extends relations
  for (const ext of extendsClauses) {
    const extText = ext.getText().replace(/<.*>/, "");
    // Try to resolve the full name
    try {
      const extType = ext.getType?.();
      const extSymbol = extType?.getSymbol?.() || extType?.getAliasSymbol?.();
      const extDecl = extSymbol?.getDeclarations?.()?.[0];
      if (extDecl) {
        const extSourceFile = extDecl.getSourceFile();
        const extName = extSymbol.getName();
        addRelation(fullName, `${deriveNamespace(extSourceFile.getFilePath())}.${extName}`, "InheritsFrom");
      } else {
        addRelation(fullName, extText, "InheritsFrom");
      }
    } catch {
      addRelation(fullName, extText, "InheritsFrom");
    }
  }

  // Members
  extractClassMembers(ifaceDecl, fullName, ns);
}

/** Extract an enum */
function extractEnum(enumDecl, filePath) {
  const name = enumDecl.getName();
  if (!name) return;
  const ns = deriveNamespace(filePath);
  const fullName = `${ns}.${name}`;

  addNode({
    name,
    fullName,
    namespace: ns,
    nodeType: "Enum",
    declaration: `enum ${name}`,
    summary: getJsDoc(enumDecl),
    returnType: null,
    parameters: null,
    parentType: null,
  });

  // Enum members
  for (const member of enumDecl.getMembers()) {
    const mName = member.getName();
    const mFullName = memberFullName(fullName, mName);
    const value = member.getValue?.();

    addNode({
      name: mName,
      fullName: mFullName,
      namespace: ns,
      nodeType: "Field",
      declaration: value !== undefined ? `${mName} = ${value}` : mName,
      summary: getJsDoc(member),
      returnType: typeof value === "string" ? "string" : "number",
      parameters: null,
      parentType: fullName,
    });
    addRelation(fullName, mFullName, "Contains");
  }
}

/** Extract a type alias */
function extractTypeAlias(typeAlias, filePath) {
  const name = typeAlias.getName();
  if (!name) return;
  const ns = deriveNamespace(filePath);
  const fullName = `${ns}.${name}`;

  const typeParams = typeAlias.getTypeParameters?.() || [];
  const typeParamStr = typeParams.length > 0
    ? `<${typeParams.map((tp) => tp.getText()).join(", ")}>`
    : "";

  const typeText = typeAlias.getType().getText(typeAlias);

  addNode({
    name: `${name}${typeParamStr}`,
    fullName,
    namespace: ns,
    nodeType: "TypeAlias",
    declaration: `type ${name}${typeParamStr} = ${typeText}`,
    summary: getJsDoc(typeAlias),
    returnType: typeText,
    parameters: null,
    parentType: null,
  });
}

/** Extract a top-level function */
function extractFunction(funcDecl, filePath, moduleFullName) {
  const name = funcDecl.getName?.();
  if (!name) return;
  const ns = deriveNamespace(filePath);
  const node = extractCallable(funcDecl, moduleFullName, ns);
  addNode(node);
  addRelation(moduleFullName, node.fullName, "Contains");
}

/** Extract a top-level variable/constant */
function extractVariable(varDecl, filePath, moduleFullName) {
  const name = varDecl.getName?.();
  if (!name || name.startsWith("_")) return;
  const ns = deriveNamespace(filePath);
  const fullName = memberFullName(moduleFullName, name);
  const varType = varDecl.getType?.().getText?.(varDecl) || "any";
  const keyword = varDecl.isConst?.() ? "const" : "let";

  addNode({
    name,
    fullName,
    namespace: ns,
    nodeType: "Property",
    declaration: `${keyword} ${name}: ${varType}`,
    summary: getJsDoc(varDecl.getVariableStatement?.() || varDecl),
    returnType: varType,
    parameters: null,
    parentType: moduleFullName,
  });
  addRelation(moduleFullName, fullName, "Contains");
}

function isPrivate(member) {
  if (!member.getModifiers) return false;
  const mods = member.getModifiers();
  return mods.some(
    (m) =>
      m.getKind() === SyntaxKind.PrivateKeyword ||
      m.getText() === "private"
  );
}

// ── File traversal helpers ──────────────────────────────────────────────────

function findFilesRecursive(dir, acc = []) {
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return acc; }

  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (ALWAYS_IGNORED_DIRS.has(entry.name)) continue;
      if (SOFT_IGNORED_DIRS.has(entry.name) && !exportedDirs.has(entry.name)) continue;
      findFilesRecursive(full, acc);
    } else if (entry.isFile() && (entry.name.endsWith(".ts") || entry.name.endsWith(".d.ts") || entry.name.endsWith(".js"))) {
      if (entry.name.endsWith(".min.js")) continue;
      if (shouldIncludeFile(full)) acc.push(full);
    }
  }
  return acc;
}

// ── Main extraction loop ────────────────────────────────────────────────────

// Track which namespaces have top-level exports (for synthetic module nodes)
const moduleNodes = new Map(); // ns → fullName

function ensureModuleNode(ns) {
  if (moduleNodes.has(ns)) return moduleNodes.get(ns);
  const fullName = ns;
  addNode({
    name: ns.split(".").pop() || ns,
    fullName,
    namespace: ns.includes(".") ? ns.slice(0, ns.lastIndexOf(".")) : libraryName,
    nodeType: "Namespace",
    declaration: `module ${ns}`,
    summary: null,
    returnType: null,
    parameters: null,
    parentType: null,
  });
  moduleNodes.set(ns, fullName);
  return fullName;
}

for (const sf of sourceFiles) {
  const filePath = sf.getFilePath();
  try {
    const ns = deriveNamespace(filePath);
    let hasTopLevel = false;

    // Handle ambient module declarations: declare module "X" { ... }
    for (const modDecl of sf.getModules?.() || []) {
      const modName = modDecl.getName().replace(/['"]/g, "");
      // Process declarations inside the module
      for (const cls of modDecl.getClasses?.() || []) extractClass(cls, filePath);
      for (const iface of modDecl.getInterfaces?.() || []) extractInterface(iface, filePath);
      for (const en of modDecl.getEnums?.() || []) extractEnum(en, filePath);
      for (const ta of modDecl.getTypeAliases?.() || []) extractTypeAlias(ta, filePath);
      for (const fn of modDecl.getFunctions?.() || []) {
        const modFullName = ensureModuleNode(modName || ns);
        extractFunction(fn, filePath, modFullName);
      }
    }

    // Top-level exported declarations
    for (const cls of sf.getClasses()) {
      if (cls.isExported()) extractClass(cls, filePath);
    }
    for (const iface of sf.getInterfaces()) {
      if (iface.isExported()) extractInterface(iface, filePath);
    }
    for (const en of sf.getEnums()) {
      if (en.isExported()) extractEnum(en, filePath);
    }
    for (const ta of sf.getTypeAliases()) {
      if (ta.isExported()) extractTypeAlias(ta, filePath);
    }
    for (const fn of sf.getFunctions()) {
      if (!fn.isExported()) continue;
      const modFullName = ensureModuleNode(ns);
      extractFunction(fn, filePath, modFullName);
      hasTopLevel = true;
    }
    for (const vs of sf.getVariableStatements()) {
      if (!vs.isExported()) continue;
      const modFullName = ensureModuleNode(ns);
      for (const decl of vs.getDeclarations()) {
        extractVariable(decl, filePath, modFullName);
      }
      hasTopLevel = true;
    }
  } catch (e) {
    process.stderr.write(`Warning: error processing ${relative(repoPath, filePath)}: ${e.message}\n`);
  }
}

// ── Output ──────────────────────────────────────────────────────────────────

const result = { nodes, relations };
process.stdout.write(JSON.stringify(result));
process.stderr.write(`Extracted ${nodes.length} nodes, ${relations.length} relations\n`);
