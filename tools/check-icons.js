// Development helper: prints the raw bytes of an icon value from the scan output.
const fs = require('fs');
const file = process.argv[2] || 'd:/OmniClean/scan-out.json';
const raw = fs.readFileSync(file);
const text = raw.toString('utf8');
const clean = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;

const data = JSON.parse(clean);
data.groups.forEach((g) => {
    const codes = [...g.icon].map((c) => 'U+' + c.codePointAt(0).toString(16).toUpperCase().padStart(4, '0'));
    console.log(g.id.padEnd(12), JSON.stringify(g.icon), codes.join(' '));
});
