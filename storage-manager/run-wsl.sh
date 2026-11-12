#!/usr/bin/env bash
set -euo pipefail
command -v wslpath >/dev/null 2>&1 || { echo 'Use this launcher from WSL with Windows executable interop enabled.' >&2; exit 1; }
manager_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
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

win_path() { powershell.exe -NoProfile -Command "$1" </dev/null | tr -d '\r'; }

if (( rebuild )); then
    echo 'Cleaning bin/ and obj/ ...'
    find "$manager_dir" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
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

"$windows_dotnet" build "$(wslpath -w "$manager_dir/StorageManager.App/StorageManager.App.csproj")" --nologo

# Run the app as a child so Ctrl+C (or a terminal close / kill) can shut it down.
# The app is a Windows GUI process: it has no console of its own, so a plain Ctrl+C can
# leave it running in the tray and locking bin/ for the next build.
run_id="zipxp-dev-$$-$(date +%s)"
quit_file="$(mktemp "${TMPDIR:-/tmp}/zipxp-quit.XXXXXX")"
rm -f "$quit_file"
"$windows_dotnet" "$(wslpath -w "$manager_dir/StorageManager.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll")" \
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
