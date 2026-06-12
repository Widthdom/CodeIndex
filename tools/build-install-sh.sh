#!/usr/bin/env bash
set -euo pipefail

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
repo_root="$(CDPATH= cd -- "${script_dir}/.." && pwd)"
output_path="${1:-${repo_root}/install.sh}"
tmp_path="${output_path}.tmp"

modules=(
  "00-core-and-verification.sh"
  "10-network-and-platform.sh"
  "20-installer.sh"
  "30-path-guidance.sh"
  "40-uninstall.sh"
  "50-self-test.sh"
  "60-reinstall.sh"
  "70-doctor.sh"
  "90-dispatch.sh"
)

: > "$tmp_path"
for module in "${modules[@]}"; do
  module_path="${repo_root}/install_modules/${module}"
  if [ ! -f "$module_path" ]; then
    echo "Missing install module: ${module_path}" >&2
    rm -f "$tmp_path"
    exit 1
  fi

  cat "$module_path" >> "$tmp_path"
done

chmod +x "$tmp_path"
mv "$tmp_path" "$output_path"
