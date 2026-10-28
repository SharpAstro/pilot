#!/usr/bin/env bash
# PIL367 / issue 367: on a real Linux under systemd, a node installed as its user unit keeps a running job through a
# node restart (a saved setting) and through an update, reattaches it, ends it with its real exit code, and the unit is
# active afterwards.
#
#   wsl -d Ubuntu -- bash tests/linux/systemd-jobs.sh          (from the plane root; exit 0 is a pass)
#
# What it needs: a user systemd bus (it sets XDG_RUNTIME_DIR, and says so if that is not enough), python3 or zip, curl,
# and a linux-x64 pilot-node and pilot. It takes them from $PILOT_LINUX_PUBLISH (folders node/, cli/ and node-2.0.1/), by
# default the Windows %TEMP%\pilot-linux-publish, which the Windows side makes with (no AOT: that cannot cross-compile):
#   dotnet publish src/Pilot.Node -c Release -r linux-x64 --self-contained -p:PublishAot=false -o <dir>/node
#   dotnet publish src/Pilot.Cli  -c Release -r linux-x64 --self-contained -p:PublishAot=false -o <dir>/cli
#   dotnet publish src/Pilot.Node -c Release -r linux-x64 --self-contained -p:PublishAot=false -p:Version=2.0.1 -o <dir>/node-2.0.1
# The three share one obj folder, and an incremental build keeps the assembly version of the publish before it (the
# node then says 2.0.1 where 2.0.0 is wanted, or cannot load Pilot.Contracts 2.0.1.0), so delete
# src/Pilot.Contracts/{obj,bin}/Release and src/Pilot.Node/{obj,bin}/Release/net10.0/linux-x64 before each publish.
# When dotnet is on this Linux's PATH and no publish is there, it publishes the same itself.
#
# What it touches: a scratch folder under /tmp for the node's data, socket, install and release feed, never the real
# ones. One thing is shared with the real node: systemd's user manager reads units from ~/.config/systemd/user only, and
# the unit is always named pilot-node, so a unit of that name there is moved aside and put back on exit. A real one that
# is active makes the script stop instead.
set -u

PLANE=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
UNIT=pilot-node
UNIT_DIR="$HOME/.config/systemd/user"
UNIT_FILE="$UNIT_DIR/$UNIT.service"
WORK=$(mktemp -d /tmp/pilot-systemd-XXXXXX)
INSTALL="$WORK/install"
DATA="$WORK/data"
SOCK="$WORK/node.sock"
FEED="$WORK/feed"
BACKUP="$WORK/unit.backup"
JOBPID=""
FAILED=0

say() { printf '%s\n' "$*"; }
fail() { say "FAIL: $*"; FAILED=1; exit 1; }
ok() { say "ok: $*"; }

cleanup() {
    local code=$?
    set +e
    systemctl --user stop "$UNIT.service" >/dev/null 2>&1
    systemctl --user disable "$UNIT.service" >/dev/null 2>&1
    rm -f "$UNIT_FILE"
    [ -f "$BACKUP" ] && mv "$BACKUP" "$UNIT_FILE"
    systemctl --user daemon-reload >/dev/null 2>&1
    # The job's scope outlives the unit on purpose; this run's own is stopped here, not left for the next.
    # Jobs run in pilot-job-<id>-<hash>.scope; this run's are the ones whose processes mention the scratch folder.
    systemctl --user list-units --plain --no-legend 'pilot-job-*.scope' 2>/dev/null | awk '{print $1}' | while read -r scope; do
        cg=$(systemctl --user show -p ControlGroup --value "$scope" 2>/dev/null)
        [ -n "$cg" ] || continue
        for p in $(cat "/sys/fs/cgroup$cg/cgroup.procs" 2>/dev/null); do
            if tr '\0' ' ' < "/proc/$p/cmdline" 2>/dev/null | grep -qF "$WORK"; then
                systemctl --user stop "$scope" >/dev/null 2>&1
                break
            fi
        done
    done
    [ -n "$JOBPID" ] && kill "$JOBPID" >/dev/null 2>&1
    rm -rf "$WORK"
    [ "$FAILED" = 0 ] && [ "$code" = 0 ] && say "PASS" || say "exit $code"
    exit "$code"
}
trap cleanup EXIT

# --- 1. a user systemd bus
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
state=$(systemctl --user is-system-running 2>&1)
say "systemctl --user is-system-running: $state"
case "$state" in
    running|degraded) ;;
    *)
        say "no user manager answers; trying loginctl enable-linger $(id -un) (persistent: it stays on, undo with disable-linger)"
        loginctl enable-linger "$(id -un)" 2>&1
        for _ in $(seq 1 20); do
            state=$(systemctl --user is-system-running 2>&1)
            case "$state" in running|degraded) break ;; esac
            sleep 1
        done
        say "after linger: $state"
        case "$state" in running|degraded) ;; *) fail "WSL gives no user systemd bus ($state)" ;; esac
        ;;
