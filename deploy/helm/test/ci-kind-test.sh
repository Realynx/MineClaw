#!/usr/bin/env bash
# CI test of the Helm chart on kind (.github/workflows/release.yml, job test-helm).
#
#   deploy/helm/test/ci-kind-test.sh <chart.tgz> <version> <image-prefix e.g. ghcr.io/realynx> <kind-cluster-name>
#
# Expects the kind cluster to exist and the images <prefix>/mineclaw-{server,web}:<version> in the runner's docker. Loads
# them into kind, installs the packaged chart with ci/kind-values.yaml, waits for the Pod to be Ready (web readiness =
# /api/health), checks /api/health (bridge up), /api/setup (setup needed) and the bot's `setup mode:` log line through a
# port-forward, runs `helm upgrade --reuse-values` once (the generated secrets must stay the same), then uninstalls.
# On failure it prints describe, events and every container's logs.
set -euo pipefail
(($# == 4)) || { sed -n '2,11p' "$0"; exit 64; }
CHART=$1 VER=$2 PREFIX=${3%/} CLUSTER=$4
HERE=$(cd "$(dirname "$0")" && pwd)
VALUES=$HERE/../mineclaw/ci/kind-values.yaml
NS=mineclaw-ci
REL=mineclaw
LOCAL_PORT=18080
SEL="app.kubernetes.io/instance=$REL"
PF_PID=""

[[ -f $CHART ]] || { echo "no chart package at $CHART" >&2; exit 1; }
[[ -f $VALUES ]] || { echo "no $VALUES" >&2; exit 1; }

step() { echo; echo "::group::$*"; }
endstep() { echo "::endgroup::"; }
stop_pf() { [[ -n $PF_PID ]] && kill "$PF_PID" 2>/dev/null || true; PF_PID=""; }
on_fail() {
    local rc=$?
    echo "::endgroup::"
    echo "::error::helm/kind test failed (exit $rc)"
    stop_pf
    kubectl -n "$NS" get all,pvc,secret -o wide || true
    kubectl -n "$NS" describe statefulset,pod -l "$SEL" || true
    kubectl -n "$NS" get events --sort-by=.lastTimestamp | tail -n 60 || true
    for pod in $(kubectl -n "$NS" get pod -l "$SEL" -o name 2>/dev/null); do
        for c in bot bridge web; do
            echo "---- $pod/$c"
            kubectl -n "$NS" logs "$pod" -c "$c" --tail 300 || true
            kubectl -n "$NS" logs "$pod" -c "$c" --previous --tail 100 2>/dev/null || true
        done
    done
    exit "$rc"
}
trap on_fail ERR
trap stop_pf EXIT

wait_for() {
    local secs=$1 what=$2; shift 2
    local end=$((SECONDS + secs))
    until "$@"; do
        ((SECONDS < end)) || { echo "timed out after ${secs}s waiting for $what" >&2; return 1; }
        sleep 3
    done
    echo "ok: $what"
}
pod() { kubectl -n "$NS" get pod -l "$SEL" -o jsonpath='{.items[0].metadata.name}'; }
port_forward() {
    stop_pf
    kubectl -n "$NS" port-forward "svc/$REL" "$LOCAL_PORT:8080" >/dev/null 2>&1 &
    PF_PID=$!
}
health_up() {
    local body
    body=$(curl -fsS --max-time 5 "http://127.0.0.1:$LOCAL_PORT/api/health" 2>/dev/null) || { kill -0 "$PF_PID" 2>/dev/null || port_forward; return 1; }
    [[ $body == *'"ok":true'* && $body == *'"bridge":"up"'* ]] || { echo "health: $body"; return 1; }
}
setup_needed() {
    local body
    body=$(curl -fsS --max-time 5 "http://127.0.0.1:$LOCAL_PORT/api/setup") || return 1
    echo "setup: $body"
    [[ $body == *'"needed":true'* ]]
}
setup_mode_logged() { kubectl -n "$NS" logs "$(pod)" -c bot 2>/dev/null | grep -q 'setup mode:'; }
secret_hash() {
    local s
    s=$(kubectl -n "$NS" get secret "$REL-secrets" -o json | jq -er '.data["bridge-key"] + "/" + .data["supervisor-token"]')
    printf '%s' "$s" | sha256sum | cut -d' ' -f1
}

step "load images into kind ($CLUSTER)"
for img in mineclaw-server mineclaw-web; do kind load docker-image "$PREFIX/$img:$VER" --name "$CLUSTER"; done
endstep

step "helm install $CHART"
helm install "$REL" "$CHART" -n "$NS" --create-namespace -f "$VALUES" \
    --set image.server.repository="$PREFIX/mineclaw-server" --set image.server.tag="$VER" \
    --set image.web.repository="$PREFIX/mineclaw-web" --set image.web.tag="$VER" \
    --wait --timeout 8m
kubectl -n "$NS" wait --for=condition=Ready pod -l "$SEL" --timeout=5m
kubectl -n "$NS" get pod -l "$SEL" -o wide
endstep

step "health, first-run state"
port_forward
wait_for 300 "/api/health ok with the bridge up" health_up
wait_for 30 "/api/setup says setup is needed" setup_needed
wait_for 180 "the bot logged 'setup mode:'" setup_mode_logged
endstep

step "helm upgrade --reuse-values (secrets kept)"
before=$(secret_hash)
helm upgrade "$REL" "$CHART" -n "$NS" --reuse-values --wait --timeout 8m
after=$(secret_hash)
[[ $before == "$after" ]] || { echo "helm upgrade regenerated the bridge key / supervisor token" >&2; false; }
echo "ok: secrets unchanged"
kubectl -n "$NS" wait --for=condition=Ready pod -l "$SEL" --timeout=5m
port_forward
wait_for 300 "/api/health ok with the bridge up after the upgrade" health_up
endstep

step "helm uninstall"
stop_pf
helm uninstall "$REL" -n "$NS" --wait --timeout 5m
kubectl delete namespace "$NS" --wait=true --timeout=3m
endstep
trap - ERR
echo "helm/kind test passed"
