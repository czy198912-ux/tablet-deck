// Tablet Deck website: the deck's own icons on the feature cards, and download buttons
// that point straight at the latest installer with its version and size.
(() => {
  for (const el of document.querySelectorAll(".js-icon")) {
    if (window.deckIcon) el.innerHTML = window.deckIcon(el.dataset.icon);
  }

  // Without this (offline, or GitHub's rate limit) the buttons still open the releases page.
  fetch("https://api.github.com/repos/nodieCode/tablet-deck/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then((r) => (r.ok ? r.json() : Promise.reject()))
    .then((release) => {
      const setup = (release.assets || []).find((a) => /^TabletDeck-Setup-.*\.exe$/.test(a.name));
      if (!setup) return;
      const mb = (setup.size / 1048576).toFixed(0);
      for (const a of document.querySelectorAll(".js-download")) a.href = setup.browser_download_url;
      for (const s of document.querySelectorAll(".js-version")) s.textContent = `Version ${release.tag_name.replace(/^v/, "")} · ${mb} MB · Windows 10 and 11`;
    })
    .catch(() => {});
})();
