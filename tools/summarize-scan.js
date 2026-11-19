// Development helper: prints a summary of the scan output.
const fs = require('fs');

const file = process.argv[2] || 'd:/OmniClean/scan-out.json';
const raw = fs.readFileSync(file, 'utf8');
// PowerShell's Out-File -Encoding utf8 writes a BOM; strip it before parsing.
const data = JSON.parse(raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw);

let detected = 0;
let total = 0;
let reclaimable = 0;

data.groups.forEach((g) => {
    console.log('== ' + g.name);
    g.targets.forEach((t) => {
        total++;
        if (t.detected) {
            detected++;
            reclaimable += t.sizeMB;
        }
        console.log(
            '   ' + t.id.padEnd(22) + (t.detected ? 'OK ' : '-- ') +
            String(t.sizeMB).padStart(10) + ' MB  ' + String(t.paths.length).padStart(3) + ' paths'
        );
    });
});

console.log(`\n${detected}/${total} detected, ${(reclaimable / 1024).toFixed(2)} GB reclaimable`);
console.log('GPU list:', JSON.stringify(data.system.gpus));
