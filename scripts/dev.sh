#!/usr/bin/env bash
set -euo pipefail
command -v wslpath >/dev/null 2>&1 || { echo 'Use this launcher from WSL with Windows executable interop enabled.' >&2; exit 1; }
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
windows_dotnet="${WINDOWS_DOTNET_EXE:-$(command -v dotnet.exe || true)}"
if [[ -z "$windows_dotnet" ]]; then
    for candidate in '/mnt/c/Program Files/dotnet/dotnet.exe' '/mnt/c/Temp/zipxp-dotnet/dotnet.exe'; do
        if [[ -f "$candidate" ]]; then windows_dotnet="$candidate"; break; fi
    done
fi
if [[ ! -f "$windows_dotnet" ]]; then
    echo 'Install the .NET 10 SDK on Windows or set WINDOWS_DOTNET_EXE to its WSL path.' >&2
    exit 1
fi
# Launcher options (everything else is passed on to the app):
#   --rebuild   delete bin/ and obj/ first, so nothing stale is reused
#   --fresh     start with empty data in a throwaway folder; your real data is untouched
#   --reset     erase your real saved data (asks first), then start empty

usage() { sed -n '/^# Launcher options/,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
rebuild=0 fresh=0 reset=0 app_args=()
for arg in "$@"; do
    case "$arg" in
        --rebuild) rebuild=1 ;;
        --fresh) fresh=1 ;;
        --reset) reset=1 ;;
        -h|--help) usage; exit 0 ;;
        *) app_args+=("$arg") ;;
    esac
done
if (( fresh && reset )); then echo 'Use either --fresh or --reset, not both.' >&2; exit 2; fi

# Noninteractive shells may not load NVM, and Windows npm can appear on WSL's PATH.
# Find a supported Linux Node and always pair it with npm from the same installation.
node_exe=""
node_candidates=()
if [[ -n "${ZIPXP_NODE_EXE:-}" ]]; then
    node_candidates+=("$ZIPXP_NODE_EXE")
else
    node_candidates+=("$(command -v node || true)")
    while IFS= read -r candidate; do node_candidates+=("$candidate"); done < <(
        printf '%s\n' "${NVM_DIR:-$HOME/.nvm}"/versions/node/*/bin/node | sort -Vr
    )
fi
for candidate in "${node_candidates[@]}"; do
    [[ -x "$candidate" ]] || continue
    if resolved_node="$("$candidate" -e 'const [major, minor] = process.versions.node.split(".").map(Number); if (process.platform !== "linux" || major < 22 || (major === 22 && minor < 12)) process.exit(1); process.stdout.write(process.execPath);' 2>/dev/null)"; then
        if [[ -f "$(dirname -- "$resolved_node")/npm" ]]; then node_exe="$resolved_node"; break; fi
    fi
done
if [[ -z "$node_exe" ]]; then
    echo 'Could not find Linux Node.js 22.12+ with npm. Install it in WSL, or set ZIPXP_NODE_EXE to its node executable.' >&2
    exit 1
fi
node_bin="$(dirname -- "$node_exe")"
export PATH="$node_bin:$PATH"
echo "Building React with Node $("$node_exe" --version) ($node_exe)"
(cd "$repo_root/src/ZipXP.UI" && "$node_exe" "$node_bin/npm" ci --no-fund --no-audit && "$node_exe" "$node_bin/npm" run build)

win_path() { powershell.exe -NoProfile -Command "$1" </dev/null | tr -d '\r'; }

if (( rebuild )); then
    echo 'Cleaning bin/ and obj/ ...'
    find "$repo_root/src" "$repo_root/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
fi

if (( fresh )); then
    data_dir="$(win_path '[IO.Path]::Combine([IO.Path]::GetTempPath(), "ZipXP-dev-fresh")')"
    rm -rf "$(wslpath -u "$data_dir")"
    echo "Fresh start: using empty data in $data_dir (your real data is untouched)."
    app_args+=(--data-dir "$data_dir")
elif (( reset )); then
    data_dir="$(win_path '[IO.Path]::Combine($env:LOCALAPPDATA, "ZipXP", "StorageManager")')"
    echo "This permanently erases ZipXP's saved folders, settings and history in:"
    echo "  $data_dir"
    echo "Files already compressed stay compressed (use Restore original size first if you want them back)."
    read -r -p 'Type "reset" to continue: ' answer
    [[ "$answer" == reset ]] || { echo 'Cancelled.'; exit 1; }
    if ! rm -rf "$(wslpath -u "$data_dir")"; then echo 'Could not erase it. Quit ZipXP first (tray icon > Quit ZipXP).' >&2; exit 1; fi
    echo 'Saved data erased.'
fi

"$windows_dotnet" build "$(wslpath -w "$repo_root/src/ZipXP.App/ZipXP.App.csproj")" -p:BuildReact=false --nologo

# Run the app as a child so Ctrl+C (or a terminal close / kill) can shut it down.
# The app is a Windows GUI process: it has no console of its own, so a plain Ctrl+C can
# leave it running in the tray and locking bin/ for the next build.
run_id="zipxp-dev-$$-$(date +%s)"
quit_file="$(mktemp "${TMPDIR:-/tmp}/zipxp-quit.XXXXXX")"
rm -f "$quit_file"
"$windows_dotnet" "$(wslpath -w "$repo_root/src/ZipXP.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll")" \
    --dev-run-id "$run_id" --quit-file "$(wslpath -w "$quit_file")" "${app_args[@]}" &
app_pid=$!

stop_app() {
    trap - INT TERM HUP
    echo; echo 'Stopping ZipXP...'
    : > "$quit_file"                                  # ask the app to quit cleanly
    for _ in $(seq 1 100); do                         # up to ~10 s
        kill -0 "$app_pid" 2>/dev/null || break
        sleep 0.1
    done
    if kill -0 "$app_pid" 2>/dev/null; then           # still there: force it
        echo 'ZipXP did not stop in time, ending it.'
        powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*$run_id*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }" >/dev/null 2>&1 || true
        kill "$app_pid" 2>/dev/null || true
    fi
    rm -f "$quit_file"
    exit 130
}
trap stop_app INT TERM HUP

wait "$app_pid" && status=0 || status=$?
rm -f "$quit_file"
exit "$status"
