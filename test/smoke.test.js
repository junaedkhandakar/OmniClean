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

// The cleanup taxonomy (targets.json) is the single source of truth for
// both editions. These helpers read it directly.
function taxonomy() {
    return JSON.parse(fs.readFileSync(path.join(ROOT, 'targets.json'), 'utf8'));
}

function taxonomyTargetIds() {
    const ids = [];
    for (const group of taxonomy().groups) {
        for (const target of group.targets) ids.push(target.id);
    }
    return ids;
}

// Category ids the server derives from the taxonomy.
function allowlistFromServer() {
    const { categoryIds } = require('../lib/taxonomy');
    return categoryIds();
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
        assert.equal(typeof c.detected, 'boolean', `category ${c.id} needs a detected flag`);
    }

    // The groups mirror the taxonomy and cover every category exactly once.
    assert.ok(Array.isArray(data.groups) && data.groups.length > 0, 'the UI needs grouped categories');
    const groupedIds = [];
    for (const g of data.groups) {
        assert.ok(g.id && g.name, 'each group needs an id and a name');
        assert.ok(Array.isArray(g.targets));
        groupedIds.push(...g.targets.map((t) => t.id));
    }
    assert.deepEqual(
        groupedIds.slice().sort(),
        data.categories.map((c) => c.id).sort(),
        'the grouped list must cover every category exactly once'
    );

    // Every category id in the taxonomy must be present.
    const expectedIds = taxonomyTargetIds().slice().sort();
    assert.deepEqual(
        data.categories.map((c) => c.id).sort(),
        expectedIds,
        'every taxonomy target must be reported by the scan'
    );

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

test('server.js and targets.json declare the same category ids', () => {
    const fromTaxonomy = taxonomyTargetIds().sort();
    const fromServer = allowlistFromServer().sort();
    assert.deepEqual(fromServer, fromTaxonomy, 'the server allowlist must match targets.json');
    assert.ok(fromTaxonomy.length >= 20, 'expected a substantial category list');
});

test('the taxonomy covers graphics caches for every major GPU vendor', () => {
    const ids = taxonomyTargetIds();
    // The point of the taxonomy is that graphics cleanup is not NVIDIA-only.
    for (const required of [
        'gpu_shader_cache',
        'nvidia_artifacts',
        'amd_artifacts',
        'intel_artifacts',
        'directx_cache',
        'vulkan_cache'
    ]) {
        assert.ok(ids.includes(required), `targets.json must define "${required}"`);
    }
});

test('the taxonomy covers several browser families and app families', () => {
    const ids = taxonomyTargetIds();
    for (const required of [
        'browser_cache',
        'browser_service_worker',
        'discord_cache',
        'teams_cache',
        'app_shelf_cache',
        'game_shader_cache',
        'dev_cache',
        'editor_caches'
    ]) {
        assert.ok(ids.includes(required), `targets.json must define "${required}"`);
    }
});

test('the taxonomy never points at a protected system location', () => {
    const forbidden = [
        'WinSxS',
        'Installer\\$PatchCache$',
        'System32\\DriverStore',
        'Docker\\wsl',
        'Package Cache'
    ];
    for (const group of taxonomy().groups) {
        for (const target of group.targets) {
            for (const p of target.paths || []) {
                for (const bad of forbidden) {
                    assert.ok(
                        !p.toLowerCase().includes(bad.toLowerCase()),
                        `"${target.id}" points at a protected location: ${p}`
                    );
                }
            }
        }
    }
});

test('every taxonomy target has an id, name, description and kind', () => {
    const kinds = new Set(['paths', 'recycle', 'browser_cache', 'gpu_cache', 'capcut_old', 'empty_dirs', 'glob', 'old_app_versions', 'editor_cache']);
    const seen = new Set();
    for (const group of taxonomy().groups) {
        for (const target of group.targets) {
            assert.match(target.id, /^[a-z][a-z0-9_]*$/, `bad id: ${target.id}`);
            assert.ok(target.name && target.name.length > 2, `${target.id} needs a name`);
            assert.ok(target.desc && target.desc.length > 5, `${target.id} needs a description`);
            assert.ok(kinds.has(target.kind), `${target.id} has an unknown kind: ${target.kind}`);
            assert.ok(!seen.has(target.id), `duplicate target id: ${target.id}`);
            seen.add(target.id);
        }
    }
});

