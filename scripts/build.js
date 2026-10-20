// Builds the Windows installer, dist/TabletDeck-Setup-<version>.exe, so players don't
// need Node.js. Developers keep using `node server.js` or start.bat.
//
//   npm install      (once: the build-only tools postject and rcedit)
//   npm run build
//
// 1. Checks keybinds/TabletDeck-StarCitizen.xml is up to date with the layouts.
// 2. Makes the app icon from public/icon-512.png.
// 3. Compiles the app window (native/launcher.cs -> TabletDeck.exe), the key sender and the
//    voice listener, with version info.
// 4. Packs server.js, qr.js, public/, layouts/ and keybinds/ into TabletDeckServer.exe, a
//    Node single executable application made from the node.exe running this script.
// 5. Signs the .exe files, if SIGN_CMD is set (see below).
// 6. Runs Inno Setup on installer/TabletDeck.iss (which signs the installer too, with
//    SIGN_CMD) and writes the installer's SHA-256 checksum next to it.
//
// A signing service that signs files between steps (SignPath, in release.yml) runs the
// stages separately:
//   npm run build -- --app         steps 1-5: build/app
//   npm run build -- --installer   step 6, from the build/app already there
//   npm run build -- --checksum    only rewrite the checksum, after the installer was signed
//
// Needs Windows, the C# compiler that ships with the .NET Framework, and Inno Setup 6
// (ISCC.exe on PATH or in its usual folders, or set ISCC to its path).
//
// SIGN_CMD signs one file, where $f stands for the quoted file name (the same syntax as
// Inno Setup's sign tools). E.g.
//   signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p <password> $f
// Without it, and without SignPath, builds are unsigned: they work, but Windows
// SmartScreen warns about an "unknown publisher".
"use strict";
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFileSync, execSync } = require("child_process");

const ROOT = path.join(__dirname, "..");
const BUILD = path.join(ROOT, "build");
const APP = path.join(BUILD, "app"); // becomes the install folder in Program Files
const DIST = path.join(ROOT, "dist");
const VERSION = JSON.parse(fs.readFileSync(path.join(ROOT, "package.json"), "utf8")).version;
// Windows version resources need four numbers: "1.2.3-beta.1" -> "1.2.3.0".
const FILE_VERSION = `${VERSION.replace(/[-+].*$/, "")}.0`;
const COPYRIGHT = "Copyright (c) 2026 nodieCode. MIT License.";
const SIGN_CMD = process.env.SIGN_CMD || "";
const SETUP_NAME = `TabletDeck-Setup-${VERSION}.exe`;

const step = (text) => console.log(`\n== ${text}`);
const run = (file, args) => execFileSync(file, args, { stdio: "inherit" });

// First existing path in the list, else null.
const firstExisting = (list) => list.find((p) => p && fs.existsSync(p)) || null;

