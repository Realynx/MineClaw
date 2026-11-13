# Installing and updating MineClaw

MineClaw has two halves that always run side by side:

- the **control server**: the bot (`MineClaw.App`, one Claude agent per bot, Story Mode, the Supervisor API on loopback);
- the **web panel**: the Web Explorer (NestJS on port 8080 in front of the Next.js UI) and the viewer bridge that connects
  it to the bot.

Every release ships three ways to install both. Each is one command, and none asks questions in a terminal: you finish the
setup in the browser.

| | One command | Good for |
|---|---|---|
| [Docker compose](#docker-compose) | `curl -fsSL …/install-docker.sh \| sudo bash` | one machine with Docker |
| [Linux + systemd](#linux-systemd) | `curl -fsSL …/install.sh \| sudo bash` | a VM, a Proxmox/LXC container, a Raspberry Pi 4/5 (arm64) |
| [Kubernetes (Helm)](#kubernetes-helm) | `helm install mineclaw oci://ghcr.io/realynx/charts/mineclaw` | a cluster you already run |

The downloads live on the [releases page](https://github.com/Realynx/MineClaw/releases); `…` above is
`https://github.com/Realynx/MineClaw/releases/latest/download`. Every asset is listed in the release's `SHA256SUMS`, and
the installers check what they download against it.

## First run (all install paths)

1. Open the web panel (`http://<host>:8080`). The **first visit creates the admin account**. Whoever completes that page
   first becomes the admin, so do it right after installing, from your own network.
2. The bot starts in **setup mode** when it has no Anthropic API key or no Minecraft server yet: it keeps running and
   waits (`setup mode: waiting for configuration …` in its log). Open **Administration**, set **Models & providers › API
   key** and **Bot & chat › Server host / port**, save, and restart the bot from the same page. It joins the server.
3. Everything else (models, budgets, trusted players, hive limits) is on the same page. The values you save there are
   kept in `settings.override.json` on the bot's data volume and win over the installer's config.

You can also hand the key and server to the installer (flags below) and skip step 2.

## Docker compose

```bash
curl -fsSL https://github.com/Realynx/MineClaw/releases/latest/download/install-docker.sh | sudo bash
```

- It needs Docker with the compose plugin (v2).
- It creates `/opt/mineclaw-docker` with `docker-compose.yml` and a `.env` (0600) holding random secrets (the bridge key,
  the Supervisor token), pulls `ghcr.io/realynx/mineclaw-server` and `mineclaw-web`, and starts them.
- Options: `--dir DIR`, `--version X.Y.Z`, `--port 8080`, `--mc-host`, `--mc-port`, `--mc-username`,
  `--origins https://mineclaw.example.com` (the browser origins the panel accepts: add your HTTPS hostname when you put
  it behind a reverse proxy).
- The API key is never a flag (it would end up in your shell history): set it in Administration, or pass it through the
  environment: `curl … | sudo ANTHROPIC_API_KEY=sk-ant-… bash`.
- Containers: `net` (holds the shared network namespace and publishes the port), `server` (the bot), `bridge` and
  `web`. The three share loopback like they do on a single host. Data lives in named volumes.
- Re-running is safe: `.env` is kept, only the flags you pass change. Remove with `--uninstall` (keeps the volumes) or
  `--uninstall --purge` (volumes, images and the directory too).

## Linux (systemd)

```bash
curl -fsSL https://github.com/Realynx/MineClaw/releases/latest/download/install.sh | sudo bash
```

- It supports Debian and Ubuntu, x86-64 and arm64. Other distributions are best effort.
- It installs the bot, the bridge and the web panel as systemd services with their own users (`mineclaw`,
  `mineclaw-bridge`, `mineclaw-web`), hardened units, and Node.js 22 (official build, checksum-pinned) for the panel.
- Layout:
  - `/opt/mineclaw{,-web,-bridge}/releases/<id>` with a `current` symlink (the last 3 are kept);
  - config in `/etc/mineclaw` and `/etc/mineclaw-web`;
  - data in `/var/lib/mineclaw` and `/var/lib/mineclaw-web`;
  - logs in journald and `/var/log/mineclaw`.
- Options:
  - `--version X.Y.Z`;
  - `--components server` (bot only; the default is `server,web`);
  - `--mc-host H --mc-port P --mc-username U`;
  - `--api-key-file FILE` (the key goes to `/etc/mineclaw/mineclaw.env`, 0600);
  - `--origin https://mineclaw.example.com` (repeatable);
  - `--port 8080`, `--no-start`, `--dry-run`;
  - `--from DIR`, to install from downloaded assets without network access to GitHub.
- Re-running is safe: config, keys, users and data are kept. To repair, run it again. `--reinstall` forces a fresh
  release directory.
- Remove with `--uninstall` (keeps config and data) or `--uninstall --purge --yes` (everything).
- Day to day:

  ```bash
  journalctl -u mineclaw -f            # the bot
  mineclaw-release status              # bot release, state, last join
  mineclaw-web-release status          # web stack
  mineclaw-update status               # the last update
  ```

## Kubernetes (Helm)

```bash
helm install mineclaw oci://ghcr.io/realynx/charts/mineclaw -n mineclaw --create-namespace \
  --set minecraft.host=mc.example.com \
  --set ingress.enabled=true --set ingress.className=nginx \
  --set 'ingress.hosts[0].host=mineclaw.example.com' --set 'ingress.hosts[0].paths[0].path=/'
```

- One StatefulSet (one replica: the bot is a singleton). Its pod runs three containers, `bot`, `bridge` and `web`, that
  share loopback.
- Values (see `deploy/helm/mineclaw/values.yaml`):
  - `minecraft.host/port/username`;
  - `anthropic.apiKey`, or `anthropic.existingSecret` + `existingSecretKey`;
  - `persistence.*` (the data PVC; set `storageClass` and `size`);
  - `ingress.*`, `web.allowedOrigins` (add your ingress URL), `web.trustProxy`;
  - `config` (any extra bot appsettings), `resources`.
- The bridge key and the Supervisor token are generated on the first install and kept across upgrades.
- **The first admin account can't be created through the ingress** (the setup page only answers direct connections).
  Create it over a port-forward, then use the ingress:

  ```bash
  kubectl -n mineclaw port-forward svc/mineclaw 8080:8080   # then open http://localhost:8080
  ```

## Updating

The web panel checks GitHub Releases for a newer version: at start, then every 12 hours, cached, with conditional
requests so it stays far below GitHub's rate limits. When one is out, every signed-in user sees **"MineClaw vX.Y.Z is
available"** with the release notes. Admins also get the way to update for their install:

| Install | In the panel | By hand |
|---|---|---|
| Linux (systemd) | **Update now** (admins) | `sudo mineclaw-update apply X.Y.Z` |
| Docker compose | the command to run | `cd /opt/mineclaw-docker && sudo sed -i 's/^MINECLAW_VERSION=.*/MINECLAW_VERSION=X.Y.Z/' .env && sudo docker compose pull && sudo docker compose up -d`, or re-run `install-docker.sh --version X.Y.Z` |
| Helm | the command to run | `helm upgrade mineclaw oci://ghcr.io/realynx/charts/mineclaw --version X.Y.Z -n mineclaw --reuse-values` |

**Update now** (systemd only) works like this:

1. The panel never runs anything as root. It writes an update request to `/var/lib/mineclaw-web/update/request.json`.
2. A systemd path unit starts `mineclaw-update` as root. It takes the repository only from `/etc/mineclaw/install.conf`
   and accepts only a plain version number.
3. It downloads that release's `SHA256SUMS` and assets, verifies them, and installs them through the same release
   manager as a fresh install (a new release directory, then an atomic `current` swap).
4. It health-checks the bot and the web stack. **If either fails, it rolls both back** to the releases that ran
   before. The bot's database is backed up before every install.
5. The panel shows the progress and the outcome (`succeeded`, `failed`, `rolled-back`) from `status.json`.

The panel doesn't trigger Docker or Helm upgrades itself: that would need the Docker socket or cluster permissions
inside the web container.

To turn the check off (no network calls at all), set `Web.Updates.Enabled` to `false` in `/etc/mineclaw-web/config.json`,
or set the environment variable `MINECLAW_WEB_Web__Updates__Enabled=false` (compose `.env`, or Helm
`web.extraEnv`). `Web.Updates.Prereleases: true` also offers release candidates.

## Building releases

See [release.md](release.md): CI, how a tag becomes a release, and the flaky-test quarantine.
