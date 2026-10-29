# Product

## Register

product

## Users
One developer on a Windows 11 laptop whose disk keeps filling up. Their workday is full of git worktrees (one per branch or agent run), Docker images and build cache, and `node_modules` / `bin` / `obj` trees spread across a `Work` folder. They open Nadfny when Windows warns about low space or a build fails with "no space left". They want to see what is eating the disk in seconds and reclaim it without a terminal.

## Product Purpose
Nadfny finds the developer-specific space hogs that generic cleaners miss (stale worktrees, Docker images, build cache and the WSL disk, regenerable build output, package caches), says how big each one is and whether it is safe to remove, and removes it in one confirmed step. Success: the user reclaims tens of gigabytes in under a minute and never loses work they meant to keep.

## Brand Personality
Calm, exact, trustworthy. It speaks like a careful colleague: plain numbers, plain verbs ("Remove", "Clear"), no alarmism, no marketing. The emotional goal is relief: the disk problem goes from vague dread to a short list with sizes.

## Anti-references
- CCleaner / "PC optimizer" apps: alarm-red scores, fake urgency, upsells, badge spam.
- Generic admin dashboards: KPI tile grids, gradient hero numbers, cards inside cards.
- Raw `du`-style dumps with no guidance about what is safe.

## Design Principles
1. Size first, then safety. Every row answers "how big" and "can I lose this" before anything else.
2. Never surprise-delete. Anything irreversible names the exact item and size on the confirm button; work that is not committed or not pushed is called out before removal.
3. Show progress, never block. Scans stream in; the window stays usable while sizes are computed.
4. Native calm. It should feel like a Settings pane from the OS (iPhone Storage is the north star), not a web page.
5. Regenerable vs. irreplaceable is the core distinction, and the UI makes it visible.

## Accessibility & Inclusion
WCAG 2.2 AA contrast in light and dark. Full keyboard operation with visible focus. Color never carries meaning alone (category colors always pair with labels; warnings pair with icon + text). Honors reduced motion and system light/dark.
