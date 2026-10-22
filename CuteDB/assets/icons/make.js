// Generates the CuteDB package icons: one SVG master per package plus PNG renders.
// Usage: npm install sharp@0.33.5 && node make.js <outDir>   (from assets/icons: node make.js .)
// sharp is a tool dependency of this script only; nothing that ships depends on it.
const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const out = process.argv[2];
fs.mkdirSync(out, { recursive: true });

// Palette: nila (indigo ground) and kunyit (turmeric), as in tools/CuteBrowser/Theme/Tokens.axaml
// and src/CuteDB.Cli/Theme.cs.
const base = (badge) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <defs>
    <linearGradient id="ground" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2A3557"/>
      <stop offset="1" stop-color="#0E1424"/>
    </linearGradient>
    <linearGradient id="body" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#C98C2A"/>
      <stop offset="0.35" stop-color="#F2B854"/>
      <stop offset="0.6" stop-color="#E9A83C"/>
      <stop offset="1" stop-color="#A8701C"/>
    </linearGradient>
    <linearGradient id="lid" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FCE3A6"/>
      <stop offset="1" stop-color="#F2B854"/>
    </linearGradient>
    <radialGradient id="glow" cx="0.5" cy="0.45" r="0.5">
      <stop offset="0" stop-color="#E9A83C" stop-opacity="0.35"/>
      <stop offset="1" stop-color="#E9A83C" stop-opacity="0"/>
    </radialGradient>
    <clipPath id="tile"><rect width="512" height="512" rx="116"/></clipPath>
  </defs>

  <g clip-path="url(#tile)">
    <rect width="512" height="512" fill="url(#ground)"/>
    <!-- kawung: the four-petal batik motif, faint, so the ground has a texture with a source -->
    <g fill="#EAE4D6" opacity="0.05">
      ${[[64, 64], [448, 64], [64, 448], [448, 448]].map(([x, y]) => `
      <ellipse cx="${x}" cy="${y - 34}" rx="20" ry="32"/><ellipse cx="${x}" cy="${y + 34}" rx="20" ry="32"/>
      <ellipse cx="${x - 34}" cy="${y}" rx="32" ry="20"/><ellipse cx="${x + 34}" cy="${y}" rx="32" ry="20"/>`).join('')}
    </g>
    <circle cx="256" cy="270" r="210" fill="url(#glow)"/>
  </g>

  <!-- shadow -->
  <ellipse cx="256" cy="432" rx="128" ry="22" fill="#05080F" opacity="0.45"/>

  <!-- the database: two discs of turmeric -->
  <path d="M132,142 L132,372 A124,42 0 0 0 380,372 L380,142 Z" fill="url(#body)"/>
  <path d="M132,232 A124,42 0 0 0 380,232" fill="none" stroke="#7A4E10" stroke-width="9" stroke-linecap="round" opacity="0.9"/>
  <path d="M140,246 A120,40 0 0 0 372,246" fill="none" stroke="#FCE3A6" stroke-width="5" stroke-linecap="round" opacity="0.55"/>
  <ellipse cx="256" cy="142" rx="124" ry="42" fill="url(#lid)"/>
  <ellipse cx="256" cy="142" rx="124" ry="42" fill="none" stroke="#C98C2A" stroke-width="4"/>
  <ellipse cx="226" cy="132" rx="58" ry="12" fill="#FFFFFF" opacity="0.45"/>
  <!-- highlight running down the body -->
  <rect x="168" y="178" width="16" height="200" rx="8" fill="#FFF3D1" opacity="0.35"/>

  <!-- the face -->
  <g>
    <ellipse cx="214" cy="318" rx="15" ry="19" fill="#1A1408"/>
    <ellipse cx="298" cy="318" rx="15" ry="19" fill="#1A1408"/>
    <circle cx="219" cy="310" r="6" fill="#FFFFFF"/>
    <circle cx="303" cy="310" r="6" fill="#FFFFFF"/>
    <ellipse cx="182" cy="348" rx="17" ry="10" fill="#f2685c" opacity="0.6"/>
    <ellipse cx="330" cy="348" rx="17" ry="10" fill="#f2685c" opacity="0.6"/>
    <path d="M240,346 Q256,364 272,346" fill="none" stroke="#1A1408" stroke-width="8" stroke-linecap="round"/>
  </g>

  ${badge}
</svg>`;

const sparkle = (cx, cy, r, color) =>
  `<path d="M${cx},${cy - r} Q${cx + r * 0.18},${cy - r * 0.18} ${cx + r},${cy} Q${cx + r * 0.18},${cy + r * 0.18} ${cx},${cy + r} Q${cx - r * 0.18},${cy + r * 0.18} ${cx - r},${cy} Q${cx - r * 0.18},${cy - r * 0.18} ${cx},${cy - r} Z" fill="${color}"/>`;

// The core package gets sparkles; the others get a badge naming what they add to the engine.
const coin = (glyph) => `
  <circle cx="404" cy="404" r="82" fill="#0E1424"/>
  <circle cx="404" cy="404" r="72" fill="#5fd3a0"/>
  <circle cx="404" cy="404" r="72" fill="none" stroke="#0E1424" stroke-width="3" opacity="0.25"/>
  ${glyph}`;

const icons = {
  'cutedb': `
  ${sparkle(410, 92, 34, '#5fd3a0')}
  ${sparkle(446, 160, 16, '#EAE4D6')}
  ${sparkle(86, 214, 14, '#5fd3a0')}`,

  // A prompt: chevron and cursor.
  'cutedb-cli': `
  ${sparkle(410, 92, 26, '#EAE4D6')}
  ${coin(`<path d="M366,376 L396,404 L366,432" fill="none" stroke="#0E1424" stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="404" y="420" width="42" height="14" rx="7" fill="#0E1424"/>`)}`,

  // Three linked nodes: something to talk to.
  'cutedb-server': `
  ${sparkle(410, 92, 26, '#EAE4D6')}
  ${coin(`<path d="M404,372 L370,428 M404,372 L438,428 M370,428 L438,428" stroke="#0E1424" stroke-width="9" stroke-linecap="round"/>
  <circle cx="404" cy="372" r="15" fill="#0E1424"/><circle cx="370" cy="428" r="15" fill="#0E1424"/><circle cx="438" cy="428" r="15" fill="#0E1424"/>`)}`,

  // A magnifier: the workbench you look into the data with.
  'cutedb-browser': `
  ${sparkle(410, 92, 26, '#EAE4D6')}
  ${coin(`<circle cx="394" cy="394" r="26" fill="none" stroke="#0E1424" stroke-width="12"/>
  <path d="M413,413 L440,440" stroke="#0E1424" stroke-width="15" stroke-linecap="round"/>`)}`,
};

(async () => {
  for (const [name, badge] of Object.entries(icons)) {
    const svg = base(badge);
    fs.writeFileSync(path.join(out, `${name}.svg`), svg);
    // NuGet shows the icon at 64px and recommends 128x128; 256 stays crisp on high-DPI screens.
    await sharp(Buffer.from(svg)).resize(256, 256).png({ compressionLevel: 9 }).toFile(path.join(out, `${name}.png`));
    console.log('wrote', name);
  }
})();
