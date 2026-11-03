// Small, dependency-free exporter for these tutorials (headings, paragraphs, links, lists and code).
// Author Markdown; regenerate HTML. This is deliberately not a general Markdown implementation.
import {readFileSync, writeFileSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const slug = process.argv[2] || 'pc-power';
if (!/^[a-z0-9-]+$/.test(slug)) throw Error('Use a tutorial filename without an extension.');
const root = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(root, slug + '.md'), 'utf8');
const escape = value => value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const inline = value => escape(value).replace(/`([^`]+)`/g,'<code>$1</code>').replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/\[([^\]]+)\]\(([^)]+)\)/g,'<a href="$2">$1</a>');
const output = []; let paragraph = [], list = null, code = null;
function flush() { if (paragraph.length) { output.push('<p>' + inline(paragraph.join(' ')) + '</p>'); paragraph = []; } }
function closeList() { if (list) { output.push('</' + list + '>'); list = null; } }
for (const line of source.split(/\r?\n/)) {
  if (line.startsWith('```')) { flush(); closeList(); if (code !== null) { output.push('<pre><code>' + escape(code.join('\n')) + '</code></pre>'); code = null; } else code = []; continue; }
  if (code !== null) { code.push(line); continue; }
  if (!line.trim()) { flush(); closeList(); continue; }
  const heading = /^(#{1,3}) (.+)$/.exec(line);
  if (heading) { flush(); closeList(); const level = heading[1].length, id = heading[2].toLowerCase().replace(/[^a-z0-9 -]/g,'').replaceAll(' ','-'); output.push(`<h${level} id="${id}">${inline(heading[2])}</h${level}>`); continue; }
  const item = /^(?:([-*]) |\d+\. )(.+)$/.exec(line);
  if (item) { flush(); const type = item[1] ? 'ul' : 'ol'; if (list !== type) { closeList(); output.push('<' + type + '>'); list = type; } output.push('<li>' + inline(item[2]) + '</li>'); continue; }
  closeList(); paragraph.push(line);
}
flush(); closeList(); if (code !== null) throw Error('Unclosed code fence.');
const title = /^# (.+)$/m.exec(source)?.[1] || slug;
const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)}</title>
<style>
  :root{color-scheme:light;--ink:#151515;--muted:#54545c;--accent:#8944ab;--rule:#d6d6dc}
  *{box-sizing:border-box}body{margin:0;color:var(--ink);background:#fff;font:17px/1.55 "Segoe UI",Arial,sans-serif}
  main{max-width:74ch;margin:48px auto;padding:0 28px 40px}header{color:var(--muted);font-size:.85rem;border-bottom:1px solid var(--rule);padding-bottom:12px}
  h1{font-size:2rem;line-height:1.18;letter-spacing:-.02em;margin:28px 0 20px}h2{font-size:1.3rem;line-height:1.3;margin:32px 0 10px}p{margin:0 0 14px}
  li{margin:8px 0}a{color:var(--accent)}code{font-family:Consolas,monospace;font-size:.87em;overflow-wrap:anywhere}pre{white-space:pre-wrap;background:#f3f3f7;padding:16px;border-left:3px solid var(--accent);overflow-wrap:anywhere}
  footer{border-top:1px solid var(--rule);margin-top:32px;padding-top:12px;color:var(--muted);font-size:.85rem}
  @page{size:letter;margin:.65in}@media print{body{font-size:10.5pt}main{margin:0;max-width:none;padding:0}h1,h2{break-after:avoid}pre,li{break-inside:avoid}a{color:inherit}}
</style></head><body><main><header>Monitor Remote / Practical feature tutorial</header>${output.join('\n')}<footer>Generated from <a href="${slug}.md">editable Markdown</a>. Edit the source and regenerate this companion.</footer></main></body></html>`;
writeFileSync(join(root, slug + '.html'), html);
console.log('Exported docs/tutorial/' + slug + '.html');
