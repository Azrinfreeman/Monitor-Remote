# UI revamp implementation plan

Status: **approved; decisions in section 14.** Implementation in progress on branch `feat/ui-revamp`.
Baseline: branch `feat/beyond-lan` at `15d549d`.
Inputs: the current `wwwroot/` and `desktop/` code, plus the guidance in the apple-design (materials, motion), color-and-elevation, ux-color, emil-design-eng, avoid-ai-design and review-animations skills.

## 1. Goal and scope

Redesign the phone controller and the Windows launcher so they look and feel like a native Apple utility: calm, grouped, translucent only where it earns its place, and alive through purposeful motion.

**In scope**
- The phone web app in `wwwroot/` (all five tabs' worth of content: pairing, Displays, Desktop, Audio, plus the new Applications and Listen sections).
- The Windows launcher window and tray in `desktop/`.
- Colour, type, layout, components, motion, and the dark/light appearance.

**Out of scope**
- Any change to server behavior, protocols, permissions, pairing rules or security checks.
- New frameworks, bundlers, npm packages or web fonts. The app stays plain HTML, CSS and JavaScript.
- Shipping Apple assets. SF Pro, SF Symbols and the Apple logo are licensed for Apple platforms only. The plan uses the system font stack (which resolves to SF on iPhone) and hand-drawn icons.
- Claiming Apple affiliation. This is an Apple-inspired design language, not Apple's product.

**Hard constraint: do not break behavior.** All element `id`s, `data-*` attributes, ARIA roles and live regions that `app.js`, `features.js`, `remote.js`, `audio.js`, `apps.js`, `listen.js` and `tests/*.mjs` rely on are kept. JavaScript changes are additive hooks (class toggles, CSS variables, small helpers), not rewrites.

## 2. Step 0: design context (assumptions, change them if wrong)

| Question | Assumption |
|----------|------------|
| Brand | None beyond the existing app icon. The product is a personal utility, so the design stays quiet and the content (a live screen, sliders) is the hero. |
| Audience | One person (you), on an iPhone first, an iPad or desktop browser second. |
| Personality | Refined and calm, not playful. Restrained saturation, one accent. |
| Appearance | Light and dark, following the device setting, designed separately rather than inverted. |
| Market | Single market, so no cultural colour adaptation needed. |
| Accent | iOS system blue family. "Apple colours" means the system palette, so no custom brand hue. |

## 3. What is wrong with the current UI

Observed from `style.css`, `remote.css`, `audio.css` and `index.html`.

| Finding | Effect |
|---------|--------|
| Dark navy only (`#101827`), no light mode, no `prefers-color-scheme`. | Looks like a generic dark dashboard; unreadable in sunlight. |
| One card style (`article`, radius 20, 1 px border) for everything, and one pale-blue filled button style for everything. | No hierarchy: "Turn displays off", "Refresh", and "Start viewing" look equally important. |
| Zero transitions or animations anywhere (searched all CSS and JS). | Panes, tabs, states and errors change instantly; the app feels static and unfinished. |
| Caps, letter-spaced "eyebrow" labels above every card. | A known generic-template tell, and noise. |
| Desktop tab stacks about 20 controls in one scroll column. | Hard to use one-handed; the live screen is pushed off-screen by buttons. |
| Navigation is a pill bar at the top of a long scroll. | Out of thumb reach, and not how iOS apps navigate. |
| Raw status sentences in `monitor-note` paragraphs. | State is read, not seen. |
| Yellow focus ring and yellow "pressed" colour, unrelated to the accent. | A second accent with no role. |

## 4. Design direction

One idea: **"iOS-native utility."** Inset grouped lists, a large collapsing title, a bottom tab bar, bottom sheets for secondary tasks, and glass only on chrome that floats over content.

Rules that keep it from becoming a template:
- No gradients, glows, neon, or decorative blobs. Depth comes from surface tone and hairlines.
- No caps-and-tracking eyebrows. Section headers are sentence case, 13 px, secondary label colour.
- No icon-in-a-rounded-square on every row. Icons only where they carry meaning (tab bar, mute, status).
- Spend the "boldness" in one place: the live-screen viewer and its floating control bar.

### Layout sketches (390 × 844 portrait)

```
Pairing                     Displays                      Desktop (viewing)
┌──────────────────┐        ┌──────────────────┐          ┌──────────────────┐
│                  │        │ Monitor Remote ● │ glass     │ ● Connected   ⤢ │
│      [icon]      │        │ Displays         │ large     ├──────────────────┤
│  Connect to PC   │        ├──────────────────┤ title     │                  │
│  Code from the   │        │ DELL U2723       │          │   live canvas    │
│  PC app          │        │  62%             │          │                  │
│  ┌─┐┌─┐┌─┐┌─┐┌─┐┌─┐       │  ━━━━━●━━━━━     │          │ ┌──────────────┐ │
│  └─┘└─┘└─┘└─┘└─┘└─┘       │  [0][25][50][100]│          │ │ctrl ⤢ fit ± │ │ glass bar
│  [   Connect    ]│        ├──────────────────┤          │ └──────────────┘ │
│                  │        │ Power            │          ├──────────────────┤
│                  │        │  Wake displays   │          │ Touchpad         │
│                  │        │  Turn off      ⏻ │          │ [L][Dbl][R][Drag]│
│                  │        ├──────────────────┤          ├──────────────────┤
│                  │        │ ▭ Displays ▭ Desktop ▭ Audio │ ⌨ Keyboard sheet │
└──────────────────┘        └──────────────────┘          └──────────────────┘
```

## 5. Colour system (ux-color + color-and-elevation)

Role-based tokens in `tokens.css`, declared once for light and once for dark under `prefers-color-scheme`, with `color-scheme: light dark`. Raw hex appears only in this file.

### 60-30-10

- **60 %** neutral: grouped background and large surfaces.
- **30 %** secondary: inset grouped cards, list separators, secondary text.
- **10 %** accent: system blue on the primary action, the selected tab, slider fill and focus ring. Nothing else is blue.

### Tokens (Apple system values; contrast validated before adoption, see section 11)

| Token | Light | Dark | Use |
|-------|-------|------|-----|
| `--bg` (grouped background) | `#F2F2F7` | `#000000` | Page |
| `--surface` (secondary grouped) | `#FFFFFF` | `#1C1C1E` | Cards, list groups |
| `--surface-2` (tertiary) | `#F2F2F7` | `#2C2C2E` | Nested controls, inputs, pressed rows |
| `--label` | `#000000` | `#FFFFFF` | Primary text |
| `--label-2` | `rgba(60,60,67,.60)` | `rgba(235,235,245,.60)` | Secondary text |
| `--label-3` | `rgba(60,60,67,.30)` | `rgba(235,235,245,.30)` | Placeholder and disabled only, never for information |
| `--separator` | `rgba(60,60,67,.29)` | `rgba(84,84,88,.60)` | Hairlines (0.5 px) |
| `--fill` | `rgba(120,120,128,.20)` | `rgba(120,120,128,.36)` | Slider track, segmented background |
| `--accent` | `#007AFF` | `#0A84FF` | Tints, selected state, slider fill |
| `--accent-fill` | to be validated | to be validated | Filled-button background (see below) |
| `--success` | `#34C759` | `#30D158` | Connected, confirmed |
| `--warning` | `#FF9500` | `#FF9F0A` | Connecting, unconfirmed |
| `--danger` | `#FF3B30` | `#FF453A` | Offline, destructive actions, errors |

Decisions:
- **Functional colours stay separate from the accent.** Danger red is never used for branding, and blue is never used for status.
- **System blue on white is only about 4.0:1**, below AA for small text, and white on `#0A84FF` is about 3.7:1. So filled buttons use a separate `--accent-fill` chosen by the validation script (the nearest shade that gives 4.5:1 with the label colour), while `--accent` is used for tints, the slider fill and large or bold text.
- **Status is never colour alone.** Every dot has a text label and, where relevant, an icon.
- **Elevation in dark mode is lighter surfaces** (`#000` to `#1C1C1E` to `#2C2C2E`), not shadows. In light mode, cards use no shadow at all, only the surface/background step; one soft shadow (`0 1px 2px rgba(0,0,0,.06)`) is allowed on floating glass.
- **Focus ring** is a 3 px `--accent` outline with a 2 px offset, replacing the yellow ring. A white inner ring is added so it is visible on blue buttons.
- `meta[name=theme-color]` is declared twice with `media="(prefers-color-scheme: …)"`.

## 6. Typography (typography-and-layout, apple-design)

- **Stack:** `-apple-system, BlinkMacSystemFont, "SF Pro Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif`. On iPhone this is SF Pro automatically; nothing is downloaded. Numeric readouts use `font-variant-numeric: tabular-nums` and `ui-rounded` where available for the big percentage.
- **Dynamic Type:** base text uses `font: -apple-system-body` where supported (falls back to 17 px), and sizes are in `rem`, so the iPhone text-size setting scales the whole UI. Layouts are tested at the largest accessibility size.
- **Scale (px at default size):** Large title 34/41 bold · Title 22/28 semibold · Headline 17/22 semibold · Body 17/22 · Subhead 15/20 · Footnote 13/18 · Caption 12/16. Negative tracking on large sizes (about -0.02 em), none on body.
- **Inputs** stay at least 16 px so Safari does not zoom on focus.
- Line length: single column, so measure is naturally under 75 characters.

## 7. Materials and elevation (apple-design-materials)

Glass is a layer, not a paint. It is used on **exactly three** surfaces:
1. The collapsing large-title bar (once scrolled).
2. The bottom tab bar.
3. The floating control bar over the remote viewer, and sheets' grabber area.

Recipe: `backdrop-filter: blur(20px) saturate(180%)`, translucent `--surface` fill at about 72 % opacity, a 0.5 px top highlight and a hairline separator.

Rules:
- Never stack glass on glass.
- **No `backdrop-filter` over the live canvas.** Blurring a continuously changing 1280×720+ image costs real GPU time on a phone. The viewer's control bar uses a more opaque translucent fill without blur.
- `@media (prefers-reduced-transparency: reduce)` and `@supports not (backdrop-filter: blur(1px))` fall back to solid `--surface`.
- `@media (prefers-contrast: more)` strengthens separators and removes translucency.
- Corners are continuous-looking: radius 12 (controls), 16 (cards), 22 (sheets). `corner-shape: squircle` is used progressively where supported; plain `border-radius` elsewhere.

## 8. Information architecture and components

### 8.1 App shell
- **Header:** large title ("Monitor Remote") that collapses into a small centred title with a glass bar after about 40 px of scroll. The **connection chip** (dot plus "Connected" / "Reconnecting…" / "PC unreachable") lives in the bar; Reconnect becomes a plain text button inside the chip's offline state.
- **Bottom tab bar** (Displays, Desktop, Audio) with icons and labels, safe-area padded, replacing the top pill bar. The selected item uses `--accent`. A sliding indicator moves between items.
- Tabs keep their current ids (`tab-monitors`, `tab-desktop`, `tab-audio`, `feature-tabs`) and `aria-pressed` contract. The `Refresh readings` and `Disconnect` actions move into a "…" menu sheet in the header (ids kept), because they are infrequent.

### 8.2 Pairing
- Full-screen centred layout: app icon, title, a single line of help, six digit cells, Connect.
- **One real `<input id="code">`** stays (so autofill and `autocomplete="one-time-code"` keep working). It is visually transparent and overlays six decorative cells that mirror its value via a small script.
- States: idle, submitting, **waiting for PC approval** (new from phase 2, shown as a breathing ring and the text "Approve this phone on your PC"), error, success.

### 8.3 Displays
- Each monitor is an inset grouped card: name and resolution, large percentage readout, slider, and a **segmented control** for presets (0 / 25 / 50 / 75 / 100).
- Power is a grouped list of two rows: "Wake displays" and "Turn displays off" (destructive red label). The row text replaces the explanatory paragraph; the unconfirmed-state note becomes a footnote under the group.

### 8.4 Desktop (largest rework)
Two states instead of one long stack:
1. **Setup card:** display and quality as two grouped rows (tapping opens the native `<select>` picker), then a full-width **Start viewing** button.
2. **Viewing:** the card collapses; the viewer fills the width. Controls are redistributed:
   - **Floating control bar** over the viewer: Enable control (toggle with a visible state), Expand, Fit, Zoom −/+. Auto-hides after 3 s of inactivity in expanded mode and returns on tap.
   - **Touch mode** as a two-option segmented control.
   - **Input actions** (Left, Double, Right, Drag) as a compact four-button row under the touchpad.
   - **Keyboard** moves from a `<details>` into a **bottom sheet** with a grabber and two detents (compact: text compose; expanded: full key grid). Same element ids inside.
   - The gesture help text becomes a dismissible coach mark shown once (remembered in `localStorage`), not a permanent paragraph.
   - **Expanded view** is a true immersive mode: header and tab bar slide away, canvas fills the safe area; landscape uses it by default.
- When control is enabled, the viewer gets a thin `--accent` ring. This doubles as a safety cue that input will reach the PC.

### 8.5 Audio
- **PC volume** is a hero card: big readout, slider, mute button with an animated speaker symbol.
- **Listen to PC sound** is a prominent card with a state-aware button, a live level indicator while listening, and the privacy line as a footnote.
- **Playback devices, microphones, Applications** become inset grouped lists. Each application row shows a neutral letter avatar (first letter, single neutral tone, not multicolour), the name, the device as subtitle, the slider, and a trailing mute button.
- "All playback devices" collapses into a disclosure row so it stops pushing the lists down.

### 8.6 Shared components
Filled, tinted, plain and destructive buttons (44 px minimum touch target, 50 px for primary); segmented control; list row; grouped section with header/footer; toggle chip for `aria-pressed` buttons; custom range slider; bottom sheet; status chip; inline status line (icon plus text, `role="status"` kept); empty state.

**Slider:** a 6 px track, 28 px white thumb with a soft shadow, fill driven by a `--fill` CSS variable that JS sets on `input`. Native `<input type=range>` is kept, so touch, accessibility and the existing JS keep working.

**Icons:** one hand-drawn inline SVG `<symbol>` sprite (about 16 icons, 24 px grid, 1.75 px stroke, round caps) referenced with `<use>`. The Content-Security-Policy is `style-src 'self'`, so the sprite uses presentation attributes and classes, never inline `style`.

## 9. Motion system (apple-design-motion, emil-design-eng, animate)

### 9.1 Principles
- Animate only `transform` and `opacity`. Height changes use the `grid-template-rows: 0fr → 1fr` technique, not `height`.
- Springs, not generic easing: CSS `linear()` curves generated from spring parameters, as tokens.
- Interruptible: respond on `pointerdown`, never block input behind an animation.
- No `transition: all`.
- Everything has a reduced-motion alternative.

### 9.2 Tokens
| Token | Value | Use |
|-------|-------|-----|
| `--ease-out` | `cubic-bezier(.23,1,.32,1)` | Enter, press release |
| `--spring-smooth` | `linear()` spring, response .5 s, damping 1.0 | Tab and pane transitions, sheets settling |
| `--spring-snappy` | `linear()` spring, response .3 s, damping .85 | Tab indicator, segmented thumb, toggles |
| `--spring-bouncy` | `linear()` spring, response .5 s, damping .7 | Used once: pairing success. Nowhere else. |
| `--dur-press` | 120 ms | Press feedback |
| `--dur-ui` | 200–240 ms | Fades, small moves |
| `--dur-sheet` | 320 ms | Sheets, immersive mode |

### 9.3 Frequency check (what does not animate)
| Interaction | Decision |
|-------------|----------|
| Slider dragging, readout numbers | No animation on the value. Only the thumb scales 1.0 to 1.12 on grab (120 ms). |
| Key presses, touchpad, scroll strip, clicks sent to the PC | No animation beyond a press state. They are used hundreds of times. |
| Live canvas frames | Never animated. |
| Polling refreshes (devices, apps) | No re-animation of existing rows; only new or removed rows animate. |
| Tab switching | Short cross-fade only (about 200 ms), because it is frequent. |

### 9.4 Where motion goes (purpose for each)
| Where | Motion | Purpose | Spec |
|-------|--------|---------|------|
| Every button and row | Press: `scale(.97)` and dim | Feedback | 120 ms, `--ease-out`, on `:active` |
| Tab change | Pane cross-fade with 8 px rise; tab-bar indicator slides | Spatial continuity | View Transitions API (`document.startViewTransition`) with a JS-class fallback; indicator uses `--spring-snappy` |
| Segmented controls | Thumb slides to the selection | State change | `--spring-snappy`, transform only |
| Connection chip | Dot colour cross-fades; pulses while "connecting"; one soft ring ping on connect | State | Pulse only while connecting, stops on connect |
| Pairing digits | Cell pops from .9 to 1 as each digit is typed | Input feedback | 140 ms |
| Pairing error | One horizontal shake, ±6 px | Error | 3 cycles, 300 ms; reduced-motion: border flash |
| Pairing waiting | Breathing ring | "Something is happening on the PC" | 2 s loop, only while waiting |
| Pairing success | Check mark draws, then the pairing screen cross-fades to the app | Completion | 400 ms stroke draw, the only use of `--spring-bouncy` |
| First load of a pane | Cards rise 8 px and fade in with a 40 ms stagger | Orientation | Once per pane per session, max 6 items |
| New or removed application row | Row expands/collapses | Continuity | `0fr → 1fr`, 250 ms |
| Mute button | Speaker waves cross-fade to a slashed speaker | State | 150 ms |
| "Confirmed by Windows" | Small check pops beside the text | Confirmation | 200 ms |
| Failed change | Row text shakes once | Error | 300 ms |
| Start viewing | Setup card collapses; viewer scales .98 to 1 and fades in; first frame fades in | Continuity from the card | 280 ms |
| Control enabled | Accent ring fades in around the viewer | Safety cue | 200 ms |
| Expanded view | Header and tab bar slide out, canvas fills | Immersion | 320 ms `--spring-smooth` |
| Floating control bar | Fades after 3 s idle (expanded only), returns on tap | Declutter | 200 ms |
| Keyboard sheet | Slides up with a backdrop dim; drag to dismiss with release velocity | Natural gesture | `--spring-smooth`; follows the finger 1:1 while dragging |
| Listening | Equalizer bars move with real signal level (a Web Audio analyser on the output); idle gentle loop if no signal | Shows it is live | Transform `scaleY` only; paused when the page is hidden |
| Sync/refresh buttons | Icon rotates once while busy | Progress | Only while the request runs |
| Header | Large title collapses into the glass bar | Scroll feedback | Scroll-linked via `animation-timeline: scroll()` where supported, otherwise an `IntersectionObserver` class toggle |

### 9.5 Accessibility of motion
- `@media (prefers-reduced-motion: reduce)`: remove translations, scales, shakes, pulses and stagger; keep only opacity fades of 100 ms or less, and static state changes. The equalizer becomes a static level bar.
- No flashing; nothing animates faster than 3 per second.
- Looping animations (connecting pulse, breathing ring, equalizer) exist only while their state is active.

## 10. Windows launcher (`desktop/`)

The launcher is WinForms. A pixel-faithful iOS look is not possible there, so the goal is an **Apple-inspired, Windows-respecting** window.

### Recommended approach: restyle WinForms (option A)
- **Layout:** replace the single long scrolling column with a three-section window: a header strip (status dot, "Running" / "Stopped", Start/Stop), a two-column body (left: QR code, URL, pairing code and expiry; right: access toggles), and a collapsible "Advanced" area (port, startup options, diagnostics).
- **Toggles:** a small custom `ToggleSwitch` control replaces the four permission check boxes (same commands: `view`, `input`, `audio`, `sound`), with a knob that slides over about 120 ms using a timer.
- **Cards:** rounded panels with surface/background tones, painted in `OnPaint` with anti-aliasing.
- **Window:** Mica backdrop and immersive dark mode through `DwmSetWindowAttribute` on Windows 11, following the system light/dark setting; plain system colours on older builds.
- **Type:** Segoe UI Variable. Apple fonts are not shipped.
- **Colour:** the same token values as the phone UI, mapped to light/dark.
- **Motion (small and purposeful):** status dot colour fade, QR code fade-in when the server is ready, toggle knob slide, a brief highlight when the pairing code renews. No animation on timers or polling.
- **Approval prompt:** the `MessageBox` for off-network pairing becomes a small themed dialog that shows the device, address and a countdown.
- **Tray menu:** themed to follow system dark mode.

### Alternative (option B), not recommended now
Rebuild in WinUI 3. This gets Mica, rounded controls and animations natively, but it changes the project type, packaging (MSIX or Windows App SDK), the `package.ps1` flow and the launcher checks, and WinUI's native look is Fluent rather than Apple. It is a separate project.

## 11. Implementation steps

New CSS replaces the three current files. Files under `wwwroot/`:

| File | Content |
|------|---------|
| `tokens.css` | Colour, type scale, radius, spacing, motion tokens, dark/light, contrast and transparency media |
| `base.css` | Reset, typography, focus ring, `[hidden]`, safe areas |
| `components.css` | Buttons, segmented control, rows, groups, slider, sheet, chip, toggles |
| `shell.css` | Header, tab bar, pane layout, pairing screen |
| `displays.css`, `desktop.css`, `audio.css` | Per-pane layout |
| `motion.css` | Keyframes, transitions, view-transition rules, reduced-motion block |
| `ui.js` | Small helpers: slider fill variable, segmented control thumb, sheet drag, header collapse, coach mark, digit cells |
| `icons.svg` (inlined into `index.html`) | The `<symbol>` sprite |
| `manifest.webmanifest` | Optional: standalone "Add to Home Screen", with `apple-touch-icon` from `desktop/assets/monitor-remote.png` |

Phases (each is a commit and can be reviewed on a phone):

| Phase | Work | Check at the end |
|-------|------|------------------|
| 0. Foundations | `tokens.css`, `base.css`, the contrast script, type scale, icon sprite | Script passes every text/surface pair in both modes |
| 1. Shell and pairing | Large title, glass bar, bottom tab bar, connection chip, pairing screen with digit cells and all states | Pairing works, including the approval-wait state |
| 2. Displays | Monitor cards, segmented presets, power list, slider | Brightness and power behave as before |
| 3. Audio | PC volume hero, Listen card with level meter, grouped lists, Applications rows | Volume, mute, apps and listen behave as before |
| 4. Desktop | Setup card, viewer, floating bar, input row, keyboard sheet, immersive mode, coach mark | Viewing, touchpad, typing and expand behave as before |
| 5. Motion and accessibility pass | Section 9 motion, reduced-motion/transparency/contrast, Dynamic Type, review-animations audit | Checklist in section 12 |
| 6. Launcher | Section 10 option A | Launcher checks pass; screenshots in light and dark |
| 7. Docs | README screenshots, `docs/DESIGN.md` (tokens and rules), `PROJECT_STATE.md` | Docs match the shipped UI |

## 12. Verification

**Automated (kept green throughout)**
- `tests/smoke.mjs`, `frontend.mjs`, `features.mjs`, and the 67-check server suite. These depend on element ids and behavior, so they catch accidental breakage.
- New `tests/design-checks.mjs` (plain Node, no dependencies):
  - computes contrast for every declared foreground/background token pair in light and dark (WCAG 4.5:1 for text, 3:1 for UI components and large text) and fails on a miss;
  - fails on raw hex outside `tokens.css`, `transition: all`, any `style=` attribute or `<style>` in HTML (the CSP forbids them), and animation of `width`, `height`, `top` or `left`;
  - fails if `motion.css` lacks a `prefers-reduced-motion` block;
  - fails if more than three elements use `backdrop-filter`.
- The avoid-ai-design scanner (`scripts/detect.mjs`) run over `wwwroot/`, with no P0/P1 findings.

**Visual QA (preview tool, synthetic demo mode)**
- Matrix: 375×667, 390×844, 844×390 landscape, iPad width, desktop; light and dark; default and largest text size; reduced motion on; reduced transparency on.
- Every pane and every state: connecting, offline, unpaired, waiting for approval, error, empty lists, long device names, many applications.
- No horizontal overflow, no clipped text, no captured JavaScript errors.

**review-animations checklist** (run on the finished CSS/JS): purpose per animation, no input-blocking, interruptible, transform/opacity only, durations under 300 ms for UI, reduced-motion alternative present, no looping animation outside its state.

**Manual on a real iPhone** (cannot be automated, and not yet done for any phase): safe areas and Dynamic Island, home-indicator spacing, tab bar reachability, sheet drag against page scroll, canvas and touchpad gestures unchanged, scroll performance while the stream plays, battery and heat during a ten-minute view, Add to Home Screen standalone mode, and Listen audio start from the tap.

## 13. Risks

| Risk | Mitigation |
|------|------------|
| CSP `style-src 'self'` blocks inline styles and `<style>`. | Sprite uses attributes and classes; dynamic values go through CSS variables set via `element.style.setProperty` (allowed), never `setAttribute('style')`. A design check enforces it. |
| Glass over the live canvas hurts performance. | No blur over the canvas; capped at three glass surfaces; measured on a real phone. |
| Sheet drag fights page scroll and the canvas touch handlers. | Sheet drag starts only from the grabber; `touch-action` on the canvas and touchpad stays untouched. |
| Layout rework breaks JS that expects the old DOM. | Ids and `data-*` kept; existing tests run after each phase. |
| Immersive mode hides controls needed in an emergency. | The Stop viewing action stays reachable by tapping the screen (control bar returns), and Stop in the PC app is unchanged. |
| System blue fails contrast for small text. | Separate `--accent-fill` and `--accent` tokens, chosen by the validation script. |
| WinForms custom painting can flicker or mis-scale at high DPI. | Double-buffered controls, DPI-aware layout, tested at 100/150/200 %. |
| Copying Apple's look too literally. | No SF Pro/Symbols files, no Apple marks; icons are original. |

## 14. Decisions (recorded 2026-10-31)

1. Appearance: follow the device (light and dark). Not answered explicitly; the default was kept.
2. Accent: **light purple** (Apple system purple family) instead of blue. Wherever this document says "system blue", read "system purple". The same contrast rule applies: filled buttons use a separate validated `--accent-fill`.
3. Launcher: **rebuild in WinUI 3** (option B), using the winui-* skills. This replaces option A in section 10 and changes packaging; see the launcher phase in the implementation status.
4. Add to Home Screen support: yes.
5. Desktop tab: viewer-first layout (section 8.4).
6. Phase order: as listed.

## Implementation status (2026-10-31)

| Phase | State | Notes |
|-------|-------|-------|
| 0 Foundations | Done | `tokens.css` (light/dark, purple accent), `base.css`, `components.css`; `tests/design-checks.mjs` validates 57 rules including contrast for every token pair in both modes. Filled buttons use `--accent-fill` `#8944AB`; the lighter `#AF52DE` is used for fills, rings and graphics only. |
| 1 Shell and pairing | Done | Large title that collapses into a glass bar, bottom tab bar with sliding indicator, connection status with ping/pulse, digit-cell pairing screen with waiting, error and success states, "Connection" sheet. |
| 2 Displays | Done | Grouped brightness cards, segmented presets, power list. |
| 3 Audio | Done | PC volume card, Listen card with a level meter driven by the real output level, grouped device and application lists. |
| 4 Desktop | Done, partly | Viewer-first layout, control bar, keyboard bottom sheet with drag-to-dismiss, immersive expand with an auto-hiding bar, coach mark shown once. Touch mode is a segmented control driving the original select. |
| 5 Motion and accessibility | Done, partly | Reduced-motion, reduced-transparency and high-contrast handling are in. The View Transitions API was not used; a CSS animation does the pane transition. Not tested at the largest Dynamic Type size on a real iPhone. |
| 6 Launcher | Done | Rebuilt in WinUI 3 (unpackaged, self-contained Windows App SDK 2.6) so the portable folder and zip still work with no signing. Mica backdrop, native toggle switches, QR fade-in, status-dot colour transition, approval dialog, tray icon through `Shell_NotifyIcon`. |
| 7 Docs | Partly | This status table. README screenshots not yet taken. |

Deviations and things to know:
- **Unpackaged WinUI.** The winui-dev-workflow guidance prefers a packaged (MSIX) app. This project ships as an unsigned portable folder, so the launcher is unpackaged (`WindowsPackageType=None`). It still uses the Windows App SDK runtime, bundled self-contained.
- **`package.ps1` builds the launcher instead of publishing it.** An unpackaged WinUI `dotnet publish` omitted the compiled XAML (`.xbf`) and resource index (`.pri`), which crashed the app at startup with a XAML parse error. The package is about 340 MB because the Windows App SDK is bundled.
- **Windows Forms cannot be mixed in** (the WPF markup targets it pulls in break the WinUI XAML compiler), so the tray icon is a small direct `Shell_NotifyIcon` wrapper (`desktop/Tray.cs`).
- **Verification.** Phone UI: headless Edge screenshots (`tests/screenshots.mjs`) in light and dark, portrait, landscape and iPhone SE size, with no JavaScript errors. Launcher: the 21 packaged checks pass and the window was launched and inspected on this PC. Not done: real iPhone testing, tray menu and approval dialog exercised by hand, ARM64.

Update (same day): the launcher now also has a Paired phones list with per-phone Revoke, and HTTPS settings in Advanced (certificate file with Browse, password stored encrypted with DPAPI for the current Windows user, hostnames, port, plain-HTTP toggle). The tab change uses the View Transitions API where the browser supports it. These were built and compile, and the launcher starts, but the two new launcher sections have not been looked at on screen: a Windows Firewall prompt for the new build covered the window during the check.
