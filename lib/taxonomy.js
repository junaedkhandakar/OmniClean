'use strict';

// Loads the shared cleanup taxonomy (targets.json).
//
// targets.json is the single source of truth for both editions: the web
// engine reads it through targets.ps1, the native engine embeds it, and
// this module reads it directly. A category therefore cannot exist on one
// side only - test/smoke.test.js asserts the three stay in sync.

const fs = require('fs');
const path = require('path');

const TAXONOMY_PATH = path.join(__dirname, '..', 'targets.json');

let cached = null;

function loadTaxonomy() {
    if (cached) return cached;

    const raw = fs.readFileSync(TAXONOMY_PATH, 'utf8');
    const parsed = JSON.parse(raw);
    if (!parsed || !Array.isArray(parsed.groups)) {
        throw new Error('targets.json must contain a "groups" array');
    }
    cached = parsed;
    return cached;
}

// Every target across every group, flattened and annotated with its group.
function allTargets() {
    const out = [];
    for (const group of loadTaxonomy().groups) {
        for (const target of group.targets) {
            out.push({ ...target, group: group.name, groupId: group.id, groupIcon: group.icon });
        }
    }
    return out;
}

// The allowlist of valid category ids, derived from the taxonomy.
function categoryIds() {
    return allTargets().map((t) => t.id);
}

// The taxonomy split into groups, for the UI.
function groupedTargets() {
    return loadTaxonomy().groups.map((group) => ({
        id: group.id,
        name: group.name,
        icon: group.icon,
        targets: group.targets
    }));
}

// Merges the scan result with the taxonomy's display metadata.
//
// The icons are merged in here rather than in PowerShell: the Windows
// console codepage mangles non-ASCII characters on their way out of
// powershell.exe, so a group icon read from targets.json would arrive as
// mojibake. Node reads targets.json as UTF-8 and never has that problem.
function decorateGroups(groups) {
    const byId = new Map();
    for (const group of loadTaxonomy().groups) {
        byId.set(group.id, group);
    }

    return groups.map((group) => {
        const meta = byId.get(group.id);
        const targets = (group.targets || []).map((target) => {
            const metaTarget = meta
                ? (meta.targets || []).find((t) => t.id === target.id)
                : null;
            return {
                ...target,
                name: metaTarget ? metaTarget.name : target.name,
                desc: metaTarget ? metaTarget.desc : target.desc,
                kind: metaTarget ? metaTarget.kind : target.kind
            };
        });
        return {
            ...group,
            icon: meta ? meta.icon : '',
            targets
        };
    });
}

module.exports = { loadTaxonomy, allTargets, categoryIds, groupedTargets, decorateGroups, TAXONOMY_PATH };
