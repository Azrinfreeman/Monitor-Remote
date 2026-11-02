# Monitor Remote identity implementation

Status: implemented and packaged. Native shell scaling and real iPhone checks remain manual.
Date: 2 November 2026.

The user selected **display + phone** after reviewing two vector concepts. The mark represents controlling a Windows PC from a phone. The app name remains Monitor Remote. This work changes branding assets and their references; functional icons, interface colors and app behavior remain intact.

## Contents

- [Selected design](#selected-design)
- [Files and integration](#files-and-integration)
- [Reproducing exports](#reproducing-exports)
- [Verification](#verification)
- [Remaining checks](#remaining-checks)

## Selected design

The landscape display and portrait phone share a compact silhouette. The open screen corner leaves clear space around the phone. Navy `#102d40` preserves continuity with the previous icon; white provides approximately 14.27:1 contrast. There are no gradients, glow, extra feature symbols or decorative text treatments.

Use the normal master above 32 pixels. Use the optical small master at 16-32 pixels: its square corners and grid-aligned frame simplify the phone opening. Both variants express the same selected concept. The Windows export uses a navy plate with transparent rounded corners for contrast across shell surfaces. Phone PNGs have opaque square backgrounds so the platform can apply its own crop.

Logo type is Segoe UI Variable Semibold, 32 units on a 64-unit-high lockup. Both words use the same weight and color. The symbol occupies a 48-unit canvas; text starts at x=64. Leave at least four units of clear space around the artwork. Use a minimum lockup height of 32 CSS pixels; below that, prefer the symbol. These limits are desktop/browser proofing decisions, not device acceptance claims.

The SVG lockups retain editable text with a system fallback. They require the specified font for exact typography. Use transparent PNG lockups when consistent lettering across systems matters. Font files and converted font outlines are not included. Microsoft permits logo/banner graphic images made using Windows fonts; see the [font redistribution FAQ](https://learn.microsoft.com/en-us/typography/fonts/font-faq). This implementation uses raster graphics and live text rather than inferring permission to distribute outlined font geometry.

## Files and integration

| File | Purpose |
| --- | --- |
| `desktop/assets/monitor-remote.svg` | Selected editable vector master |
| `desktop/assets/monitor-remote-small.svg` | Optical small-size master |
| `desktop/assets/monitor-remote.ico` | Windows frames: 16, 20, 24, 32, 40, 48, 64, 128, 256 pixels |
| `desktop/assets/monitor-remote.png` | 256-pixel desktop preview |
| `desktop/assets/logo/symbol-{dark,white,black}.svg` | Unplated monochrome symbol variants |
| `desktop/assets/logo/monitor-remote-{dark,white}.svg` | Editable horizontal logo lockups |
| `desktop/assets/logo/monitor-remote-{dark,white}.png` | Transparent 2x logo exports, 622 x 128 pixels |
| `desktop/assets/generate-icon.ps1` | SVG-based export entry point |
| `wwwroot/icon-{180,192,512}.png` | Opaque browser/home-screen icons |
| `wwwroot/favicon.ico` | 16, 20, 24, 32, 48-pixel browser icon |
| `wwwroot/manifest.webmanifest` | Added 192-pixel entry; retained identity and theme values |
| `wwwroot/index.html` | Favicon link and decorative pairing image |
| `wwwroot/shell.css` | Pairing image sizing and crop; removed redundant colored enclosure |
| [Final proof](final-preview.html) | Self-contained preview of exported assets |
| [Concept comparison](concepts/comparison.html) | Historical alternatives, before user selection |

Existing desktop references already propagate the ICO to the executable, embedded tray resource and copied `Assets/AppIcon.ico`. The title bar and window continue to consume those paths. The installer uses `SetupIconFile`; shortcut and uninstall icons resolve through the launcher executable. No changes to these consumers were needed.

The pairing image has empty alternative text and is hidden from assistive technology because the visible app name already identifies it. Pairing labels, inputs, IDs and feature glyphs are preserved.

## Reproducing exports

Run from the repository root on Windows with Windows PowerShell and Segoe UI Variable installed:

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File desktop/assets/generate-icon.ps1
```

To verify regeneration without overwriting delivered files:

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File desktop/assets/generate-icon.ps1 -OutputDirectory artifacts/branding-regeneration-check
```

The renderer is Windows WPF in .NET Framework CLR 4.0.30319.42000. It reads the SVG paths through `Geometry.Parse` and rasterizes with `RenderTargetBitmap`. No third-party renderer, package or font was installed. The supported master format is filled paths on a `0 0 48 48` viewBox, with no path transforms or strokes. Unsupported elements fail explicitly. Keep all editable geometry in the masters rather than adding drawing coordinates to the exporter.

Use `concepts/build-proof.py` only to rebuild the historical comparison. It does not generate production assets. The final proof embeds the checked export images and must be refreshed if those images change.

## Verification

- [x] User selected display + phone before production replacement.
- [x] Inspected normal icon, dark/white lockups and all nine decoded ICO frames.
- [x] Checked ICO dimensions, planes, bit depth and transparent corners; home-screen PNGs have correct dimensions and fully opaque backgrounds.
- [x] Regenerated all 13 generated export files to a separate directory; every file matched byte-for-byte.
- [x] Parsed logo SVG XML and manifest JSON; no embedded font binaries or linked raster content in logo SVGs.
- [x] Rendered pairing markup with existing CSS at 320 and 568 pixels in light mode and 390 pixels in dark mode. The proof removes scripts and forces the pairing form visible. Viewport-height declarations on unrelated sheets were replaced with fixed values only in the proof because the inline renderer rejects viewport units. The dark proof explicitly activates the existing dark token block. These are layout checks, not a live connection test.
- [x] Ran `node tests/frontend.mjs`: passed.
- [x] Built `MonitorRemote.csproj`: zero warnings and errors.
- [x] Built the desktop with Release, `win-x64` and self-contained settings through `package.ps1`: zero warnings and errors. An initial runtime-unspecified build failed because WindowsAppSDKSelfContained requires an architecture; the existing package workflow supplies it.
- [x] Generated a fresh portable payload through `build-installer.ps1`; its desktop ICO and all changed web files match the source assets.
- [x] Packaged launcher `--check` passed its pairing, permission, static asset, process lifecycle and synthetic audio checks.
- [x] Compiled the installer with local Inno Setup 6.7.3, and visually inspected the associated icons extracted from both generated executables.
- [x] Ran `git diff --check`: passed. The UI scanner found no P0 findings. Its existing focus warning is a partial-file false positive (`base.css` defines `:focus-visible`); the unrelated viewer metadata separator warning remains outside scope.

Deliverables built:

- Portable launcher: `artifacts/installer-payload-679a1cd700f94f4e84fef73a236b7578/MonitorRemote.Desktop.exe`
- Installer: `artifacts/MonitorRemote-1.0.0-win-x64-Setup.exe`
- Installer SHA256: `54334EC4408804949E9665E9DA255A157FE0CF4C29C7F1004761B364945922BA`

No installation, commit or publication was performed. Existing unrelated changes in audio, README, packaging, installer and listening tests were preserved.

## Remaining checks

- [ ] Inspect the running app's taskbar, Alt+Tab, title bar and tray on light/dark Windows shells at 100%, 150% and 200% scaling. Extracted executable icons prove propagation, not every native shell rendering state. Account for Windows icon caching when checking an existing installation.
- [ ] Check Safari Add to Home Screen on a real iPhone. The rendered rounded crop does not establish device acceptance; an existing shortcut may cache its old icon.

The requested apple-design, avoid-ai-design, ui-unslop, no-slop-ui, ux-color and ui-ux-pro-max skills informed the concept comparison and production review. Existing native typography and the app's established palette take precedence over generic frontend style exclusions. No claim of human authorship or trademark clearance is made.
