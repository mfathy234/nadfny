// Points every download button at the newest release's files and fills in version and size.
// Without JavaScript, or if GitHub is unreachable, the buttons still open the latest release page.
(function () {
  var API = 'https://api.github.com/repos/mfathy234/nadfny/releases/latest';
  var patterns = { msi: /-win-x64\.msi$/i, zip: /-portable-win-x64\.zip$/i };
  var ar = document.documentElement.lang === 'ar';

  function mb(bytes) { return Math.round(bytes / 1048576) + (ar ? ' ميجابايت' : ' MB'); }

  fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
    .then(function (release) {
      var version = String(release.tag_name || '').replace(/^v/i, '');
      Object.keys(patterns).forEach(function (kind) {
        var asset = (release.assets || []).find(function (a) { return patterns[kind].test(a.name); });
        if (!asset) return;
        document.querySelectorAll('[data-dl="' + kind + '"]').forEach(function (a) { a.href = asset.browser_download_url; });
        document.querySelectorAll('[data-file="' + kind + '"]').forEach(function (el) { el.textContent = asset.name + ', ' + mb(asset.size); });
        document.querySelectorAll('[data-meta="' + kind + '"]').forEach(function (el) {
          el.textContent = ar ? 'الإصدار ' + version + '، ويندوز 10 و11، ' + mb(asset.size)
                              : 'Version ' + version + ', Windows 10 and 11, ' + mb(asset.size);
        });
      });
      document.querySelectorAll('[data-version]').forEach(function (el) { el.textContent = version; });
    })
    .catch(function () { /* links already point at the latest release page */ });
})();
