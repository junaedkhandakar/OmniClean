// Smoke tests for the OmniClean web edition backend.
//
// Run with:  npm test
//
// These tests start server.js on a dedicated port, verify the API contract,
// prove that the security boundaries hold (command injection, DNS rebinding,
// cross-origin requests, path traversal) and check that cleaner.ps1 really
// receives its categories as separate ids (the bug that used to make the
// Clean button silently do nothing).
//
// Nothing here performs a real cleanup: the only cleaner invocations use
// -DryRun or an invalid category id.

const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn, execFile } = require('node:child_process');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.TEST_PORT) || 47471;
const HOST = `127.0.0.1:${PORT}`;
const isWin = process.platform === 'win32';

let serverChild = null;
let serverLog = '';

// ---- helpers -------------------------------------------------------------

function rawRequest({ path: reqPath, method = 'GET', host = HOST, origin, body = null, headers = {} }) {
    return new Promise((resolve, reject) => {
        const finalHeaders = { Host: host, ...headers };
        if (origin) finalHeaders.Origin = origin;
        if (body) finalHeaders['Content-Length'] = Buffer.byteLength(body);
        if (body) finalHeaders['Content-Type'] = 'application/json';

        const req = http.request(
            {
                host: '127.0.0.1',
                port: PORT,
                path: reqPath,
                method,
                headers: finalHeaders,
                setHost: false
            },
            (res) => {
                const chunks = [];
                res.on('data', (c) => chunks.push(c));
                res.on('end', () =>
                    resolve({
                        status: res.statusCode,
                        headers: res.headers,
                        body: Buffer.concat(chunks).toString('utf8')
                    })
                );
            }
        );
        req.on('error', reject);
        if (body) req.write(body);
        req.end();
    });
}

function runPowerShell(scriptPath, args = [], timeoutMs = 180000) {
    return new Promise((resolve) => {
        execFile(
            'powershell.exe',
            ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, ...args],
            { timeout: timeoutMs, maxBuffer: 10 * 1024 * 1024, windowsHide: true },
            (err, stdout, stderr) => {
                resolve({
                    code: err ? (typeof err.code === 'number' ? err.code : 1) : 0,
                    stdout: stdout || '',
                    stderr: stderr || ''
                });
            }
        );
    });
}

// Category ids declared in targets.ps1 (PowerShell side of the allowlist).
function targetIdsFromPs1() {
    const src = fs.readFileSync(path.join(ROOT, 'targets.ps1'), 'utf8');
    return [...src.matchAll(/^\s*id\s*=\s*'([a-z0-9_]+)'/gm)].map((m) => m[1]);
}

// Category ids declared in server.js (JavaScript side of the allowlist).
function allowlistFromServerJs() {
    const src = fs.readFileSync(path.join(ROOT, 'server.js'), 'utf8');
    const block = src.match(/CATEGORIES = Object\.freeze\(\[([^\]]*)\]/);
    assert.ok(block, 'server.js must declare CATEGORIES = Object.freeze([...])');
    return [...block[1].matchAll(/'([a-z0-9_]+)'/g)].map((m) => m[1]);
}

// ---- server lifecycle ----------------------------------------------------

before(async () => {
    serverChild = spawn(process.execPath, ['server.js'], {
        cwd: ROOT,
        env: { ...process.env, PORT: String(PORT) },
        stdio: ['ignore', 'pipe', 'pipe']
    });
    serverChild.stdout.on('data', (d) => (serverLog += d));
    serverChild.stderr.on('data', (d) => (serverLog += d));

    // Wait until the static server answers.
    const deadline = Date.now() + 15000;
    for (;;) {
        try {
            const res = await rawRequest({ path: '/' });
            if (res.status === 200) break;
        } catch { /* not up yet */ }
        if (Date.now() > deadline) {
            throw new Error(`server.js did not start on port ${PORT}. Log:\n${serverLog}`);
        }
        await new Promise((r) => setTimeout(r, 250));
    }
});

after(() => {
    if (serverChild) serverChild.kill();
});

// ---- static serving ------------------------------------------------------

test('serves the UI and static assets', async () => {
    const page = await rawRequest({ path: '/' });
    assert.equal(page.status, 200);
    assert.match(page.body, /OmniClean/);
    assert.match(page.headers['content-type'], /text\/html/);

    const css = await rawRequest({ path: '/style.css' });
    assert.equal(css.status, 200);
    assert.match(css.headers['content-type'], /text\/css/);
    assert.ok(css.headers['content-security-policy'], 'static responses should carry a CSP header');
});