// ---- taxonomy endpoint ----------------------------------------------------

test('GET /api/taxonomy returns the grouped catalogue', async () => {
    const res = await rawRequest({ path: '/api/taxonomy' });
    assert.equal(res.status, 200);

    const data = JSON.parse(res.body);
    assert.ok(Array.isArray(data.groups) && data.groups.length > 0);
    for (const group of data.groups) {
        assert.equal(typeof group.id, 'string');
        assert.equal(typeof group.name, 'string');
        assert.ok(Array.isArray(group.targets) && group.targets.length > 0);
        for (const target of group.targets) {
            assert.match(target.id, /^[a-z][a-z0-9_]*$/);
            assert.ok(typeof target.kind === 'string' && target.kind.length > 0);
        }
    }
});

// ---- large file scan ------------------------------------------------------

test('/api/largefiles rejects an invalid drive', async () => {
    const res = await rawRequest({ path: '/api/largefiles?drive=..%5Cwindows' });
    assert.equal(res.status, 400);
});

test('/api/largefiles rejects an out-of-range size filter', async () => {
    for (const value of ['0', '-5', '999999']) {
        const res = await rawRequest({ path: `/api/largefiles?minSizeMB=${value}` });
        assert.equal(res.status, 400, `minSizeMB=${value} must be rejected`);
    }
});

test('largefiles.ps1 emits a parseable report', { skip: !isWin }, async () => {
    // A very high threshold keeps the test quick while still proving the
    // deep walker works end to end. The walk itself is what costs time, so
    // the timeout has to be generous.
    const script = path.join(ROOT, 'largefiles.ps1');
    // MaxDirs bounds the walk so the suite stays fast on machines with
    // millions of files; the report contract is what this test proves.
    const res = await runPowerShell(script, ['-Drive', 'C:', '-MinSizeMB', '102400', '-Top', '5', '-MaxDirs', '20000'], 300000);

    assert.equal(res.code, 0, `largefiles.ps1 should exit 0. Output:\n${res.stderr}`);
    const data = JSON.parse(res.stdout);
    assert.equal(data.drive, 'C:');
    assert.ok(Number.isFinite(data.scannedFiles), 'scannedFiles must be a number');
    assert.ok(Number.isFinite(data.scannedDirs), 'scannedDirs must be a number');
    assert.ok(Array.isArray(data.files));
    assert.ok(Array.isArray(data.folders));
    assert.ok(Array.isArray(data.types));

    // Everything reported must be at or above the requested threshold.
    for (const file of data.files) {
        assert.ok(
            file.sizeMB >= 102400,
            `file ${file.path} (${file.sizeMB} MB) is below the 102400 MB threshold`
        );
    }

    // The results must be sorted largest first.
    for (let i = 1; i < data.files.length; i++) {
        assert.ok(
            data.files[i - 1].bytes >= data.files[i].bytes,
            'files must be sorted by size descending'
        );
    }
});

test('the native build embeds the shared taxonomy', { skip: !isWin }, async () => {
    const exe = path.join(ROOT, 'OmniClean.exe');
    if (!fs.existsSync(exe)) {
        // The CI workflow builds it before this test runs; locally it may be
        // missing, which is not a failure of the taxonomy itself.
        return;
    }
    const res = await new Promise((resolve) => {
        execFile(exe, ['--validate-taxonomy'], { timeout: 60000, windowsHide: true }, (err, stdout) => {
            resolve({ code: err ? (typeof err.code === 'number' ? err.code : 1) : 0, stdout: stdout || '' });
        });
    });
    assert.equal(res.code, 0, 'the executable must be able to read its embedded taxonomy');
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