esac
command -v systemd-run >/dev/null || fail "systemd-run is not installed"
command -v curl >/dev/null || fail "curl is not installed"

if [ -f "$UNIT_FILE" ]; then
    systemctl --user is-active --quiet "$UNIT.service" && fail "a real $UNIT unit is active here; stop it first (this run replaces the unit of that name)"
    cp "$UNIT_FILE" "$BACKUP" && say "moved the existing $UNIT_FILE aside; it is put back on exit"
fi

# --- 2. the binaries
if [ -z "${PILOT_LINUX_PUBLISH:-}" ]; then
    win_temp=$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')
    [ -n "$win_temp" ] && PILOT_LINUX_PUBLISH="$(wslpath "$win_temp")/pilot-linux-publish"
fi
PUB="${PILOT_LINUX_PUBLISH:-$WORK/publish}"
if [ ! -x "$PUB/node/pilot-node" ] || [ ! -x "$PUB/cli/pilot" ]; then
    command -v dotnet >/dev/null || fail "no linux-x64 build in $PUB and no dotnet to make one; see the header of this script"
    pub() { (cd "$PLANE" && dotnet publish "$1" -c Release -r linux-x64 --self-contained -p:PublishAot=false "${@:3}" -o "$PUB/$2") >/dev/null || fail "publish $1"; }
    pub src/Pilot.Node node
    pub src/Pilot.Cli cli
    pub src/Pilot.Node node-2.0.1 -p:Version=2.0.1
fi
[ -x "$PUB/node/pilot-node" ] && [ -x "$PUB/cli/pilot" ] || fail "no pilot-node and pilot in $PUB"

# The install folder: a release of 2.0.0 (its VERSION marker), as `pilot update` needs it to be.
stage() { # stage <node folder> <target folder> <version>
    mkdir -p "$2" && cp -r "$1"/. "$2"/ && cp -rn "$PUB/cli"/. "$2"/ 2>/dev/null
    : > "$2/pilot-gui"; : > "$2/pilot-tui"
    chmod +x "$2/pilot-node" "$2/pilot" "$2/pilot-gui" "$2/pilot-tui"
    (cd "$2" && { printf '%s linux-x64\n' "$3"; find . -type f ! -name VERSION | sed 's|^\./||' | sort; } > VERSION)
}
stage "$PUB/node" "$INSTALL" 2.0.0
NODE="$INSTALL/pilot-node"
CLI="$INSTALL/pilot"
"$NODE" --version | grep -q '^2\.0\.0' || fail "the node says $("$NODE" --version), not 2.0.0"

# --- 3. a release to update to, from a folder feed
mkdir -p "$FEED"
HAVE_UPDATE=0
if [ -x "$PUB/node-2.0.1/pilot-node" ]; then
    stage "$PUB/node-2.0.1" "$WORK/release" 2.0.1
    if command -v python3 >/dev/null; then
        (cd "$WORK/release" && python3 - "$FEED/pilot-2.0.1-linux-x64.zip" <<'PY'
import os, sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, names in os.walk("."):
        for n in names:
            p = os.path.join(root, n)
            z.write(p, os.path.relpath(p, "."))
PY
        ) && HAVE_UPDATE=1
    elif command -v zip >/dev/null; then
        (cd "$WORK/release" && zip -qr "$FEED/pilot-2.0.1-linux-x64.zip" .) && HAVE_UPDATE=1
    fi
    rm -rf "$WORK/release"
fi
[ "$HAVE_UPDATE" = 1 ] && ok "staged release 2.0.1 in a folder feed" || say "no 2.0.1 build or no zip tool: the update leg is skipped, the settings restart stands for it (both go through the unit)"

# --- 4. install as the user unit
mkdir -p "$DATA"
"$NODE" autostart install -- --data "$DATA" --node-socket "$SOCK" --no-watchtower --update-policy off --release-feed "$FEED" >"$WORK/install.out" 2>&1
cat "$WORK/install.out"
for _ in $(seq 1 30); do [ -S "$SOCK" ] && break; sleep 1; done
[ -S "$SOCK" ] || { journalctl --user -u "$UNIT" --no-pager -n 30; fail "the node did not answer on $SOCK"; }
systemctl --user is-active --quiet "$UNIT.service" || fail "the unit is not active after install"
main_pid() { systemctl --user show -p MainPID --value "$UNIT.service"; }
PID0=$(main_pid)
ok "$UNIT.service active, node pid $PID0"

