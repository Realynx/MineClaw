#!/usr/bin/env bash
# Release manager that runs INSIDE the MineClaw container. deploy/deploy-alice.ps1 pushes it to
# /usr/local/sbin/mineclaw-release on every deploy. You can also run it by hand: pct exec 114 -- /usr/local/sbin/mineclaw-release status
#
#   mineclaw-release install <tarball> <release-id> [--unit FILE] [--no-start] [--timeout SEC]
#   mineclaw-release rollback [--timeout SEC] [--dry-run] [--force]
#                                                  # current -> the next older release, restart, health check; the db
#                                                  # goes back to that release's backup when the live schema is newer
#   mineclaw-release restart  [--timeout SEC]
#   mineclaw-release health   [--timeout SEC] [--since EPOCH]
#   mineclaw-release status
#   mineclaw-release backups                       # db backups (taken at every install, tagged with the release)
#
# Layout: /opt/mineclaw/releases/<yyyyMMdd-HHmmss>/ and /opt/mineclaw/current -> releases/<id> (atomic rename swap).
# The newest $KEEP releases (default 3) are kept, and the current one always stays.
# Data: /var/lib/mineclaw/mineclaw.db is never overwritten by an install. If it doesn't exist yet, the committed clean-state
# seed is staged at /var/lib/mineclaw/seed/mineclaw-seed.db and the app bootstraps the db from it on first start.
# Every install first copies the db (service stopped: a WAL db is only consistent closed) to
# /var/lib/mineclaw/backups/mineclaw-<utc>-rel<release it ran under>-v<schema>.db, and records the schema the new build
# opens in releases/<id>/SCHEMA_VERSION (MineClaw.App --schema-version). A rollback to a build whose schema is older than
# the live db restores that build's backup (the live db is moved aside, never deleted), or refuses when there is none
# (live 10-27: the previous build couldn't open schema v6 and crash-looped). --dry-run prints the plan and changes nothing.
set -euo pipefail
export LC_ALL=C

APP=${MINECLAW_APP:-/opt/mineclaw}
REL=$APP/releases
CUR=$APP/current
DATA=${MINECLAW_DATA:-/var/lib/mineclaw}
DB=$DATA/mineclaw.db
BACKUPS=$DATA/backups
KEEP_BACKUPS=${MINECLAW_KEEP_BACKUPS:-8}
# Test seam (tests/MineClaw.App.Tests/ReleaseScriptTests.cs): no root check, and "current" may be a plain file holding the id
# where the test machine can't make symlinks.
TEST=${MINECLAW_RELEASE_TEST:-}
UNIT=/etc/systemd/system/mineclaw.service
KEEP=${MINECLAW_KEEP:-3}
# Log lines the app writes once it is in the world (first connect, and every reconnect).
JOIN_RE='joined .* as |in game:'

TIMEOUT=180 SINCE="" UNIT_SRC="" NO_START=0 DRY=0 FORCE=0

die() { echo "ERROR: $*" >&2; exit 1; }
current_id() {
    if [[ -L $CUR ]]; then basename "$(readlink "$CUR")"
    elif [[ -n $TEST && -f $CUR ]]; then cat "$CUR"
    fi
    return 0
}
releases() { ls -1 "$REL" 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort || true; }

swap_to() { # new symlink beside the old one, then rename(2) over it: never a moment without "current"
    if [[ -n $TEST && ! -L $CUR ]]; then echo "$1" > "$CUR"; return 0; fi
    ln -sfn "releases/$1" "$APP/.current.new"
    mv -Tf "$APP/.current.new" "$CUR"
}

