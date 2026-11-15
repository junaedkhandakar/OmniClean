// OmniClean web edition - client logic.
// Talks to the local Node backend (127.0.0.1 only) which in turn drives the
// PowerShell cleanup engine. Every destructive action is confirmed first and
// preview (dry run) mode is the default.
'use strict';

document.addEventListener('DOMContentLoaded', () => {
    // ---- DOM Elements ----
    const ringFill = document.getElementById('ring-fill');
    const gaugePercent = document.getElementById('gauge-percent');
    const gaugeRatio = document.getElementById('gauge-ratio');
    const statFree = document.getElementById('stat-free');
    const statUsed = document.getElementById('stat-used');
    const statTotal = document.getElementById('stat-total');
    const systemPill = document.getElementById('system-pill-text');
    const statusDot = document.getElementById('status-dot');

    const driveSelect = document.getElementById('drive-select');
    const driveMediaLabel = document.getElementById('drive-media-label');
    const driveCapacityTitle = document.getElementById('drive-capacity-title');

    const categoryList = document.getElementById('category-list');
    const selectedSizeText = document.getElementById('selected-size-text');
    const btnClean = document.getElementById('btn-clean');
    const btnScan = document.getElementById('btn-scan');
    const btnSelectAll = document.getElementById('btn-select-all');
    const btnDeselectAll = document.getElementById('btn-deselect-all');
    const toggleDryRun = document.getElementById('toggle-dryrun');

    const toggleSchedule = document.getElementById('toggle-schedule');
    const scheduleVal = document.getElementById('schedule-val');

    const safetyMode = document.getElementById('safety-mode');
    const safetyAdmin = document.getElementById('safety-admin');
    const safetyNote = document.getElementById('safety-note');

    const consoleOutput = document.getElementById('console-output');
    const btnClearConsole = document.getElementById('btn-clear-console');
    const toast = document.getElementById('toast');

    const confirmOverlay = document.getElementById('confirm-overlay');
    const confirmBody = document.getElementById('confirm-body');
    const confirmList = document.getElementById('confirm-list');
    const confirmCancel = document.getElementById('confirm-cancel');
    const confirmAccept = document.getElementById('confirm-accept');

    const RING_CIRCUMFERENCE = 578; // 2 * PI * 92
    const STATUS_TIMEOUT_MS = 130 * 1000;
    const CLEAN_TIMEOUT_MS = 610 * 1000;
    const SCHEDULE_TIMEOUT_MS = 30 * 1000;
    const CLEAN_BTN_HTML = btnClean.innerHTML;
    const DRY_RUN_THRESHOLD_MB = 0.5;

    let currentCategories = [];
    let selectedCategoryIds = new Set();
    let cleanInFlight = false;
    let toastTimer = null;

    // ---- small helpers ----

    function showToast(msg, kind = 'info') {
        toast.textContent = msg;
        toast.classList.remove('toast-error', 'toast-success');
        if (kind === 'error') toast.classList.add('toast-error');
        if (kind === 'success') toast.classList.add('toast-success');
        toast.classList.add('show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toast.classList.remove('show'), 4000);
    }

    const LOG_TYPES = new Set(['system', 'success', 'warn', 'error']);

    function addLog(msg, type = 'info') {
        const line = document.createElement('div');
        line.className = 'log-line' + (LOG_TYPES.has(type) ? ` log-${type}` : '');
        line.textContent = msg;
        consoleOutput.appendChild(line);
        consoleOutput.scrollTop = consoleOutput.scrollHeight;
    }

    function setStatusDot(state) {
        statusDot.classList.remove('busy', 'error');
        if (state) statusDot.classList.add(state);
    }

    // fetch with a hard timeout so a hung backend never leaves the UI stuck.
    async function fetchJSON(url, options = {}, timeoutMs = 30000) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
            const res = await fetch(url, { ...options, signal: controller.signal });
            const text = await res.text();
            let data = {};
            try {
                data = text ? JSON.parse(text) : {};
            } catch {
                data = { error: 'Server returned an invalid response' };
            }
            return { ok: res.ok, status: res.status, data };
        } finally {
            clearTimeout(timer);
        }
    }

    function formatSize(mb) {
        if (!Number.isFinite(mb)) return '0.0 MB';
        return mb > 1024 ? `${(mb / 1024).toFixed(2)} GB` : `${mb.toFixed(1)} MB`;
    }

    function applyDisk(disk) {
        if (!disk) return;
        statFree.textContent = `${disk.freeGB} GB`;
        statUsed.textContent = `${disk.usedGB} GB`;
        statTotal.textContent = `${disk.totalGB} GB`;
        updateGauge(disk.percentFree, disk.freeGB, disk.totalGB);

        if (disk.drive) {
            driveCapacityTitle.textContent = `Drive ${disk.drive} Capacity`;
            driveMediaLabel.textContent = `Local Disk (${disk.drive})`;
        }
    }

    function setScheduleText(enabled) {
        scheduleVal.textContent = enabled ? 'Active (Every Sunday at 12:00 PM)' : 'Disabled';
        scheduleVal.style.color = enabled ? 'var(--status-success)' : 'var(--text-tertiary)';
    }

    function cleanHardwareText(s) {
        return (s || '')
            .replace(/\((R|TM)\)/gi, '')
            .replace(/\s*@\s*[\d.]+\s*GHz.*/i, '')
            .replace(/\s+/g, ' ')
            .trim();
    }

    function applySystem(system) {
        if (!system || !systemPill) return;
        const parts = [];
        const cpu = cleanHardwareText(system.cpu);
        const gpu = cleanHardwareText(system.gpu);
        if (cpu) parts.push(cpu);
        if (system.ramGB) parts.push(`${system.ramGB} GB RAM`);
        if (gpu) parts.push(gpu);
        else if (system.arch) parts.push(system.arch);
        if (parts.length > 0) systemPill.textContent = parts.join(' • ');
    }

    // ---- gauge ----
    function updateGauge(percent, freeGB, totalGB) {
        const safePercent = Number.isFinite(percent)
            ? Math.min(100, Math.max(0, percent))
            : 0;
        gaugePercent.textContent = `${safePercent}%`;
        gaugeRatio.textContent = `${freeGB} GB / ${totalGB} GB`;

        const offset = RING_CIRCUMFERENCE - (RING_CIRCUMFERENCE * (safePercent / 100));
        ringFill.style.strokeDashoffset = offset;

        if (safePercent < 15) {
            ringFill.style.stroke = 'var(--status-critical)';
        } else if (safePercent < 25) {
            ringFill.style.stroke = 'var(--status-caution)';
        } else {
            ringFill.style.stroke = 'var(--accent-default)';
        }
    }

    // ---- categories ----

    function buildCategoryItem(cat) {
        const isSelected = selectedCategoryIds.has(cat.id);
        const item = document.createElement('div');
        item.className = 'category-item' + (isSelected ? ' selected' : '');
        item.dataset.id = cat.id;
        item.setAttribute('role', 'checkbox');
        item.setAttribute('tabindex', '0');
        item.setAttribute('aria-checked', String(isSelected));

        const hasSize = cat.sizeMB > 0.1;
        const badgeClass = hasSize ? 'has-data' : 'clean';

        const left = document.createElement('div');
        left.className = 'item-left';

        const checkbox = document.createElement('div');
        checkbox.className = 'custom-checkbox';
        checkbox.setAttribute('aria-hidden', 'true');
        checkbox.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3"><polyline points="20 6 9 17 4 12"/></svg>';

        const details = document.createElement('div');
        details.className = 'item-details';
        const h4 = document.createElement('h4');
        h4.textContent = cat.name;
        const p = document.createElement('p');
        p.textContent = cat.desc;
        p.title = cat.desc;
        details.append(h4, p);

        left.append(checkbox, details);

        const right = document.createElement('div');
        right.className = 'item-right';
        const badge = document.createElement('span');
        badge.className = `size-badge ${badgeClass}`;
        badge.textContent = hasSize ? formatSize(cat.sizeMB) : 'Clean';
        right.appendChild(badge);

        item.append(left, right);
        return item;
    }

    function renderCategories() {
        categoryList.innerHTML = '';

        if (!currentCategories || currentCategories.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'loading-state';
            empty.textContent = 'No categories detected.';
            categoryList.appendChild(empty);
            updateSelectedSummary();
            return;
        }

        const fragment = document.createDocumentFragment();
        currentCategories.forEach((cat) => fragment.appendChild(buildCategoryItem(cat)));
        categoryList.appendChild(fragment);

        updateSelectedSummary();
    }

    // Single delegated listener instead of rebinding every row on each toggle.
    categoryList.addEventListener('click', (event) => {
        const item = event.target.closest('.category-item');
        if (!item || !item.dataset.id) return;
        toggleCategory(item.dataset.id);
    });

    // Keyboard support for the checkbox-style rows.
    categoryList.addEventListener('keydown', (event) => {
        if (event.key !== ' ' && event.key !== 'Enter') return;
        const item = event.target.closest('.category-item');
        if (!item || !item.dataset.id) return;
        event.preventDefault();
        toggleCategory(item.dataset.id);
    });

    function toggleCategory(id) {
        if (selectedCategoryIds.has(id)) {
            selectedCategoryIds.delete(id);
        } else {
            selectedCategoryIds.add(id);
        }
        const item = categoryList.querySelector(`.category-item[data-id="${CSS.escape(id)}"]`);
        if (item) {
            const nowSelected = selectedCategoryIds.has(id);
            item.classList.toggle('selected', nowSelected);
            item.setAttribute('aria-checked', String(nowSelected));
        }
        updateSelectedSummary();
    }

    function updateSelectedSummary() {
        let totalMB = 0;
        currentCategories.forEach((cat) => {
            if (selectedCategoryIds.has(cat.id)) {
                totalMB += (cat.sizeMB || 0);
            }
        });

        selectedSizeText.textContent = formatSize(totalMB);

        const dryRun = toggleDryRun.checked;
        btnClean.disabled = cleanInFlight || selectedCategoryIds.size === 0;
        btnClean.innerHTML = dryRun ? CLEAN_BTN_HTML : CLEAN_BTN_HTML.replace('Preview Cleanup', 'Clean Selected Storage');
        updateSafetyPanel();
    }

    function updateSafetyPanel() {
        const dryRun = toggleDryRun.checked;
        safetyMode.textContent = dryRun ? 'Preview (dry run)' : 'Live deletion';
        safetyMode.className = 'safety-value ' + (dryRun ? 'preview' : 'live');
        safetyNote.textContent = dryRun
            ? 'Preview mode never deletes anything. It only reports what a real run would remove.'
            : 'Live mode permanently deletes the selected caches. Browsers stay signed in - passwords, cookies and history are never touched.';
    }

    btnSelectAll.addEventListener('click', () => {
        selectedCategoryIds = new Set(currentCategories.map((c) => c.id));
        renderCategories();
    });

    btnDeselectAll.addEventListener('click', () => {
        selectedCategoryIds.clear();
        renderCategories();
    });

    toggleDryRun.addEventListener('change', () => {
        updateSelectedSummary();
        addLog(
            toggleDryRun.checked
                ? '[Mode] Preview only - the next run will not delete anything.'
                : '[Mode] Live deletion enabled - the next run will permanently remove files.',
            toggleDryRun.checked ? 'system' : 'warn'
        );
    });

    btnClearConsole.addEventListener('click', () => {
        consoleOutput.innerHTML = '';
        addLog('[Log cleared]', 'system');
    });

    // ---- confirmation dialog ----

    function openConfirmDialog({ title, body, items, acceptLabel }) {
        document.getElementById('confirm-title').textContent = title;
        confirmBody.textContent = body;
        confirmList.innerHTML = '';
        items.forEach((item) => {
            const li = document.createElement('li');
            const name = document.createElement('span');
            name.className = 'confirm-name';
            name.textContent = item.name;
            const size = document.createElement('span');
            size.className = 'confirm-size';
            size.textContent = item.size;
            li.append(name, size);
            confirmList.appendChild(li);
        });
        confirmAccept.textContent = acceptLabel;
        confirmOverlay.hidden = false;
        confirmAccept.focus();
    }

    function closeConfirmDialog() {
        confirmOverlay.hidden = true;
    }

    confirmCancel.addEventListener('click', closeConfirmDialog);
    confirmOverlay.addEventListener('click', (event) => {
        if (event.target === confirmOverlay) closeConfirmDialog();
    });
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && !confirmOverlay.hidden) closeConfirmDialog();
    });

    // ---- status ----

    async function fetchStatus({ silent = false } = {}) {
        if (!silent) {
            addLog('[Scanning] Checking storage metrics and junk categories...', 'system');
            setStatusDot('busy');
        }
        btnScan.disabled = true;
        try {
            const drive = driveSelect.value ? `?drive=${encodeURIComponent(driveSelect.value)}` : '';
            const { ok, status, data } = await fetchJSON(`/api/status${drive}`, {}, STATUS_TIMEOUT_MS);

            if (!ok) {
                setStatusDot('error');
                addLog(`[Error] Scan failed (${status}): ${data.error || 'unknown error'}`, 'warn');
                if (status === 403) {
                    addLog('[Hint] Access denied - open the app via Launch.bat (http://127.0.0.1:4982).', 'warn');
                }
                return;
            }

            applyDisk(data.disk);
            applySystem(data.system);
            populateDrives(data.drives, data.disk ? data.disk.drive : null);

            if (typeof data.isAdmin === 'boolean') {
                safetyAdmin.textContent = data.isAdmin ? 'Administrator' : 'Standard user';
                safetyAdmin.className = 'safety-value ' + (data.isAdmin ? 'live' : '');
            }

            if (data.categories) {
                currentCategories = data.categories;
                // Keep the user's manual selection across rescans: only add
                // newly detected junk, never drop what the user picked.
                const detected = currentCategories
                    .filter((c) => c.sizeMB > DRY_RUN_THRESHOLD_MB)
                    .map((c) => c.id);
                detected.forEach((id) => selectedCategoryIds.add(id));
                renderCategories();
            }

            if (data.scheduledTask) {
                toggleSchedule.checked = data.scheduledTask.enabled;
                setScheduleText(data.scheduledTask.enabled);
            }

            setStatusDot('');
            addLog('[Scan Complete] Storage status up to date.', 'success');
        } catch (err) {
            setStatusDot('error');
            const reason = err.name === 'AbortError' ? 'timed out' : err.message;
            addLog(`[Error] Failed to connect to optimizer service: ${reason}`, 'warn');
        } finally {
            btnScan.disabled = false;
        }
    }

    btnScan.addEventListener('click', () => fetchStatus());

    // ---- drive picker ----

    function populateDrives(drives, activeDrive) {
        if (!Array.isArray(drives) || drives.length === 0) return;
        // Rebuild only when the list actually changed, so the current
        // selection is never reset by a background refresh.
        const signature = drives.map((d) => d.id).join(',');
        if (driveSelect.dataset.signature === signature) {
            if (activeDrive) driveSelect.value = activeDrive;
            return;
        }
        driveSelect.dataset.signature = signature;
        driveSelect.innerHTML = '';
        drives.forEach((d) => {
            const opt = document.createElement('option');
            opt.value = d.id;
            opt.textContent = d.label || d.id;
            driveSelect.appendChild(opt);
        });
        if (activeDrive) driveSelect.value = activeDrive;
        if (!driveSelect.value && driveSelect.options.length > 0) {
            driveSelect.value = driveSelect.options[0].value;
        }
    }

    driveSelect.addEventListener('change', () => {
        addLog(`[Drive] Switching report to ${driveSelect.value}.`, 'system');
        fetchStatus();
    });

    // ---- clean ----

    async function runClean(dryRun) {
        const categories = currentCategories.filter((c) => selectedCategoryIds.has(c.id));
        const totalMB = categories.reduce((sum, c) => sum + (c.sizeMB || 0), 0);

        const openDialog = () => new Promise((resolve) => {
            const onCancel = () => {
                confirmCancel.removeEventListener('click', onCancel);
                confirmAccept.removeEventListener('click', onAccept);
                resolve(false);
            };
            const onAccept = () => {
                confirmCancel.removeEventListener('click', onCancel);
                confirmAccept.removeEventListener('click', onAccept);
                resolve(true);
            };
            confirmCancel.addEventListener('click', onCancel);
            confirmAccept.addEventListener('click', onAccept);

            openConfirmDialog({
                title: dryRun ? 'Preview cleanup' : 'Confirm permanent deletion',
                body: dryRun
                    ? `Preview ${categories.length} target(s) and report what a live run would remove (${formatSize(totalMB)}). Nothing is deleted.`
                    : `Permanently delete ${categories.length} target(s) (about ${formatSize(totalMB)}). Browsers stay signed in - passwords, cookies and history are never touched.`,
                items: categories.map((c) => ({ name: c.name, size: formatSize(c.sizeMB || 0) })),
                acceptLabel: dryRun ? 'Run preview' : 'Delete now'
            });
        });

        const confirmed = await openDialog();
        if (!confirmed) {
            addLog('[Cancelled] Cleanup was cancelled by the user.', 'warn');
            return;
        }

        cleanInFlight = true;
        btnClean.disabled = true;
        btnClean.innerHTML = '<div class="spinner" style="width:18px;height:18px;border-width:2px;"></div> <span>Working...</span>';
        setStatusDot('busy');
        addLog(
            `[Starting Cleanup] ${dryRun ? 'Previewing' : 'Purging'} ${selectedCategoryIds.size} selected categories...`,
            'system'
        );

        try {
            const { ok, status, data } = await fetchJSON('/api/clean', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    categories: Array.from(selectedCategoryIds),
                    dryRun,
                    drive: driveSelect.value || 'C:'
                })
            }, CLEAN_TIMEOUT_MS);

            if (status === 409) {
                addLog('[Busy] A cleanup is already running.', 'warn');
                showToast('A cleanup is already running.', 'error');
                return;
            }

            if (!ok) {
                addLog(`[Error] Cleanup request failed (${status}): ${data.error || 'unknown error'}`, 'error');
                showToast('Cleanup request failed.', 'error');
                return;
            }

            (data.logs || []).forEach((line) => {
                if (line.trim()) addLog(line, 'info');
            });

            applyDisk(data.disk);

            if (data.success) {
                showToast(
                    data.dryRun
                        ? 'Preview finished - nothing was deleted.'
                        : 'Cleanup completed successfully!',
                    'success'
                );
                addLog(
                    data.dryRun
                        ? '[Success] Preview complete - no files were modified.'
                        : '[Success] Selected storage cleaned successfully.',
                    'success'
                );
            } else {
                // Never report a failed run as a success.
                showToast('Cleanup finished with errors - check the log.', 'error');
                addLog(`[Error] Cleanup reported a failure: ${data.error || 'see log above'}`, 'error');
            }
        } catch (err) {
            const reason = err.name === 'AbortError' ? 'timed out' : err.message;
            addLog(`[Error] Cleanup failed: ${reason}`, 'error');
            showToast('Cleanup encountered an error.', 'error');
        } finally {
            cleanInFlight = false;
            btnClean.innerHTML = CLEAN_BTN_HTML;
            updateSelectedSummary();
            setStatusDot('');
            setTimeout(() => fetchStatus({ silent: true }), 1000);
        }
    }

    btnClean.addEventListener('click', () => {
        if (cleanInFlight || selectedCategoryIds.size === 0) return;
        runClean(toggleDryRun.checked);
    });

    // ---- schedule ----

    toggleSchedule.addEventListener('change', async () => {
        const isEnabled = toggleSchedule.checked;
        scheduleVal.textContent = isEnabled ? 'Enabling...' : 'Disabling...';

        try {
            const { ok, data } = await fetchJSON('/api/schedule', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enable: isEnabled })
            }, SCHEDULE_TIMEOUT_MS);

            if (!ok || !data.success) {
                throw new Error(data.error || 'request failed');
            }

            setScheduleText(isEnabled);
            showToast(
                isEnabled ? 'Auto-Clean scheduled every Sunday at 12:00 PM!' : 'Auto-Clean schedule removed.',
                'success'
            );
            addLog(`[Scheduler] Auto-clean ${isEnabled ? 'activated' : 'deactivated'} in Windows Task Scheduler.`, 'success');
        } catch (err) {
            toggleSchedule.checked = !isEnabled;
            scheduleVal.textContent = 'Error updating schedule';
            scheduleVal.style.color = 'var(--status-critical)';
            showToast('Could not update the schedule.', 'error');
            addLog(`[Scheduler Error] ${err.message}`, 'warn');
        }
    });

    // ---- initial load ----
    updateSafetyPanel();
    fetchStatus();
});