pilot() { "$CLI" --node-socket "$SOCK" "$@"; }
wait_node_after() { # wait_node_after <old main pid>: the unit's node is a new process and answers again
    for _ in $(seq 1 60); do
        local now; now=$(main_pid)
        if [ -n "$now" ] && [ "$now" != 0 ] && [ "$now" != "$1" ] && pilot jobs >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    return 1
}
start_time() { awk '{print $22}' "/proc/$1/stat" 2>/dev/null; }

# --- 5. a long job
printf 'sleep 40\nexit 7\n' > "$WORK/job.sh"
out=$(cd "$WORK" && pilot run -- sh "$WORK/job.sh") || fail "pilot run: $out"
say "$out"
JOB=$(printf '%s' "$out" | sed -n 's/^job \(j[0-9]*\).*/\1/p')
[ -n "$JOB" ] || fail "no job id in: $out"
job_pid() { curl -s --unix-socket "$SOCK" "http://localhost/api/v1/jobs/$JOB/log" | grep -o '"pid":[ ]*[0-9]*' | head -1 | grep -o '[0-9]*$'; }
for _ in $(seq 1 20); do JOBPID=$(job_pid); [ -n "$JOBPID" ] && break; sleep 1; done
[ -n "$JOBPID" ] || { curl -s --unix-socket "$SOCK" "http://localhost/api/v1/jobs/$JOB/log"; fail "the job log gives no pid"; }
T0=$(start_time "$JOBPID")
[ -n "$T0" ] || fail "job pid $JOBPID is not alive"
ok "job $JOB runs as pid $JOBPID (start time $T0)"
grep -q "$UNIT.service" "/proc/$JOBPID/cgroup" && { cat "/proc/$JOBPID/cgroup"; fail "the job is in the node unit's cgroup"; }
ok "the job is outside the unit's cgroup: $(cat "/proc/$JOBPID/cgroup")"

check_carried() { # check_carried <what>
    [ "$(start_time "$JOBPID")" = "$T0" ] || fail "$1: job pid $JOBPID is gone or has another start time"
    ok "$1: job pid $JOBPID alive, same start time"
    pilot jobs | grep "^$JOB " | grep -q running || { pilot jobs; fail "$1: the new node does not list $JOB as running"; }
    ok "$1: the new node lists $JOB running"
    [ "$(job_pid)" = "$JOBPID" ] || fail "$1: the new node follows another pid ($(job_pid))"
    systemctl --user is-active --quiet "$UNIT.service" || fail "$1: the unit is not active"
    ok "$1: $UNIT.service is active"
}

# --- 6. a settings restart
PID=$(main_pid)
pilot settings set lan-port 47653 || fail "settings set"
wait_node_after "$PID" || { journalctl --user -u "$UNIT" --no-pager -n 40; fail "the unit did not bring the node back after the settings restart"; }
check_carried "settings restart"

# --- 7. an update
if [ "$HAVE_UPDATE" = 1 ]; then
    PID=$(main_pid)
    pilot update || fail "pilot update"
    wait_node_after "$PID" || { journalctl --user -u "$UNIT" --no-pager -n 40; fail "the unit did not bring the node back after the update"; }
    head -1 "$INSTALL/VERSION" | grep -q '^2\.0\.1 ' || fail "the install is not 2.0.1: $(head -1 "$INSTALL/VERSION")"
    "$NODE" --version | grep -q '^2\.0\.1' || fail "the node says $("$NODE" --version) after the update"
    ok "update: installed 2.0.1 in place"
    check_carried "update"
fi

# --- 8. the job ends with its own exit code
for _ in $(seq 1 90); do
    kill -0 "$JOBPID" 2>/dev/null || break
    sleep 1
done
kill -0 "$JOBPID" 2>/dev/null && fail "the job did not end"
for _ in $(seq 1 20); do pilot jobs | grep "^$JOB " | grep -qv running && break; sleep 1; done
line=$(pilot jobs | grep "^$JOB ")
say "$line"
case "$line" in *running*) fail "the node still lists $JOB running" ;; esac
exitfile=$(find "$DATA/jobs" -name "$JOB.log.exit" | head -1)
[ -n "$exitfile" ] && [ "$(tr -d '[:space:]' < "$exitfile")" = 7 ] || fail "the exit file does not say 7: ${exitfile:-none}"
case "$line" in *"exit 7"*|*"exited 7"*|*"code 7"*|*"(7)"*) ok "the node ends it with its real exit code" ;; *) fail "the node's line for $JOB does not carry exit code 7: $line" ;; esac
systemctl --user is-active "$UNIT.service" | grep -qx active || fail "the unit is not active at the end"
ok "systemctl --user is-active $UNIT: active"
exit 0
