#!/usr/bin/env bash
#
# Builds and installs CuteDB Browser on Linux or macOS.
#
# Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.
#
# Usage:
#   ./install.sh [--prefix DIR] [--self-contained] [--no-launcher]
#
#   --prefix DIR        Where to install. Default: ~/.local/share/cutebrowser
#   --self-contained    Bundle the .NET runtime, so the machine needs no .NET installed.
#   --no-launcher       Skip the `cutebrowser` command and the desktop entry.
#
# Requires the .NET 10 SDK. On Linux, Avalonia also wants the usual X11/Wayland libraries; the
# script names the ones it finds missing rather than failing at the first launch.

set -euo pipefail

PREFIX="${HOME}/.local/share/cutebrowser"
SELF_CONTAINED=false
LAUNCHER=true

while [[ $# -gt 0 ]]; do
    case "$1" in
        --prefix)         PREFIX="$2"; shift 2 ;;
        --self-contained) SELF_CONTAINED=true; shift ;;
        --no-launcher)    LAUNCHER=false; shift ;;
        -h|--help)        sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)                echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

step() { printf '\033[33m==> %s\033[0m\n' "$1"; }
fail() { printf '\033[31mError: %s\033[0m\n' "$1" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$(dirname "$SCRIPT_DIR")/CuteBrowser.csproj"

[[ -f "$PROJECT" ]] || fail "Cannot find CuteBrowser.csproj next to this script. Run it from the repository."

step "Checking for the .NET SDK"
command -v dotnet >/dev/null 2>&1 \
    || fail "The .NET 10 SDK is not on PATH. Install it from https://dotnet.microsoft.com/download"

dotnet --list-sdks | grep -q '^10\.' \
    || fail "CuteDB Browser targets net10.0, and no .NET 10 SDK is installed."

# Work out the runtime identifier, because a published app needs one and guessing wrong produces a
# binary that will not start on the machine that just built it.
case "$(uname -s)" in
    Darwin) OS=osx ;;
    Linux)  OS=linux ;;
    *)      fail "Unsupported system: $(uname -s). Use install.ps1 on Windows." ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  ARCH=x64 ;;
    arm64|aarch64) ARCH=arm64 ;;
    *)             fail "Unsupported architecture: $(uname -m)" ;;
esac

RID="${OS}-${ARCH}"
step "Publishing for ${RID} to ${PREFIX}"

mkdir -p "$PREFIX"

# A settings file that already holds someone's API keys must survive a reinstall.
CONFIG="${PREFIX}/CuteBrowser.dll.config"
PRESERVED=""
if [[ -f "$CONFIG" ]]; then
    PRESERVED="$(mktemp)"
    cp "$CONFIG" "$PRESERVED"
fi

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    -o "$PREFIX" \
    --self-contained "$SELF_CONTAINED"

if [[ -n "$PRESERVED" ]]; then
    cp "$PRESERVED" "$CONFIG"
    rm -f "$PRESERVED"
    step "Existing settings preserved"
fi

EXE="${PREFIX}/CuteBrowser"
[[ -x "$EXE" ]] || fail "Publish finished but ${EXE} is not there."

if [[ "$OS" == "linux" ]]; then
    step "Checking the libraries Avalonia needs"
    MISSING=()
    for lib in libX11.so.6 libSM.so.6 libice.so.6 libfontconfig.so.1; do
        ldconfig -p 2>/dev/null | grep -qi "$lib" || MISSING+=("$lib")
    done

    if [[ ${#MISSING[@]} -gt 0 ]]; then
        printf '\033[33m    Missing: %s\033[0m\n' "${MISSING[*]}"
        echo "    Debian/Ubuntu: sudo apt install libx11-6 libice6 libsm6 libfontconfig1"
        echo "    Fedora:        sudo dnf install libX11 libICE libSM fontconfig"
        echo "    Arch:          sudo pacman -S libx11 libice libsm fontconfig"
    fi
fi

if [[ "$LAUNCHER" == true ]]; then
    step "Installing the launcher"

    BIN="${HOME}/.local/bin"
    mkdir -p "$BIN"
    ln -sf "$EXE" "${BIN}/cutebrowser"
    echo "    ${BIN}/cutebrowser"

    case ":${PATH}:" in
        *":${BIN}:"*) ;;
        *) printf '\033[33m    %s is not on PATH. Add it to your shell profile.\033[0m\n' "$BIN" ;;
    esac

    if [[ "$OS" == "linux" ]]; then
        APPS="${HOME}/.local/share/applications"
        mkdir -p "$APPS"
        cat > "${APPS}/cutebrowser.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=CuteDB Browser
Comment=Browse, query and explain a CuteDB database
Exec=${EXE}
Path=${PREFIX}
Terminal=false
Categories=Development;Database;
DESKTOP
        echo "    ${APPS}/cutebrowser.desktop"
    fi
fi

echo
printf '\033[32mCuteDB Browser installed.\033[0m\n'
echo "  Run:      ${EXE}"
echo "  Settings: ${CONFIG}"
echo
echo "Jack, the assistant, needs an API key before he will answer. Set one in"
echo "Tools > Settings, or export an environment variable:"
echo "  OPENAI_API_KEY, AZURE_OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY,"
echo "  OPENAI_COMPATIBLE_API_KEY, TAVILY_API_KEY"
echo "Ollama needs no key at all and runs on your own machine."
