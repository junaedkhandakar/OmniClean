// Development helper: exercises the deep large-file endpoint.
const http = require('http');

const minSizeMB = Number(process.argv[2]) || 2048;

http.get(
    { host: '127.0.0.1', port: 4982, path: `/api/largefiles?drive=C:&minSizeMB=${minSizeMB}&top=8`, headers: { Host: '127.0.0.1:4982' } },
    (r) => {
        let d = '';
        r.on('data', (c) => (d += c));
        r.on('end', () => {
            console.log('STATUS', r.statusCode);
            const j = JSON.parse(d);
            console.log('scannedFiles', j.scannedFiles, 'scannedDirs', j.scannedDirs, 'skippedLinks', j.skippedLinks);
            console.log('totalFound', j.totalFound);
            console.log('TYPES:', j.types.slice(0, 6).map((t) => `${t.ext}=${t.sizeMB}MB`).join(', '));
            console.log('TOP FOLDERS:');
            j.folders.slice(0, 6).forEach((f) => console.log('   ', f.sizeMB + 'MB', f.path));
            console.log('TOP FILES:');
            j.files.slice(0, 8).forEach((f) => console.log('   ', f.sizeMB + 'MB', f.path));
        });
    }
).on('error', (e) => console.log('ERR', e.message));
