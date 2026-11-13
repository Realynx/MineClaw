# CI and releases

Two GitHub Actions workflows live in `.github/workflows/`. Neither needs a secret on pull requests, so PRs from forks run
the same checks with a read-only token.

## CI (`ci.yml`): every pull request and every push to main

| Job | What it runs |
|---|---|
| `dotnet <project>` | One job per test project under `tests/` (14 jobs, in parallel on separate runners), through `tests/ci/dotnet_test.py`. They run on **Windows** runners like the development machines: the golden tests (renders, TUI frames, bit-exact numerics) are recorded there and several differ on Linux. The release builds and the install tests run on Linux |
| `web` | `npm ci`, then `turbo run typecheck`, `lint` and `test` (vitest, `--retry=1`) in `web/` |
| `scripts` | `bash -n` + shellcheck (errors gate, warnings are reported) on `deploy/**/*.sh`, `helm lint`/`helm template` of the chart, `docker compose config` of the compose file |

### Flaky and known-failing tests

- **Retry.** `tests/ci/dotnet_test.py` runs a project, and when tests fail it runs **only the failed ones** once more.
  A test that passes on the retry is reported as *flaky* (a warning annotation on the run) and doesn't fail the job; a test
  that fails twice does. Vitest gets the same single retry (`--retry=1`).
- **Quarantine.** `tests/ci/quarantine.txt` lists tests that fail reliably in CI for reasons outside the code under test
  (game files that only exist on a developer machine, wall-clock timing on 2-core runners). One line per entry:
  `<project dir> <fully-qualified name prefix>  # why, since when`. They are left out of the gating run and then run on
  their own, report-only; CI posts a notice when all of a project's quarantined tests pass so the entry can go.
- Run the same thing locally: `python3 tests/ci/dotnet_test.py tests/MineClaw.Bot.Tests`.

## Releases (`release.yml`)

Push a tag `vX.Y.Z` and the workflow builds, tests and publishes everything:

```bash
git tag -a v1.2.3 -m "MineClaw 1.2.3" && git push origin v1.2.3
```

| Tag | Result |
|---|---|
| `v1.2.3` | GitHub Release marked latest; images `:1.2.3`, `:1.2` and `:latest`; chart `1.2.3` |
| `v1.2.3-rc.1` | a prerelease; images `:1.2.3-rc.1` only (no `:latest`) |
| `v1.2.3-test.1` | a **draft** prerelease, for trying the pipeline; nothing is visible until you publish or delete it |

A pull request that touches `deploy/`, `tests/ci/` or the workflow itself runs the whole release workflow as a **dry run**
(version `0.0.0-ci.<run>`): everything is built and the three installs are tested, but nothing is pushed or released.
`workflow_dispatch` does the same for a version you type in.

### What gets built

| Job | Output |
|---|---|
| `dotnet` (linux-x64, linux-arm64, win-x64) | `mineclaw-server-<v>-<rid>.tar.gz` (`.zip` for Windows) and, for Linux, `mineclaw-bridge-<v>-<rid>.tar.gz`, by `deploy/release/package-dotnet.sh` (it uses `deploy/publish-linux.sh`): self-contained single files, stamped `InformationalVersion <v>+<commit>`, with a `RELEASE.json` |
| `web` | `mineclaw-web-<v>-linux-{x64,arm64}.tar.gz` by `deploy/release/assemble-web.sh`: the same tree `deploy/deploy-web.ps1` builds (Nest + its production deps for that arch, the Next standalone server) |
| `installers` | `install.sh`, `install-docker.sh`, `docker-compose.yml` and the Helm chart `mineclaw-<v>.tgz` |
| `images` | `ghcr.io/realynx/mineclaw-server` (bot + bridge) and `ghcr.io/realynx/mineclaw-web`, linux/amd64 + linux/arm64. The Dockerfiles only copy the prebuilt trees, so the arm64 image needs no emulated compile |
| `test-linux` | `install.sh` on the runner (a real systemd host): install, reinstall, an update request, uninstall |
| `test-docker` | `install-docker.sh` with the freshly built images: health, setup mode, reinstall, uninstall |
| `test-helm` | the packaged chart in a kind cluster: the pod becomes ready, health, upgrade, uninstall |
| `release` | only after all tests pass: `SHA256SUMS`, the chart pushed to `oci://ghcr.io/realynx/charts`, `:X.Y`/`:latest` tags, the GitHub Release with an install block and generated notes |

No game assets are ever packaged: the bot's 1.12.2 language file is only copied when a local launcher install has the
client jar (CI runners don't), and `package-dotnet.sh` removes it anyway; the web viewer imports textures from the
user's own jar at runtime.

### Version stamping

- .NET: `-p:Version=X.Y.Z -p:InformationalVersion=X.Y.Z[-pre]+<commit>`; `MineClaw.App --version` prints it, and the
  Administration page shows it.
- Every tarball carries `RELEASE.json` (`version`, `commit`, `built`, `component`). The web API reads its own (or
  `MINECLAW_VERSION`, which the images set) and the update checker compares it with GitHub Releases
  ([install.md › Updating](install.md#updating)).

### Container packages

The first publish creates the GHCR packages `mineclaw-server`, `mineclaw-web` and `charts/mineclaw` under the
organisation. If they come out private, make them public once in the organisation's package settings
(Packages → the package → Package settings → Change visibility) so installs can pull without a login.
