# Nadfny website

Static site served by Cloudflare Pages at https://nadfny.pages.dev/ from this `site/` folder. There is no build step.

- `index.html` is English, `ar/index.html` is Arabic (right to left).
- `release.js` asks the GitHub API for the newest release and points every download button at its `.msi` and portable `.zip`, filling in version and size. Without JavaScript, or if GitHub is unreachable, the buttons open the latest release page.
- `motion.js` adds scroll reveals; content is visible without it and reduced motion is respected.
- `assets/og.png` is the 1200 x 630 social preview.

Preview locally with `npx serve site`. Deploy with `npx wrangler pages deploy site --project-name nadfny`.
