// Scans the repository for emoji / pictograph characters.
//
// The UI is meant to stay icon-driven (Segoe Fluent Icons in the native
// edition, inline SVG in the web edition), so any emoji left in a user
// facing string is a regression. Run: node tools/scan-emoji.js
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const TARGETS = [
    'public/index.html',
    'public/app.js',
    'public/style.css',
    'OmniClean.cs',
    'OmniClean.Analysis.cs',
    'OmniClean.Engine.cs',
    'OmniClean.Taxonomy.cs',
    'OmniClean.Resolvers.cs',
    'targets.json',
    'README.md'
];

// The pattern deliberately matches lone variation selectors and skin-tone
// modifiers too, so a sequence that starts with an unusual base still gets
// flagged - hence the disable for the misleading-character-class rule.
// eslint-disable-next-line no-misleading-character-class
const EMOJI = /[\u{1F000}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}\u{1F1E6}-\u{1F1FF}\u{FE0F}\u{1F3FB}-\u{1F3FF}]/gu;

let hits = 0;
for (const rel of TARGETS) {
    const full = path.join(ROOT, rel);
    if (!fs.existsSync(full)) continue;
    const lines = fs.readFileSync(full, 'utf8').split(/\r?\n/);
    lines.forEach((line, i) => {
        const found = line.match(EMOJI);
        if (!found) return;
        hits++;
        console.log(`${rel}:${i + 1}: ${JSON.stringify(found.join(''))} | ${line.trim().slice(0, 120)}`);
    });
}

console.log(hits === 0 ? 'OK: no emoji found.' : `${hits} line(s) with emoji.`);
process.exit(hits === 0 ? 0 : 1);
