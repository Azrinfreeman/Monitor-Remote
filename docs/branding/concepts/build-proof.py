"""Rebuild the concept SVGs and self-contained comparison; standard library only."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CONCEPTS = {
    "display-pointer": (
        "Display + pointer",
        '<path d="M25 32H8a3 3 0 0 1-3-3V11a3 3 0 0 1 3-3h32a3 3 0 0 1 3 3v12h-4V12H9v16h16Z"/>'
        '<path d="M19 32h4v6h6v4H13v-4h6Z"/>'
        '<path d="m29 22 15 11-7 2-3 7Z"/>',
        "Recommended: the open screen corner makes room for the action it controls.",
    ),
    "display-phone": (
        "Display + phone",
        '<path d="M26 33H7a3 3 0 0 1-3-3V11a3 3 0 0 1 3-3h30a3 3 0 0 1 3 3v7h-4v-6H8v17h18Z"/>'
        '<path d="M17 33h4v5h5v4H11v-4h6Z"/>'
        '<path fill-rule="evenodd" d="M32 21h10a2 2 0 0 1 2 2v18a2 2 0 0 1-2 2H32a2 2 0 0 1-2-2V23a2 2 0 0 1 2-2Zm1.5 3.5v13h7v-13Z"/>',
        "Alternative: makes the phone-to-PC relationship explicit; more detail at tray size.",
    ),
}

def svg(key, size=48, color="currentColor"):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" '
            f'viewBox="0 0 48 48" fill="{color}" aria-hidden="true">{CONCEPTS[key][1]}</svg>')

sections = []
for key, (name, geometry, description) in CONCEPTS.items():
    (ROOT / f"{key}.svg").write_text(svg(key, 48, "#102d40") + "\n", encoding="utf-8")
    for variant, color in (("dark", "#102d40"), ("white", "#ffffff")):
        lockup = (f'<svg xmlns="http://www.w3.org/2000/svg" width="360" height="64" viewBox="0 0 360 64" '
                  f'role="img" aria-label="Monitor Remote"><g transform="translate(0 8)" fill="{color}">{geometry}</g>'
                  f'<text x="64" y="43" fill="{color}" font-family="Segoe UI Variable, system-ui, sans-serif" '
                  'font-size="32" font-weight="600" letter-spacing="-.8">Monitor Remote</text></svg>\n')
        (ROOT / f"{key}-logo-{variant}.svg").write_text(lockup, encoding="utf-8")
    samples = "".join(
        f'<div class="sample"><div class="surfaces">'
        + "".join(f'<div class="surface {theme}">{svg(key, size)}</div>' for theme in ("light", "dark", "navy"))
        + f'</div><span>{size} px</span></div>'
        for size in (16, 20, 24, 32, 48)
    )
    sections.append(f'''<section><h2>{name}</h2>
    <div class="lockup">{svg(key, 64)}<span>Monitor Remote</span></div>
    <p>{description}</p><div class="samples">{samples}</div>
    <div class="homes"><figure><div class="home">{svg(key, 180)}</div><figcaption>180 px home-screen crop</figcaption></figure>
    <figure><div class="mono">{svg(key, 180)}</div><figcaption>180 px monochrome</figcaption></figure></div>
    <details><summary>Inspect 512 px export-size proof</summary><div class="large navy">{svg(key, 512)}</div></details></section>''')

html = '''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Monitor Remote: identity concepts</title><style>
*{box-sizing:border-box}body{margin:0;color:var(--foreground,#202124);font:15px/1.5 system-ui,sans-serif}
h2{font-size:19px;line-height:1.3;margin:0 0 20px;font-weight:650}p{margin:16px 0 24px;max-width:48em}
section{padding:24px 0 28px;border-bottom:1px solid var(--border,#ccc)}
.lockup{display:flex;align-items:center;gap:16px;color:#102d40;background:#fff;padding:24px 16px;border-radius:8px}
/* avoid-ai-design-ignore: T7 -- local Windows font specified by the approved brief; system fallback intentional in concept proofs. */
.lockup span{font-family:"Segoe UI Variable",system-ui,sans-serif;font-size:clamp(21px,4.4vw,32px);font-weight:600;letter-spacing:-.025em;white-space:nowrap}
svg{display:block;flex:none}.samples{display:flex;flex-wrap:wrap;gap:24px}.sample>span{display:block;margin-top:8px;font-size:13px}
.surfaces{display:flex;flex-direction:column;gap:6px}.surface{display:flex;align-items:center;justify-content:center;min-width:64px;min-height:64px;padding:8px;border-radius:6px}
.light{background:#fff;color:#000}.dark{background:#202124;color:#fff}.navy,.home{background:#102d40;color:#fff}
.homes{display:flex;flex-wrap:wrap;gap:24px;margin-top:28px}figure{margin:0}.home,.mono{width:180px;height:180px;display:grid;place-items:center}.home{border-radius:40px}.mono{background:white;color:black}
figcaption{margin-top:8px;font-size:13px}details{margin-top:24px}summary{cursor:pointer;text-decoration:underline;text-underline-offset:3px;padding:6px 0}summary:focus-visible{outline:2px solid var(--ring,#102d40);outline-offset:4px}
.large{width:512px;height:512px;margin-top:16px}.large svg{max-width:100%;height:auto}details[open]{overflow:auto}
@media(max-width:400px){.samples{gap:12px}.surface{min-width:48px;min-height:56px;padding:4px}.homes{gap:16px}.home,.mono{width:140px;height:140px}.homes svg{width:140px;height:140px}.lockup{padding:20px 12px;gap:10px}.lockup svg{width:48px;height:48px}}
</style></head><body><p>Concept proofs: same navy, type and scale. Black/white rows test the geometry independently of color.</p>'''
html += "".join(sections)
html += '<p>Provisional lockups use live system text. No font files are included. App assets have not been replaced.</p></body></html>'
(ROOT / "comparison.html").write_text(html, encoding="utf-8")
print("Created two editable SVG concepts and comparison.html")
