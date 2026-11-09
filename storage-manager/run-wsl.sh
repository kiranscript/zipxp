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
exec "$windows_dotnet" "$(wslpath -w "$manager_dir/StorageManager.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll")" "$@"
