# tdpdf.com

The public website: five static pages, generated from this repository and served by nginx
from a container on **doodlessd**. There is no CMS, no database and no admin login.

| Path | Source |
|---|---|
| `/` | `content/home.md` |
| `/download/` | `content/download.md` (version, size and SHA-256 filled from the latest release) |
| `/privacy/` | `../../PRIVACY.md`, verbatim |
| `/third-party-licenses/` | `../../THIRD-PARTY-NOTICES.md`, verbatim |
| `/telemetry/` | `content/telemetry.md` |

`pages.json` is the manifest: what pages exist, at which URL, with which title and
description. `layout.html` is the frame around every page, `site.css` is the whole
stylesheet (colours from `Themes/Dark.xaml`), and `screenshots/` holds the home page's
screenshot slots (see its README).

## Changing the site

Edit the Markdown (or `PRIVACY.md`) and merge to `main`. That is the whole process:
`.github/workflows/site.yml` rebuilds the site and publishes it, and doodlessd serves the new
version within about five minutes. The same workflow runs after every successful release, so
the download page moves to the new version by itself.

To see a change before merging:

```sh
pwsh -File build/site/build-site.ps1 -OutputDir build/site/out
python3 -m http.server -d build/site/out 8765     # then open http://localhost:8765/
```

`build-site.ps1` needs PowerShell 7 and internet access (it reads the latest release from
GitHub). `-Tag v2.0.2.0` builds against a specific release.

## How it is served

```
merge to main / release ──> site.yml (runner `tdpdf`) ──> ghcr.io/doodlemania2/tdpdf-site:latest
                                                                    │ watchtower, every 5 min
visitor ──> Cloudflare ──> tunnel `public-fleet` ──> doodlessd: 127.0.0.1:18106 ──> tdpdf-site:8080
```

- **doodlessd** `~/tdpdf-site-stack/docker-compose.yml`, a copy of `deploy/docker-compose.yml`.
  Watchtower lives in `~/docs-stack` and updates every container labelled
  `com.centurylinklabs.watchtower.enable=true`, this one included.
- **Cloudflare**: the shared `public-fleet` tunnel, a host-network cloudflared on doodlessd whose
  config lives in the K3s repo (`services/edge/public-fleet/config.yml`). It routes `tdpdf.com` and
  `www.tdpdf.com` to `http://127.0.0.1:18106`. Until 2026-10-30 the site had its own tunnel,
  `tdpdf-site`, and a `tdpdf-site-tunnel` container; both are retired.
- nginx answers `www.tdpdf.com` with a 301 to `https://tdpdf.com`, and sends the old Ghost URLs
  `/home/`, `/news/` and `/rss/` to `/`.
- The container publishes only on loopback (`127.0.0.1:18106`). The tunnel is the only way in;
  do not add an NPM proxy host.

To redeploy by hand: Actions ▸ site ▸ Run workflow. To restart on doodlessd:
`cd ~/tdpdf-site-stack && docker compose pull && docker compose up -d`.

## History

Until 2026-10-23 tdpdf.com was a Ghost site on Cloudron, published through Ghost's Admin API by
`build/ghost/publish-site.ps1`. The pages and styles were carried over unchanged; only the frame
around them is new.
