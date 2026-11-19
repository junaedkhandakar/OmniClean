// Development helper: verifies the native executable embeds a readable taxonomy.
const fs = require('fs');
const path = require('path');

const exe = path.join(__dirname, '..', 'OmniClean.exe');
if (!fs.existsSync(exe)) {
    console.log('OmniClean.exe not built - run build.bat first');
    process.exit(0);
}

// The embedded resource is stored as a UTF-8 blob inside the PE. Searching
// for the raw UTF-8 bytes of a known category name proves the file was
// embedded without being transcoded.
const bytes = fs.readFileSync(exe);
const needle = Buffer.from('Outdated CapCut App Versions', 'utf8');
const iconNeedle = Buffer.from('🖥️', 'utf8');

console.log('exe size          :', bytes.length);
console.log('name found (utf8) :', bytes.includes(needle));
console.log('icon found (utf8) :', bytes.includes(iconNeedle));

// A mojibake encoding would show up as the ANSI byte sequence instead.
const ansiNeedle = Buffer.from([0xCF, 0xF0, 0x20, 0x41, 0x70, 0x70]); // "Out" in cp1252
console.log('ansi mojibake     :', bytes.includes(ansiNeedle) && !bytes.includes(needle));