u32be() { # 4 bytes at offset $2 of file $1 as a big-endian unsigned int
    local b; b=$(od -An -tu1 -j"$2" -N4 "$1" 2>/dev/null) || return 1
    set -- $b
    (($# == 4)) || return 1
    echo $(( ($1 << 24) | ($2 << 16) | ($3 << 8) | $4 ))
}

db_schema() { # PRAGMA user_version of a SQLite db ("" = no db): header offset 60, or the newest page-1 frame in its WAL
    local f=$1 v ps size n i off pg wv
    [[ -s $f ]] || return 0
    if command -v sqlite3 >/dev/null 2>&1 && v=$(sqlite3 -readonly "$f" 'PRAGMA user_version;' 2>/dev/null) && [[ $v =~ ^[0-9]+$ ]]; then
        echo "$v"; return 0
    fi
    v=$(u32be "$f" 60) || return 0
    if [[ -s $f-wal ]]; then # an uncheckpointed migration lives in the WAL: frames are a 24-byte header + one page
        ps=$(u32be "$f-wal" 8) || ps=0
        size=$(stat -c %s "$f-wal")
        if ((ps > 0)); then
            n=$(( (size - 32) / (24 + ps) ))
            for ((i = 0; i < n; i++)); do
                off=$(( 32 + i * (24 + ps) ))
                pg=$(u32be "$f-wal" "$off") || break
                if ((pg == 1)) && wv=$(u32be "$f-wal" $((off + 24 + 60))); then v=$wv; fi
            done
        fi
    fi
    echo "$v"
}

schema_text() { if [[ -n $1 ]]; then echo "schema v$1"; else echo "${2:-none}"; fi; }

release_schema() { cat "$REL/$1/SCHEMA_VERSION" 2>/dev/null || true; }

record_schema() { # the schema the release's build opens, asked from its own binary (an old build without the flag exits 64)
    local id=$1 v
    v=$(timeout 20 "$REL/$id/MineClaw.App" --schema-version 2>/dev/null | head -n1 | tr -d '[:space:]' || true)
    if [[ $v =~ ^[0-9]+$ ]]; then echo "$v" > "$REL/$id/SCHEMA_VERSION"; echo "schema: releases/$id opens db schema v$v"
    else echo "schema: releases/$id didn't report its db schema (a rollback to it needs a matching backup)"; fi
}

backup_db() { # $1 = the release the db ran under (its tag); stops the service for a consistent copy
    local tag=${1:-none} v name was=0
    if [[ ! -e $DB ]]; then echo "backup: no db yet"; return 0; fi
    systemctl is-active --quiet mineclaw && was=1
    if ((was)); then systemctl stop mineclaw; fi
    v=$(db_schema "$DB")
    mkdir -p "$BACKUPS"; chmod 0750 "$BACKUPS" 2>/dev/null || true
    name="mineclaw-$(date -u +%Y%m%dT%H%M%SZ)-rel$tag-v${v:-unknown}.db"
    cp -p "$DB" "$BACKUPS/$name"
    if [[ -s $DB-wal ]]; then cp -p "$DB-wal" "$BACKUPS/$name-wal"; fi
    chown --reference="$DB" "$BACKUPS" "$BACKUPS/$name"* 2>/dev/null || true
    echo "backup: $BACKUPS/$name (db schema v${v:-?}, taken under release $tag)"
    if ((was && NO_START)); then systemctl start mineclaw; fi # --no-start: the old build keeps running
    prune_backups
    return 0
}

prune_backups() {
    ls -1 "$BACKUPS" 2>/dev/null | grep -E '^mineclaw-.*\.db$' | sort -r | tail -n +$((KEEP_BACKUPS + 1)) | while read -r b; do
        rm -f -- "$BACKUPS/$b" "$BACKUPS/$b-wal" && echo "pruned backup $b"
    done || true
    return 0
}

backup_for() { # newest backup taken under release $1 whose schema is <= $2 (any schema when $2 is empty)
    local id=$1 max=$2 b v
    ls -1 "$BACKUPS" 2>/dev/null | grep -E "^mineclaw-.*-rel$id-v[0-9]+\.db$" | sort -r | while read -r b; do
        v=${b##*-v}; v=${v%.db}
        if [[ -z $max ]] || ((v <= max)); then echo "$b"; break; fi
    done || true
    return 0
}

prune() {
    local cur; cur=$(current_id)
    rm -rf -- "$REL"/*.tmp
    releases | sort -r | tail -n +$((KEEP + 1)) | while read -r r; do
        [[ $r == "$cur" ]] && continue
        rm -rf -- "${REL:?}/$r" && echo "pruned release $r"
    done
}

health() { # $1 timeout, $2 epoch: wait for "active" + a join line logged after $2
    local timeout=$1 since=$2 t=0 line
    while ((t < timeout)); do
        line=$(journalctl -u mineclaw --since "@$since" -o short-iso --no-pager 2>/dev/null | grep -m1 -E "$JOIN_RE" || true)
        if [[ -n $line ]] && systemctl is-active --quiet mineclaw; then
            echo "HEALTHY after ${t}s: $line"
            return 0
        fi
        sleep 2
        t=$((t + 2))
    done
    echo "UNHEALTHY after ${t}s: service is $(systemctl is-active mineclaw || true); recent log:"
    journalctl -u mineclaw --since "@$since" -o short-iso --no-pager | tail -n 25
    return 1
}

restart() {
    local since; since=$(date +%s)
    systemctl restart mineclaw
    health "$TIMEOUT" "$since"
}

install_release() {
    local tarball=$1 id=$2 prev
    [[ -f $tarball ]] || die "no tarball at $tarball"
    [[ $id =~ ^[0-9]{8}-[0-9]{6}$ ]] || die "bad release id '$id' (want yyyyMMdd-HHmmss)"
    id -u mineclaw >/dev/null 2>&1 || die "user mineclaw missing: run deploy/provision-alice.sh first"
    [[ -e $REL/$id ]] && die "release $id already exists"
    [[ -f /etc/mineclaw/appsettings.json ]] || die "/etc/mineclaw/appsettings.json missing (deploy with -PushConfig)"

    install -d -m 0755 "$REL"
    rm -rf -- "$REL/$id.tmp"
    mkdir -p "$REL/$id.tmp"
    tar -xzf "$tarball" -C "$REL/$id.tmp" --strip-components=1 --no-same-owner
    [[ -f $REL/$id.tmp/MineClaw.App ]] || die "tarball has no MineClaw.App"
    chmod -R u=rwX,go=rX "$REL/$id.tmp"
    chmod 0755 "$REL/$id.tmp/MineClaw.App"
    mv -T "$REL/$id.tmp" "$REL/$id"
    echo "unpacked releases/$id ($(du -sh "$REL/$id" | cut -f1))"

    if [[ -e $DATA/mineclaw.db ]]; then
        echo "data: existing mineclaw.db kept"
    elif [[ -f $REL/$id/seed/mineclaw-seed.db ]]; then
        install -d -m 0750 -o mineclaw -g mineclaw "$DATA" "$DATA/seed"
        install -m 0640 -o mineclaw -g mineclaw "$REL/$id/seed/mineclaw-seed.db" "$DATA/seed/mineclaw-seed.db"
        echo "data: no db yet; staged clean seed, the app bootstraps mineclaw.db from it on start"
    else
        echo "data: no db and no seed in the release; the app starts with an empty db"
    fi

    if [[ -n $UNIT_SRC ]]; then
        if cmp -s "$UNIT_SRC" "$UNIT"; then echo "unit: unchanged"; else install -m 0644 "$UNIT_SRC" "$UNIT" && echo "unit: installed/updated"; fi
    fi
    [[ -f $UNIT ]] || die "no systemd unit at $UNIT (pass --unit)"
    systemctl daemon-reload
    systemctl enable --quiet mineclaw

    record_schema "$id"
    prev=$(current_id)
    backup_db "${prev:-none}"
    swap_to "$id"
    echo "current -> releases/$id (previous: ${prev:-none})"
    prune
    if ((NO_START)); then
        echo "not (re)started (--no-start); service is $(systemctl is-active mineclaw || true)"
        return 0
    fi
    restart
}

rollback() {
    local cur prev live want bk aside cs
    cur=$(current_id)
    prev=$(releases | awk -v c="$cur" '$0 < c' | tail -n1)
    [[ -n $prev ]] || die "no release older than '${cur:-none}' to roll back to"
    live=$(db_schema "$DB")
    want=$(release_schema "$prev")
    cs=$(release_schema "$cur")
    echo "rollback: releases/$cur (db schema v${cs:-?}) -> releases/$prev (v${want:-?}); live db: $(schema_text "$live")"
    bk=""
    if [[ -z $live ]]; then
        echo "db: none yet, nothing to match"
    elif [[ -n $want ]] && ((live <= want)); then
        echo "db: schema v$live opens with releases/$prev (v$want): kept as it is"
    else
        bk=$(backup_for "$prev" "$want")
        if [[ -n $bk ]]; then
            echo "db: live schema v$live is newer than releases/$prev can open (v${want:-?}): restoring $BACKUPS/$bk"
            echo "db: the live db moves to $BACKUPS/mineclaw-<now>-rolledback-from-rel$cur-v$live.db (nothing is deleted)"
        elif ((FORCE)); then
            echo "db: WARNING --force: live schema v$live, releases/$prev opens v${want:-?} and there is no backup for it; it may crash-loop"
        else
            die "refusing to roll back: the live db is schema v$live but releases/$prev opens $(schema_text "$want" "an unknown schema") and no backup taken under releases/$prev is in $BACKUPS. Roll forward (redeploy), restore a backup by hand (mineclaw-release backups), or pass --force"
        fi
    fi
    if ((DRY)); then echo "dry run: nothing changed"; return 0; fi
    if [[ -n $bk ]]; then
        systemctl stop mineclaw || true
        aside="mineclaw-$(date -u +%Y%m%dT%H%M%SZ)-rolledback-from-rel$cur-v$live.db"
        mkdir -p "$BACKUPS"; chmod 0750 "$BACKUPS" 2>/dev/null || true
        mv -f "$DB" "$BACKUPS/$aside"
        if [[ -e $DB-wal ]]; then mv -f "$DB-wal" "$BACKUPS/$aside-wal"; fi
        rm -f "$DB-shm"
        cp -p "$BACKUPS/$bk" "$DB"
        if [[ -e $BACKUPS/$bk-wal ]]; then cp -p "$BACKUPS/$bk-wal" "$DB-wal"; fi
        chown --reference="$BACKUPS/$aside" "$DB"* 2>/dev/null || true
        echo "db: restored $bk (schema v$(db_schema "$DB")); the newer db is kept at $BACKUPS/$aside"
    fi
    swap_to "$prev"
    echo "rolled back: current -> releases/$prev (was $cur)"
    restart
}

list_backups() {
    local live; live=$(db_schema "$DB")
    echo "db backups in $BACKUPS (live db: $(schema_text "$live")):"
    ls -1 "$BACKUPS" 2>/dev/null | grep -E '^mineclaw-.*\.db$' | sed 's/^/  /' || echo "  none"
}

status() {
    echo "release:  $(current_id || true)   [kept: $(releases | tr '\n' ' ')]"
    systemctl show mineclaw -p ActiveState -p SubState -p UnitFileState -p NRestarts -p ExecMainStartTimestamp --no-pager | sed 's/^/  /'
    echo "db:       $(stat -c '%s bytes, modified %y' "$DATA/mineclaw.db" 2>/dev/null || echo 'not created yet')"
    echo "last join: $(journalctl -u mineclaw -o short-iso --no-pager 2>/dev/null | grep -E "$JOIN_RE" | tail -n1 || true)"
    echo "--- last 15 log lines"
    journalctl -u mineclaw -n 15 -o short-iso --no-pager 2>/dev/null || true
}

cmd=${1:-}; shift || true
args=()
while (($#)); do
    case $1 in
        --timeout) TIMEOUT=$2; shift 2 ;;
        --since) SINCE=$2; shift 2 ;;
        --unit) UNIT_SRC=$2; shift 2 ;;
        --no-start) NO_START=1; shift ;;
        --dry-run) DRY=1; shift ;;
        --force) FORCE=1; shift ;;
        *) args+=("$1"); shift ;;
    esac
done
[[ -n $TEST || $(id -u) -eq 0 ]] || die "run as root"
case $cmd in
    install) ((${#args[@]} == 2)) || die "usage: install <tarball> <release-id>"; install_release "${args[0]}" "${args[1]}" ;;
    rollback) rollback ;;
    backups) list_backups ;;
    restart) restart ;;
    health) health "$TIMEOUT" "${SINCE:-$(date -d '-10 min' +%s)}" ;;
    status) status ;;
    *) sed -n '2,23p' "$0"; exit 64 ;;
esac
