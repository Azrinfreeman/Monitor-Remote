// Design checks: token contrast, CSP-safe markup, and motion rules. Plain Node, no dependencies.
import {readFileSync, readdirSync} from 'node:fs';
const root = new URL('../wwwroot/', import.meta.url);
const read = name => readFileSync(new URL(name, root), 'utf8');
let failures = 0, checks = 0;
const check = (ok, message) => { checks++; if (!ok) { failures++; console.log('FAIL: ' + message); } };

// ---- parse tokens.css into light and dark maps
const css = read('tokens.css');
const darkStart = css.indexOf('@media (prefers-color-scheme: dark)');
const parse = text => Object.fromEntries([...text.matchAll(/--([\w-]+):\s*([^;]+);/g)].map(m => [m[1], m[2].trim()]));
const light = parse(css.slice(0, darkStart));
const dark = {...light, ...parse(css.slice(darkStart, css.indexOf('@media (prefers-contrast', darkStart)))};

function color(value) {
  value = value.trim();
  let m = value.match(/^#([0-9a-f]{6})$/i);
  if (m) return {r: parseInt(m[1].slice(0, 2), 16), g: parseInt(m[1].slice(2, 4), 16), b: parseInt(m[1].slice(4, 6), 16), a: 1};
  m = value.match(/^rgba?\(([^)]+)\)$/);
  if (m) { const p = m[1].split(',').map(Number); return {r: p[0], g: p[1], b: p[2], a: p[3] ?? 1}; }
  throw new Error('Unsupported colour: ' + value);
}
const over = (top, under) => ({r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1});
const lum = c => { const f = v => { v /= 255; return v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4; }; return .2126 * f(c.r) + .7152 * f(c.g) + .0722 * f(c.b); };
const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };

// [foreground token, background token, minimum ratio, description]
const pairs = [
  ['label', 'surface', 4.5, 'primary text on cards'], ['label', 'bg', 4.5, 'primary text on page'],
  ['label-2', 'surface', 4.5, 'secondary text on cards'], ['label-2', 'bg', 4.5, 'secondary text on page'],
  ['label-2', 'surface-2', 4.5, 'secondary text on inset surfaces'],
  ['on-accent', 'accent-fill', 4.5, 'label on filled buttons'],
  ['accent-text', 'surface', 4.5, 'accent text on cards'], ['accent-text', 'bg', 4.5, 'accent text on page'],
  ['accent', 'surface', 3, 'accent graphics (slider fill, selected icon) on cards'],
  ['success', 'surface', 4.5, 'success text'], ['warning', 'surface', 4.5, 'warning text'], ['danger', 'surface', 4.5, 'danger text'],
  ['danger', 'bg', 4.5, 'danger text on page'],
  ['success-dot', 'surface', 3, 'success dot'], ['warning-dot', 'surface', 3, 'warning dot'], ['danger-dot', 'surface', 3, 'danger dot']
];
for (const [name, map] of [['light', light], ['dark', dark]]) {
  for (const [fg, bg, min, what] of pairs) {
    const base = color(map[bg]);
    const surface = over(color(map.surface), color(map.bg));
    const under = bg === 'bg' ? color(map.bg) : bg === 'surface' || bg === 'surface-2' || bg === 'accent-fill' ? over(color(map[bg]), surface) : base;
    const value = ratio(over(color(map[fg]), under), under);
    check(value >= min, `${name}: ${what} ${fg} on ${bg} is ${value.toFixed(2)}:1, needs ${min}:1`);
  }
}

// ---- stylesheet rules
const sheets = readdirSync(root).filter(f => f.endsWith('.css'));
for (const name of sheets) {
  const text = read(name);
  if (name !== 'tokens.css') check(!/#[0-9a-f]{3,8}\b/i.test(text.replace(/\/\*[\s\S]*?\*\//g, '')), `${name}: raw hex colour outside tokens.css`);
  check(!/transition:\s*all\b/.test(text), `${name}: transition: all`);
  check(!/(?:transition|animation)[^;{}]*\b(?:width|height|top|left|margin|padding)\b/.test(text.replace(/grid-template-rows/g, '')), `${name}: animates a layout property`);
}
const motion = read('motion.css');
check(/@media\s*\(prefers-reduced-motion:\s*reduce\)/.test(motion), 'motion.css lacks a prefers-reduced-motion block');
const allCss = sheets.map(n => read(n)).join(' ');
const blurSelectors = new Set([...allCss.matchAll(/([^{}]+)\{[^{}]*backdrop-filter\s*:\s*blur/g)].map(m => m[1].trim()));
const glassUses = (read('index.html').match(/class="[^"]*glass/g) ?? []).length;
check(blurSelectors.size <= 1 && glassUses <= 3, `glass is limited to a few surfaces (${blurSelectors.size} selectors, ${glassUses} elements)`);

// ---- markup rules (the CSP is style-src 'self')
const html = read('index.html');
check(!/\sstyle\s*=/.test(html) && !/<style[\s>]/.test(html), 'index.html contains inline styles, which the CSP blocks');
check(!/\bon[a-z]+\s*=\s*["']/.test(html), 'index.html contains inline event handlers');
for (const file of readdirSync(root).filter(f => f.endsWith('.js')))
  check(!/setAttribute\(\s*['"]style['"]/.test(read(file)), `${file}: sets a style attribute, which the CSP blocks`);

console.log(failures ? `${failures} of ${checks} design checks failed.` : `PASS: ${checks} design checks.`);
process.exit(failures ? 1 : 0);
