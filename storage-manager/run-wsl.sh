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
"$windows_dotnet" build "$(wslpath -w "$manager_dir/StorageManager.App/StorageManager.App.csproj")" --nologo

# Run the app as a child so Ctrl+C (or a terminal close / kill) can shut it down.
# The app is a Windows GUI process: it has no console of its own, so a plain Ctrl+C can
# leave it running in the tray and locking bin/ for the next build.
run_id="zipxp-dev-$$-$(date +%s)"
quit_file="$(mktemp "${TMPDIR:-/tmp}/zipxp-quit.XXXXXX")"
rm -f "$quit_file"
"$windows_dotnet" "$(wslpath -w "$manager_dir/StorageManager.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll")" \
    --dev-run-id "$run_id" --quit-file "$(wslpath -w "$quit_file")" "$@" &
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
