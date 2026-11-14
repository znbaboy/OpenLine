# OpenLine raw mic (browser extension)

Browsers run voice processing on every mic: echo cancellation, noise suppression, auto gain. With a virtual mic that carries video audio, that processing cuts music and effects. Chrome's echo canceller can also treat audio playing in another tab as "echo" and remove it.

This extension turns that processing off, only on the sites listed in `manifest.json`.

**Install (Chrome / Edge / Brave):** open `chrome://extensions`, turn on **Developer mode**, click **Load unpacked**, pick this folder, then reload the site.

**Other sites:** add them to `matches` in `manifest.json`, then click the reload icon on the extension.

Use headphones: with echo cancellation off, a stranger's voice coming out of speakers would be sent back to them.