test('serves the browser UI with the accessibility hooks the app relies on', async () => {
    const page = await rawRequest({ path: '/' });
    assert.match(page.body, /id="confirm-overlay"/, 'the confirmation dialog must exist');
    assert.match(page.body, /id="toggle-dryrun"[^>]*checked/, 'preview mode must default to ON');
    assert.match(page.body, /aria-live="polite"/, 'the log region must announce updates');

    const js = await rawRequest({ path: '/app.js' });
    assert.equal(js.status, 200);
    assert.match(js.headers['content-type'], /javascript/);
});

// ---- path traversal ------------------------------------------------------

test('does not serve files outside public/', async () => {
    const attempts = ['/../server.js', '/%2e%2e/server.js', '/..%5c..%5cserver.js', '/../../cleaner.ps1'];
    for (const p of attempts) {
        const res = await rawRequest({ path: p });
        assert.notEqual(res.status, 200, `path ${p} must not be served`);
        assert.ok(!res.body.includes("require('http')"), `path ${p} leaked server.js source`);
        assert.ok(!res.body.includes('CategoriesCsv'), `path ${p} leaked cleaner.ps1 source`);
    }
});

// ---- request origin/host boundaries --------------------------------------

test('rejects requests with a foreign Host header (DNS rebinding)', async () => {
    const res = await rawRequest({ path: '/api/status', host: 'attacker.example' });
    assert.equal(res.status, 403);
});

test('rejects requests with a foreign Origin (cross-site fetch)', async () => {
    const res = await rawRequest({
        path: '/api/clean',
        method: 'POST',
        origin: 'http://evil.example',
        body: JSON.stringify({ categories: ['recycle_bin'] })
    });
    assert.equal(res.status, 403);
});

test('does not emit permissive CORS headers', async () => {
    const res = await rawRequest({ path: '/api/status' });
    assert.equal(res.headers['access-control-allow-origin'], undefined);
});

// ---- clean endpoint input validation -------------------------------------

test('rejects command-injection payloads in categories', async () => {
    const payloads = [
        ['recycle_bin & echo PWNED'],
        ['recycle_bin" & echo PWNED & rem "'],
        ['$({calc})'],
        ['recycle_bin,temp_files'] // one combined string must not pass either
    ];
    for (const categories of payloads) {
        const res = await rawRequest({
            path: '/api/clean',
            method: 'POST',
            body: JSON.stringify({ categories })
        });
        assert.equal(res.status, 400, `payload ${JSON.stringify(categories)} must be rejected`);
        assert.ok(!res.body.includes('PWNED'));
    }
});

test('rejects well-formed but unknown category ids', async () => {
    const res = await rawRequest({
        path: '/api/clean',
        method: 'POST',
        body: JSON.stringify({ categories: ['definitely_not_a_category'] })
    });
    assert.equal(res.status, 400);
});

test('rejects empty or non-array categories', async () => {
    for (const body of [{}, { categories: [] }, { categories: 'recycle_bin' }, { categories: [42] }]) {
        const res = await rawRequest({ path: '/api/clean', method: 'POST', body: JSON.stringify(body) });
        assert.equal(res.status, 400, `body ${JSON.stringify(body)} must be rejected`);
    }
});

test('rejects an oversized request body', async () => {
    const res = await rawRequest({
        path: '/api/clean',
        method: 'POST',
        body: JSON.stringify({ categories: ['recycle_bin'], pad: 'x'.repeat(32 * 1024) })
    });
    assert.ok(res.status === 413 || res.status === 400, `expected 413/400, got ${res.status}`);
});

test('rejects a malformed drive parameter on /api/status', async () => {
    const res = await rawRequest({ path: '/api/status?drive=..%5Cwindows' });
    assert.equal(res.status, 400);
});

test('schedule endpoint requires a boolean enable flag', async () => {
    for (const body of [{}, { enable: 'yes' }, { enable: 1 }]) {
        const res = await rawRequest({ path: '/api/schedule', method: 'POST', body: JSON.stringify(body) });
        assert.equal(res.status, 400, `body ${JSON.stringify(body)} must be rejected`);
    }
});

// ---- status schema & allowlist parity ------------------------------------

