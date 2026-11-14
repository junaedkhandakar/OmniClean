// Storage Optimizer Client Logic
document.addEventListener('DOMContentLoaded', () => {
    // DOM Elements
    const ringFill = document.getElementById('ring-fill');
    const gaugePercent = document.getElementById('gauge-percent');
    const gaugeRatio = document.getElementById('gauge-ratio');
    const statFree = document.getElementById('stat-free');
    const statUsed = document.getElementById('stat-used');
    const statTotal = document.getElementById('stat-total');
    const systemPill = document.getElementById('system-pill-text');

    const categoryList = document.getElementById('category-list');
    const selectedSizeText = document.getElementById('selected-size-text');
    const btnClean = document.getElementById('btn-clean');
    const btnScan = document.getElementById('btn-scan');
    const btnSelectAll = document.getElementById('btn-select-all');
    const btnDeselectAll = document.getElementById('btn-deselect-all');

    const toggleSchedule = document.getElementById('toggle-schedule');
    const scheduleVal = document.getElementById('schedule-val');

    const consoleOutput = document.getElementById('console-output');
    const btnClearConsole = document.getElementById('btn-clear-console');
    const toast = document.getElementById('toast');

    const RING_CIRCUMFERENCE = 578; // 2 * PI * 92
    const STATUS_TIMEOUT_MS = 130 * 1000;
    const CLEAN_TIMEOUT_MS = 610 * 1000;
    const SCHEDULE_TIMEOUT_MS = 30 * 1000;
    const CLEAN_BTN_HTML = btnClean.innerHTML;

    let currentCategories = [];
    let selectedCategoryIds = new Set();
    let cleanInFlight = false;

    // ---- small helpers ----

    function showToast(msg) {
        toast.textContent = msg;
        toast.classList.add('show');
        setTimeout(() => toast.classList.remove('show'), 3500);
    }

    const LOG_TYPES = new Set(['system', 'success', 'warn', 'error']);

    function addLog(msg, type = 'info') {
        const line = document.createElement('div');
        line.className = 'log-line' + (LOG_TYPES.has(type) ? ` log-${type}` : '');
        line.textContent = msg;
        consoleOutput.appendChild(line);
        consoleOutput.scrollTop = consoleOutput.scrollHeight;
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
        return mb > 1024 ? `${(mb / 1024).toFixed(2)} GB` : `${mb.toFixed(1)} MB`;
    }

    function applyDisk(disk) {
        if (!disk) return;
        statFree.textContent = `${disk.freeGB} GB`;
        statUsed.textContent = `${disk.usedGB} GB`;
        statTotal.textContent = `${disk.totalGB} GB`;
        updateGauge(disk.percentFree, disk.freeGB, disk.totalGB);
    }

    function setScheduleText(enabled) {
            scheduleVal.textContent = enabled ? 'Active (Every Sunday at 12:00 PM)' : 'Disabled';
            scheduleVal.style.color = enabled ? '#6ccb5f' : '#8a8a8a';
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
        gaugePercent.textContent = `${percent}%`;
        gaugeRatio.textContent = `${freeGB} GB / ${totalGB} GB`;

        const offset = RING_CIRCUMFERENCE - (RING_CIRCUMFERENCE * (percent / 100));
        ringFill.style.strokeDashoffset = offset;
        ringFill.style.filter = 'none';

        if (percent < 15) {
            ringFill.style.stroke = '#ff99a4'; // Win11 Critical
        } else if (percent < 25) {
            ringFill.style.stroke = '#fce100'; // Win11 Caution
        } else {
            ringFill.style.stroke = '#60cdff'; // Win11 Accent Cyan
        }
    }

    // ---- categories ----

    function buildCategoryItem(cat) {
        const isSelected = selectedCategoryIds.has(cat.id);
        const item = document.createElement('div');
        item.className = 'category-item' + (isSelected ? ' selected' : '');
        item.dataset.id = cat.id;

        const hasSize = cat.sizeMB > 0.1;
        const badgeClass = hasSize ? 'has-data' : 'clean';

        const left = document.createElement('div');
        left.className = 'item-left';

        const checkbox = document.createElement('div');
        checkbox.className = 'custom-checkbox';
        checkbox.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3"><polyline points="20 6 9 17 4 12"/></svg>';

        const details = document.createElement('div');
        details.className = 'item-details';
        const h4 = document.createElement('h4');
        h4.textContent = cat.name;
        const p = document.createElement('p');
        p.textContent = cat.desc;
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
        const id = item.dataset.id;

        if (selectedCategoryIds.has(id)) {
            selectedCategoryIds.delete(id);
            item.classList.remove('selected');
        } else {
            selectedCategoryIds.add(id);
            item.classList.add('selected');
        }
        updateSelectedSummary();
    });

    function updateSelectedSummary() {
        let totalMB = 0;
        currentCategories.forEach((cat) => {
            if (selectedCategoryIds.has(cat.id)) {
                totalMB += (cat.sizeMB || 0);
            }
        });

        selectedSizeText.textContent = totalMB > 1024
            ? `${(totalMB / 1024).toFixed(2)} GB`
            : `${totalMB.toFixed(1)} MB`;

        btnClean.disabled = cleanInFlight || selectedCategoryIds.size === 0;
    }

    btnSelectAll.addEventListener('click', () => {
        selectedCategoryIds = new Set(currentCategories.map((c) => c.id));
        renderCategories();
    });

    btnDeselectAll.addEventListener('click', () => {
        selectedCategoryIds.clear();
        renderCategories();
    });

    btnClearConsole.addEventListener('click', () => {
        consoleOutput.innerHTML = '';
        addLog('[Log cleared]', 'system');
    });

    // ---- status ----

    async function fetchStatus() {
        addLog('[Scanning] Checking storage metrics and junk categories...', 'system');
        btnScan.disabled = true;
        try {
            const { ok, status, data } = await fetchJSON('/api/status', {}, STATUS_TIMEOUT_MS);

            if (!ok) {
                addLog(`[Error] Scan failed (${status}): ${data.error || 'unknown error'}`, 'warn');
                if (status === 403) {
                    addLog('[Hint] Access denied - open the app via Launch.bat (http://127.0.0.1:4982).', 'warn');
                }
                return;
            }

            applyDisk(data.disk);
            applySystem(data.system);

            if (data.categories) {
                currentCategories = data.categories;
                // Auto-select items that have junk detected
                selectedCategoryIds = new Set(
                    currentCategories.filter((c) => c.sizeMB > 0.5).map((c) => c.id)
                );
                renderCategories();
            }

            if (data.scheduledTask) {
                toggleSchedule.checked = data.scheduledTask.enabled;
                setScheduleText(data.scheduledTask.enabled);
            }

            addLog('[Scan Complete] Storage status up to date.', 'success');
        } catch (err) {
            const reason = err.name === 'AbortError' ? 'timed out' : err.message;
            addLog(`[Error] Failed to connect to optimizer service: ${reason}`, 'warn');
        } finally {
            btnScan.disabled = false;
        }
    }

    btnScan.addEventListener('click', fetchStatus);

    // ---- clean ----

    btnClean.addEventListener('click', async () => {
        if (cleanInFlight || selectedCategoryIds.size === 0) return;

        cleanInFlight = true;
        btnClean.disabled = true;
        btnClean.innerHTML = '<div class="spinner" style="width:18px;height:18px;border-width:2px;"></div> <span>Cleaning...</span>';
        addLog(`[Starting Cleanup] Purging ${selectedCategoryIds.size} selected categories...`, 'system');

        try {
            const { ok, status, data } = await fetchJSON('/api/clean', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ categories: Array.from(selectedCategoryIds) })
            }, CLEAN_TIMEOUT_MS);

            if (status === 409) {
                addLog('[Busy] A cleanup is already running.', 'warn');
                showToast('A cleanup is already running.');
                return;
            }

            if (!ok) {
                addLog(`[Error] Cleanup request failed (${status}): ${data.error || 'unknown error'}`, 'error');
                showToast('Cleanup request failed.');
                return;
            }

            (data.logs || []).forEach((line) => {
                if (line.trim()) addLog(line, 'info');
            });

            applyDisk(data.disk);

            if (data.success) {
                showToast('Cleanup completed successfully!');
                addLog('[Success] Selected storage cleaned successfully.', 'success');
            } else {
                // Never report a failed run as a success.
                showToast('Cleanup finished with errors - check the log.');
                addLog(`[Error] Cleanup reported a failure: ${data.error || 'see log above'}`, 'error');
            }
        } catch (err) {
            const reason = err.name === 'AbortError' ? 'timed out' : err.message;
            addLog(`[Error] Cleanup failed: ${reason}`, 'error');
            showToast('Cleanup encountered an error.');
        } finally {
            cleanInFlight = false;
            btnClean.innerHTML = CLEAN_BTN_HTML;
            updateSelectedSummary();
            setTimeout(fetchStatus, 1000);
        }
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
            showToast(isEnabled ? 'Auto-Clean scheduled every Sunday at 12:00 PM!' : 'Auto-Clean schedule removed.');
            addLog(`[Scheduler] Auto-clean ${isEnabled ? 'activated' : 'deactivated'} in Windows Task Scheduler.`, 'success');
        } catch (err) {
            toggleSchedule.checked = !isEnabled;
            scheduleVal.textContent = 'Error updating schedule';
            scheduleVal.style.color = '#f87171';
            addLog(`[Scheduler Error] ${err.message}`, 'warn');
        }
    });

    // Initial load
    fetchStatus();
});
