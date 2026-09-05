#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
INSTALL_DIR="$HOME/.vt/bin"
ARCH="$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')"
sudo chown -R "$(id -u):$(id -g)" "$HOME/.vt"
install -d -m 0700 "$HOME/.vt/auth"
mkdir -p "$INSTALL_DIR"
            if [[ ! -w "$INSTALL_DIR" ]]; then
                if ! command -v sudo >/dev/null 2>&1; then
                    echo "Development vt install directory is not writable: $INSTALL_DIR" >&2
                    exit 1
                fi
                sudo chown -R "$(id -u):$(id -g)" "$HOME/.vt"
            fi

if [[ -f "$ROOT/src/Vt/Vt.csproj" ]] && command -v dotnet >/dev/null 2>&1; then
  PUBLISH_DIR="$ROOT/artifacts/publish/dev"
  dotnet publish "$ROOT/src/Vt/Vt.csproj" -c Release \
    -r "linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')" \
    --self-contained true -o "$PUBLISH_DIR"
  install -m 0755 "$PUBLISH_DIR/vt" "$INSTALL_DIR/vt.new"
            elif [[ -x "$ROOT/.vt/local/bootstrap/$ARCH/vt" ]] && "$ROOT/.vt/local/bootstrap/$ARCH/vt" --help >/dev/null 2>&1; then
                install -m 0755 "$ROOT/.vt/local/bootstrap/$ARCH/vt" "$INSTALL_DIR/vt.new"
            elif [[ -x "$ROOT/.vt/local/bootstrap/vt" ]] && "$ROOT/.vt/local/bootstrap/vt" --help >/dev/null 2>&1; then
                install -m 0755 "$ROOT/.vt/local/bootstrap/vt" "$INSTALL_DIR/vt.new"
            elif [[ -x /opt/vt-host-bin/vt ]] && /opt/vt-host-bin/vt --help >/dev/null 2>&1; then
                install -m 0755 /opt/vt-host-bin/vt "$INSTALL_DIR/vt.new"
else
  ASSET="vt-linux-$ARCH"
  TEMP_DIR="$(mktemp -d)"
  trap 'rm -rf "$TEMP_DIR"' EXIT
                GITHUB_AUTH_TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
                if [[ -z "$GITHUB_AUTH_TOKEN" ]] && command -v git >/dev/null 2>&1; then
                    GITHUB_AUTH_TOKEN="$(printf 'protocol=https\nhost=github.com\n\n' \
                        | GIT_TERMINAL_PROMPT=0 git credential fill 2>/dev/null \
                        | sed -n 's/^password=//p')"
                fi
                CURL_AUTH=()
                if [[ -n "$GITHUB_AUTH_TOKEN" ]]; then
                    CURL_AUTH=(-H "Authorization: Bearer $GITHUB_AUTH_TOKEN")
                fi
  curl --fail --location --retry 3 \
                    "${CURL_AUTH[@]}" \
                    "https://github.com/vharris-psp/vt/releases/latest/download/$ASSET" \
    --output "$TEMP_DIR/$ASSET"
  curl --fail --location --retry 3 \
                    "${CURL_AUTH[@]}" \
                    "https://github.com/vharris-psp/vt/releases/latest/download/$ASSET.sha256" \
    --output "$TEMP_DIR/$ASSET.sha256"
  (cd "$TEMP_DIR" && sha256sum --check "$ASSET.sha256")
  install -m 0755 "$TEMP_DIR/$ASSET" "$INSTALL_DIR/vt.new"
fi

mv -f "$INSTALL_DIR/vt.new" "$INSTALL_DIR/vt"
vt_path='export PATH="$HOME/.vt/bin:$PATH"'
grep -Fqx "$vt_path" "$HOME/.bashrc" || printf '\n%s\n' "$vt_path" >> "$HOME/.bashrc"
            if ! vt secret status 2>/dev/null | grep -q 'is configured'; then
                echo "Run 'vt secret login' before 'vt auth login' to configure 1Password SDK access."
            fi
echo "Development environment ready: $INSTALL_DIR/vt"