test('GET /api/status returns the documented schema', { skip: !isWin }, async () => {
    const res = await rawRequest({ path: '/api/status' });
    assert.equal(res.status, 200);

    const data = JSON.parse(res.body);
    assert.ok(Number.isFinite(data.disk.totalGB), 'disk.totalGB must be a number');
    assert.ok(Number.isFinite(data.disk.freeGB));
    assert.ok(Number.isFinite(data.disk.percentFree));

    assert.ok(Array.isArray(data.categories) && data.categories.length > 0);
    for (const c of data.categories) {
        assert.equal(typeof c.id, 'string');
        assert.equal(typeof c.name, 'string');
        assert.equal(typeof c.desc, 'string');
        assert.ok(Number.isFinite(c.sizeMB), `category ${c.id} sizeMB must be a number`);
        assert.ok(Array.isArray(c.paths), `category ${c.id} paths must be an array`);
    }

    assert.equal(typeof data.scheduledTask.enabled, 'boolean');
    assert.ok(data.system && typeof data.system.cpu === 'string');
    assert.equal(typeof data.isAdmin, 'boolean');
    assert.ok(Array.isArray(data.drives) && data.drives.length > 0, 'the drive picker needs a drive list');
    for (const d of data.drives) {
        assert.match(d.id, /^[A-Z]:$/);
        assert.equal(typeof d.label, 'string');
    }

    // The disk figures must be internally consistent. A drive list that
    // reuses the same variable names as the active drive silently reports
    // the wrong volume's numbers.
    const { totalGB, freeGB, usedGB, percentFree } = data.disk;
    assert.ok(
        Math.abs(totalGB - freeGB - usedGB) < 0.05,
        `disk numbers must add up (total ${totalGB} - free ${freeGB} != used ${usedGB})`
    );
    const expectedPct = (freeGB / totalGB) * 100;
    assert.ok(
        Math.abs(expectedPct - percentFree) < 0.2,
        `percentFree ${percentFree} does not match ${freeGB}/${totalGB}`
    );

    // The reported drive must be one of the listed drives.
    assert.ok(
        data.drives.some((d) => d.id === data.disk.drive),
        `the reported drive ${data.disk.drive} must appear in the drive list`
    );
});

test('server.js and targets.ps1 declare the same category ids', () => {
    const fromPs = targetIdsFromPs1().sort();
    const fromJs = allowlistFromServerJs().sort();
    assert.deepEqual(fromJs, fromPs, 'CATEGORIES allowlist must match targets.ps1');
    assert.ok(fromPs.length >= 7, 'expected the full category list');
});

// ---- release version parity ----------------------------------------------

test('the native, web and README versions agree', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
    const cs = fs.readFileSync(path.join(ROOT, 'OmniClean.cs'), 'utf8');
    const readme = fs.readFileSync(path.join(ROOT, 'README.md'), 'utf8');

    const native = cs.match(/CURRENT_VERSION\s*=\s*"v?([0-9.]+)"/);
    assert.ok(native, 'OmniClean.cs must declare CURRENT_VERSION');

    assert.equal(
        pkg.version,
        native[1],
        'package.json version must match the native CURRENT_VERSION'
    );

    assert.ok(
        readme.includes(`Version: ${pkg.version}`),
        `README must state "Version: ${pkg.version}"`
    );
});

// ---- cleaner.ps1 argument binding ----------------------------------------

test('cleaner.ps1 receives comma-separated categories as separate ids', { skip: !isWin }, async () => {
    const script = path.join(ROOT, 'cleaner.ps1');
    const res = await runPowerShell(script, ['-Categories', 'recycle_bin,temp_files', '-DryRun']);

    assert.equal(res.code, 0, `dry run should exit 0. Output:\n${res.stdout}\n${res.stderr}`);
    assert.match(res.stdout, /Categories requested: recycle_bin, temp_files/);
    assert.match(res.stdout, /\[DRY RUN\]/);
    assert.match(res.stdout, /Recycle Bin/);
    assert.match(res.stdout, /Temporary System & User Files/);
    assert.match(res.stdout, /nothing was deleted/i);
});

test('cleaner.ps1 rejects unknown category ids', { skip: !isWin }, async () => {
    const script = path.join(ROOT, 'cleaner.ps1');
    const res = await runPowerShell(script, ['-Categories', 'definitely_not_a_category']);

    assert.equal(res.code, 2, `unknown category should exit 2. Output:\n${res.stdout}`);
    assert.match(res.stdout, /Unknown category/);
});

test('scan.ps1 emits parseable JSON on stdout only', { skip: !isWin }, async () => {
    const script = path.join(ROOT, 'scan.ps1');
    const res = await runPowerShell(script);

    assert.equal(res.code, 0, `scan should exit 0. Output:\n${res.stderr}`);
    const data = JSON.parse(res.stdout); // throws if anything but JSON was written
    assert.ok(Array.isArray(data.categories));
});

// ---- dry-run default ------------------------------------------------------

test('/api/clean defaults to dry run and reports that nothing was deleted', { skip: !isWin }, async () => {
    const res = await rawRequest({
        path: '/api/clean',
        method: 'POST',
        body: JSON.stringify({ categories: ['temp_files'] })
    });

    assert.equal(res.status, 200);
    const data = JSON.parse(res.body);
    assert.equal(data.dryRun, true, 'a cleanup must never delete unless dryRun is explicitly false');
    assert.ok(Array.isArray(data.logs) && data.logs.length > 0);
    assert.match(data.logs.join('\n'), /DRY RUN/i);
    assert.ok(Number.isFinite(data.disk.totalGB), 'the response must carry refreshed disk stats');
});
