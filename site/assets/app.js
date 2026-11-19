// OmniClean product page - progressive enhancement only.
//
// The page is a static document and reads correctly with JavaScript
// disabled. This file adds two small conveniences and nothing that the
// content depends on.

'use strict';

document.addEventListener('DOMContentLoaded', () => {
    // 1. Mark the nav link for the section currently in view, so the
    //    reader always knows where they are on a long page.
    const navLinks = Array.from(document.querySelectorAll('.masthead-nav a'));
    const sections = navLinks
        .map((link) => {
            const id = link.getAttribute('href');
            return id && id.startsWith('#') ? document.querySelector(id) : null;
        })
        .filter(Boolean);

    if (sections.length > 0 && 'IntersectionObserver' in window) {
        const visible = new Set();

        const observer = new IntersectionObserver(
            (entries) => {
                for (const entry of entries) {
                    if (entry.isIntersecting) visible.add(entry.target.id);
                    else visible.delete(entry.target.id);
                }

                for (const link of navLinks) {
                    const id = link.getAttribute('href');
                    const active = id && id.startsWith('#') && visible.has(id.slice(1));
                    if (active) link.setAttribute('aria-current', 'true');
                    else link.removeAttribute('aria-current');
                }
            },
            { rootMargin: '-72px 0px -60% 0px', threshold: 0 }
        );

        for (const section of sections) observer.observe(section);
    }

    // 2. Copy a build command to the clipboard on click. The command is
    //    rendered as text so it stays selectable when scripting is off.
    for (const block of document.querySelectorAll('.build-block pre')) {
        const command = block.querySelector('code');
        if (!command) continue;

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'copy-btn';
        button.textContent = 'Copy';
        button.setAttribute('aria-label', 'Copy command to clipboard');

        button.addEventListener('click', async () => {
            try {
                await navigator.clipboard.writeText(command.textContent.trim());
                button.textContent = 'Copied';
            } catch {
                // Clipboard access can be denied; fall back to selecting
                // the text so the reader can copy it manually.
                const range = document.createRange();
                range.selectNodeContents(command);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                button.textContent = 'Selected';
            }

            setTimeout(() => {
                button.textContent = 'Copy';
            }, 1800);
        });

        block.appendChild(button);
    }
});
