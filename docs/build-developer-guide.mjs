import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Dependency-free PDF renderer for this guide. Text uses PDF base-14 fonts.
const dir = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(dir, 'DEVELOPER_GUIDE.md'), 'utf8');
const output = path.join(dir, 'Monitor-Remote-Developer-Guide.pdf');
const W = 595.28, H = 841.89, margin = 48, width = W - margin * 2;
const pages = []; let commands = [], y = H - 76, code = false;
const ascii = s => s.replace(/[\u2018\u2019]/g, "'").replace(/[\u201c\u201d]/g, '"').replace(/[\u2013\u2014]/g, '-').replace(/[^\x20-\x7e]/g, '?');
const esc = s => ascii(s).replace(/\\/g, '\\\\').replace(/\(/g, '\\(').replace(/\)/g, '\\)');
function label(text, x, at, font, size, color = '0.12 0.18 0.25') {
  commands.push(`BT /${font} ${size} Tf ${color} rg 1 0 0 1 ${x.toFixed(2)} ${at.toFixed(2)} Tm (${esc(text)}) Tj ET`);
}
function finish() {
  label('MONITOR REMOTE  /  DEVELOPER REFERENCE', margin, H - 34, 'F2', 8, '0.15 0.40 0.45');
  commands.push(`0.78 0.85 0.87 RG 0.6 w ${margin} 48 m ${W-margin} 48 l S`);
  label('Source-based guide | 1 October 2026', margin, 32, 'F1', 8, '0.40 0.45 0.50');
  label(String(pages.length + 1), W - margin - 12, 32, 'F2', 9);
  pages.push(commands.join('\n')); commands = []; y = H - 76;
}
// Conservative character widths keep long technical lines within the page.
function wrap(text, size, mono = false, bold = false) {
  const max = Math.floor(width / (size * (mono ? 0.60 : bold ? 0.58 : 0.53)));
  if (mono) {
    const value = ascii(text);
    if (value.length > max) throw Error(`Code line exceeds PDF width; split it in Markdown: ${text}`);
    return [value];
  }
  const words = ascii(text).split(/\s+/); const lines = []; let line = '';
  for (let word of words) {
    if (line && line.length + word.length + 1 > max) { lines.push(line); line = ''; }
    while (word.length > max) { if (line) { lines.push(line); line=''; } lines.push(word.slice(0,max)); word=word.slice(max); }
    line += (line ? ' ' : '') + word;
  }
  if (line) lines.push(line); return lines.length ? lines : [''];
}
const raw = source.split(/\r?\n/);
for (let i = 0; i < raw.length; i++) {
  let line = raw[i];
  if (line === '---') { if (commands.length) finish(); continue; }
  if (line.startsWith('```')) { code = !code; y -= 7; continue; }
  if (!line.trim()) { y -= code ? 3 : 7; continue; }
  const heading = !code && /^(#{1,2}) (.*)/.exec(line);
  const size = code ? 8.4 : heading ? (heading[1].length === 1 ? 21 : 13) : 10.4;
  const font = code ? 'F3' : heading ? 'F2' : 'F1';
  if (heading) line = heading[2];
  else if (!code) {
    while (i+1 < raw.length && raw[i+1].trim() && !/^(#|```|---|[-*] |\d+\. )/.test(raw[i+1]) && !/^[-*] |^\d+\. /.test(line)) line += ' ' + raw[++i];
  }
  const lines = code ? wrap(line, size, true) : wrap(line, size, false, !!heading);
  const leading = code ? 12 : heading ? size * 1.25 : 15;
  const needed = lines.length * leading + (heading ? 13 : 3);
  if (y - needed < 67) finish();
  if (heading) y -= 5;
  for (const item of lines) {
    if (y-leading < 62) finish();
    if (code) commands.push(`0.94 0.96 0.97 rg ${margin-5} ${y-3} ${width+10} ${leading+1} re f`);
    label(item, margin, y, font, size, heading ? '0.08 0.35 0.40' : '0.12 0.18 0.25'); y -= leading;
  }
  y -= heading ? 8 : 3;
}
if (commands.length) finish();
const objects = [null]; const add = s => (objects.push(s), objects.length - 1);
const catalog = add(''), tree = add('');
const fonts = ['Helvetica','Helvetica-Bold','Courier'].map(n => add(`<< /Type /Font /Subtype /Type1 /BaseFont /${n} /Encoding /WinAnsiEncoding >>`));
const kids = [];
for (const stream of pages) {
  const content = add(`<< /Length ${Buffer.byteLength(stream, 'ascii')} >>\nstream\n${stream}\nendstream`);
  kids.push(add(`<< /Type /Page /Parent ${tree} 0 R /MediaBox [0 0 ${W} ${H}] /Resources << /Font << /F1 ${fonts[0]} 0 R /F2 ${fonts[1]} 0 R /F3 ${fonts[2]} 0 R >> >> /Contents ${content} 0 R >>`));
}
objects[catalog] = `<< /Type /Catalog /Pages ${tree} 0 R >>`;
objects[tree] = `<< /Type /Pages /Count ${kids.length} /Kids [${kids.map(k=>`${k} 0 R`).join(' ')}] >>`;
const info = add('<< /Title (Monitor Remote - Developer Build Guide) /Author (Monitor Remote project) /Subject (Architecture, implementation, validation and packaging) >>');
let pdf = '%PDF-1.4\n'; const offsets = [0];
for (let i=1;i<objects.length;i++) { offsets.push(Buffer.byteLength(pdf,'ascii')); pdf += `${i} 0 obj\n${objects[i]}\nendobj\n`; }
const xref = Buffer.byteLength(pdf,'ascii');
pdf += `xref\n0 ${objects.length}\n0000000000 65535 f \n`;
pdf += offsets.slice(1).map(n=>String(n).padStart(10,'0')+' 00000 n \n').join('');
pdf += `trailer\n<< /Size ${objects.length} /Root ${catalog} 0 R /Info ${info} 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
fs.writeFileSync(output, pdf, 'ascii');
// Verify object offsets, stream lengths, page count and chapter coverage.
const actual = fs.readFileSync(output, 'ascii');
if (!actual.startsWith('%PDF-1.4') || !actual.endsWith('%%EOF\n')) throw Error('Invalid PDF envelope');
for (let i=1;i<objects.length;i++) if (!actual.slice(offsets[i]).startsWith(`${i} 0 obj\n`)) throw Error('Invalid xref');
for (const match of actual.matchAll(/\/Length (\d+) >>\nstream\n([\s\S]*?)\nendstream/g)) if (+match[1] !== Buffer.byteLength(match[2],'ascii')) throw Error('Invalid stream length');
for (let i=1;i<=18;i++) if (!actual.includes(`Step ${i} -`)) throw Error(`Missing step ${i}`);
console.log(`Created and structurally verified ${kids.length} pages, ${fs.statSync(output).size} bytes: ${output}`);
