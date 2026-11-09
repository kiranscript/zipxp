#!/usr/bin/env bash
set -euo pipefail

if ! command -v wslpath >/dev/null 2>&1; then
    echo 'This launcher requires WSL with Windows executable interop enabled.' >&2
    exit 1
fi

gui_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
windows_dotnet="${WINDOWS_DOTNET_EXE:-}"
if [[ -z "$windows_dotnet" ]]; then
    windows_dotnet="$(command -v dotnet.exe || true)"
fi
if [[ -z "$windows_dotnet" ]]; then
    for candidate in '/mnt/c/Program Files/dotnet/dotnet.exe' '/mnt/c/Temp/zipxp-dotnet/dotnet.exe'; do
        if [[ -f "$candidate" ]]; then
            windows_dotnet="$candidate"
            break
        fi
    done
fi
if [[ -z "$windows_dotnet" || ! -f "$windows_dotnet" ]]; then
    echo 'Windows .NET SDK not found. Install the .NET 10 SDK on Windows, or set WINDOWS_DOTNET_EXE to its WSL path.' >&2
    exit 1
fi

"$windows_dotnet" build "$(wslpath -w "$gui_dir/StorageExplorer/StorageExplorer.csproj")" --nologo
# Launch through this exact Windows runtime, including SDKs outside Program Files.
exec "$windows_dotnet" "$(wslpath -w "$gui_dir/StorageExplorer/bin/Debug/net10.0-windows/StorageExplorer.dll")" "$@"