// Every file under dir, as paths relative to ROOT with forward slashes.
function filesUnder(dir) {
  return fs.readdirSync(path.join(ROOT, dir), { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? filesUnder(`${dir}/${e.name}`) : [`${dir}/${e.name}`]);
}

function sign(file) {
  if (!SIGN_CMD) return;
  console.log(`Signing ${path.relative(ROOT, file)}`);
  execSync(SIGN_CMD.replace(/\$f/g, `"${file}"`).replace(/\$q/g, '"'), { stdio: "inherit" });
}

// Steps 1-5: build/app (the files the installer copies) and build/icon.ico.
async function buildApp() {
  const { inject } = require("postject");
  fs.rmSync(BUILD, { recursive: true, force: true });
  fs.mkdirSync(path.join(APP, "native"), { recursive: true });

  step("Checking the keybind profile");
  run(process.execPath, [path.join(ROOT, "tools", "make-profile.js"), "--check"]);

  step("Making the icon");
  // The deck's 512 px icon scaled to the sizes Windows uses, stored as PNGs in an .ico.
  const sizes = [256, 48, 32, 16];
  execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", `
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile('${path.join(ROOT, "public", "icon-512.png")}')
    foreach ($s in ${sizes.join(",")}) {
      $bmp = New-Object System.Drawing.Bitmap $s, $s
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.DrawImage($src, 0, 0, $s, $s)
      $bmp.Save('${BUILD}\\icon-' + $s + '.png', [System.Drawing.Imaging.ImageFormat]::Png)
      $g.Dispose(); $bmp.Dispose()
    }
    $src.Dispose()`], { stdio: "inherit" });
  const pngs = sizes.map((s) => fs.readFileSync(path.join(BUILD, `icon-${s}.png`)));
  const icoHeader = Buffer.alloc(6 + 16 * sizes.length);
  icoHeader.writeUInt16LE(1, 2); // type: icon
  icoHeader.writeUInt16LE(sizes.length, 4);
  let offset = icoHeader.length;
  sizes.forEach((s, i) => {
    const e = 6 + 16 * i;
    icoHeader[e] = icoHeader[e + 1] = s % 256; // 0 means 256
    icoHeader.writeUInt16LE(1, e + 4); // planes
    icoHeader.writeUInt16LE(32, e + 6); // bits per pixel
    icoHeader.writeUInt32LE(pngs[i].length, e + 8);
    icoHeader.writeUInt32LE(offset, e + 12);
    offset += pngs[i].length;
  });
  const ico = path.join(BUILD, "icon.ico");
  fs.writeFileSync(ico, Buffer.concat([icoHeader, ...pngs]));

  step("Compiling the app window, key sender and voice listener");
  const fw = path.join(process.env.WINDIR || "C:\\Windows", "Microsoft.NET", "Framework64", "v4.0.30319");
  // The compiler and flags server.js uses from source, plus a version resource, which makes
  // the files easier for antivirus software and players to identify.
  function compileCs(name, out, title, { target = "exe", refs = [], icon = null } = {}) {
    const info = path.join(BUILD, `${name}-version.cs`);
    fs.writeFileSync(info, [
      "using System.Reflection;",
      `[assembly: AssemblyTitle("${title}")]`,
      `[assembly: AssemblyDescription("${title}")]`,
      '[assembly: AssemblyProduct("Tablet Deck")]',
      '[assembly: AssemblyCompany("nodieCode")]',
      `[assembly: AssemblyCopyright("${COPYRIGHT}")]`,
      `[assembly: AssemblyVersion("${FILE_VERSION}")]`,
      `[assembly: AssemblyFileVersion("${FILE_VERSION}")]`,
      `[assembly: AssemblyInformationalVersion("${VERSION}")]`,
      "",
    ].join("\n"));
    run(path.join(fw, "csc.exe"), ["/nologo", "/optimize", `/target:${target}`, "/platform:x64", `/lib:${path.join(fw, "WPF")}`,
      ...refs.map((r) => `/r:${r}`), ...(icon ? [`/win32icon:${icon}`] : []), `/out:${out}`, path.join(ROOT, "native", `${name}.cs`), info]);
    return out;
  }
  const window = compileCs("launcher", path.join(APP, "TabletDeck.exe"), "Tablet Deck",
    { target: "winexe", refs: ["System.Windows.Forms.dll", "System.Drawing.dll", "System.Web.Extensions.dll"], icon: ico });
  const natives = [
    compileCs("keysender", path.join(APP, "native", "keysender.exe"), "Tablet Deck key sender"),
    compileCs("voice", path.join(APP, "native", "voice.exe"), "Tablet Deck voice commands", { refs: ["System.Speech.dll"] }),
  ];

  step("Packing TabletDeckServer.exe");
  // A single executable runs one script and can only require Node's built-in modules, so
  // qr.js is inlined where server.js requires it.
  const qrRequire = 'const qr = require("./qr");';
  const server = fs.readFileSync(path.join(ROOT, "server.js"), "utf8");
  if (!server.includes(qrRequire)) throw new Error(`server.js no longer contains ${qrRequire}; update scripts/build.js.`);
  const qrSource = fs.readFileSync(path.join(ROOT, "qr.js"), "utf8");
  const bundle = server.replace(qrRequire, () =>
    `const qr = (() => { const module = { exports: {} };\n${qrSource}\nreturn module.exports; })();`);
  fs.writeFileSync(path.join(BUILD, "bundle.js"), bundle);

  // Files server.js reads with readBundled(), under their repo paths.
  const assets = ["package.json", ...filesUnder("public"), ...filesUnder("layouts"), ...filesUnder("keybinds")];
  const blob = path.join(BUILD, "sea.blob");
  fs.writeFileSync(path.join(BUILD, "sea-config.json"), JSON.stringify({
    main: path.join(BUILD, "bundle.js"),
    output: blob,
    disableExperimentalSEAWarning: true,
    assets: Object.fromEntries(assets.map((a) => [a, path.join(ROOT, a)])),
  }, null, 2));
  run(process.execPath, ["--experimental-sea-config", path.join(BUILD, "sea-config.json")]);
  console.log(`${assets.length} bundled files`);

  const exe = path.join(APP, "TabletDeckServer.exe");
  fs.copyFileSync(process.execPath, exe);

  // node.exe's signature stops matching once the app is added, so remove it first.
  const kits = path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "Windows Kits", "10", "bin");
  const signtool = firstExisting([
    process.env.SIGNTOOL,
    ...(fs.existsSync(kits) ? fs.readdirSync(kits).filter((d) => /^10\./.test(d)).sort().reverse().map((d) => path.join(kits, d, "x64", "signtool.exe")) : []),
  ]);
  if (signtool) run(signtool, ["remove", "/s", exe]);
  else console.log("signtool not found (Windows SDK); TabletDeckServer.exe keeps node.exe's old signature.");

  // Icon and version info before the app goes in; rcedit rewrites the resources.
  run(path.join(ROOT, "node_modules", "rcedit", "bin", "rcedit-x64.exe"), [exe,
    "--set-icon", ico,
    "--set-file-version", FILE_VERSION,
    "--set-product-version", VERSION,
    "--set-version-string", "FileDescription", "Tablet Deck server",
    "--set-version-string", "ProductName", "Tablet Deck",
    "--set-version-string", "CompanyName", "nodieCode",
    "--set-version-string", "LegalCopyright", COPYRIGHT,
    "--set-version-string", "OriginalFilename", "TabletDeckServer.exe",
    "--set-version-string", "InternalName", "TabletDeckServer",
  ]);
  await inject(exe, "NODE_SEA_BLOB", fs.readFileSync(blob), { sentinelFuse: "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2" });

  step(SIGN_CMD ? "Signing" : "Signing skipped (SIGN_CMD not set)");
  for (const file of [window, exe, ...natives]) sign(file);
}

// Step 6: the installer, from build/app.
function buildInstaller() {
  if (!fs.existsSync(path.join(APP, "TabletDeck.exe"))) throw new Error("build/app is missing; run the --app stage first.");
  fs.mkdirSync(DIST, { recursive: true });
  step("Building the installer");
  const iscc = firstExisting([
    process.env.ISCC,
    path.join(process.env.LOCALAPPDATA || "", "Programs", "Inno Setup 6", "ISCC.exe"),
    path.join(process.env["ProgramFiles(x86)"] || "", "Inno Setup 6", "ISCC.exe"),
    path.join(process.env.ProgramFiles || "", "Inno Setup 6", "ISCC.exe"),
  ]) || "ISCC.exe";
  // Inno Setup signs the installer and uninstaller through a sign tool named "signtool"
  // whose command comes from build/sign.iss, so the command never passes through a
  // command line (where its quotes could get mangled).
  const signIss = path.join(BUILD, "sign.iss");
  if (SIGN_CMD) fs.writeFileSync(signIss, `SignTool=signtool ${SIGN_CMD}\nSignedUninstaller=yes\n`);
  else fs.rmSync(signIss, { force: true });
  run(iscc, ["/Q", `/DAppVersion=${VERSION}`, `/DFileVersion=${FILE_VERSION}`, `/DBuildDir=${BUILD}`, `/O${DIST}`,
    ...(SIGN_CMD ? ["/Ssigntool=$p"] : []), path.join(ROOT, "installer", "TabletDeck.iss")]);
}

// dist/<installer>.sha256, which the app's updater checks downloads against.
function writeChecksum() {
  const hash = crypto.createHash("sha256").update(fs.readFileSync(path.join(DIST, SETUP_NAME))).digest("hex");
  fs.writeFileSync(path.join(DIST, `${SETUP_NAME}.sha256`), `${hash}  ${SETUP_NAME}\n`);
  console.log(`\ndist/${SETUP_NAME}\nSHA-256 ${hash}`);
}

(async () => {
  if (process.platform !== "win32") throw new Error("The installer can only be built on Windows.");
  const stage = ["--app", "--installer", "--checksum"].find((s) => process.argv.includes(s)) || "all";
  console.log(`Building Tablet Deck ${VERSION}${SIGN_CMD ? "" : " (unsigned)"}${stage === "all" ? "" : ` (${stage.slice(2)} stage)`}`);
  if (stage === "all" || stage === "--app") await buildApp();
  if (stage === "all" || stage === "--installer") buildInstaller();
  if (stage !== "--app") writeChecksum();
})().catch((err) => { console.error(err); process.exit(1); });
