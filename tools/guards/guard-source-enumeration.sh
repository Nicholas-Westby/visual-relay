#!/usr/bin/env bash
# Visual Relay — source-enumeration guard
#
# Detects a stale virtio-fs / readdir cache on the dev VM: when directory
# listings return empty (or a subset), MSBuild's default **/*.cs glob silently
# compiles only the files it can see — producing an empty or partial assembly
# with no hint at the real cause.  This guard compares the number of source
# files git tracks against the number visible on disk (excluding obj/ / bin/).
#
# Exit 0 when the view is intact; exit 2 when the visible count is drastically
# below the tracked count (0, or < ~50 %).
set -euo pipefail

# ---- configuration -----------------------------------------------------------
# Patterns that MSBuild SDK-style projects glob (implicit <Compile Include>).
# "*.cs" is the critical one; "*.axaml" is secondary.  Keep the patterns
# aligned with the check in Directory.Build.targets.
patterns=("*.cs" "*.axaml")

# Directories to scan for visible files (find roots).  Must match the repo's
# source/tool/test layout.  Silently walk from repo root; the -path exclusions
# trim obj/ / bin/ everywhere.
scan_dirs=("src" "tests" "tools")

# How low the visible count can go before we block the build (fraction of
# git-tracked count).  0.50 means < 50 % of tracked is fatal.
readonly MIN_RATIO=0.50

# ---- helpers ----------------------------------------------------------------
_err() {
  echo "guard-source-enumeration: $*" >&2
}

# ---- main -------------------------------------------------------------------

# Work from the repo root (where this script lives is tools/guards/).
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo_root"

# 1. Count git-tracked source files — this is ground truth.
tracked_total=0
visible_total=0
declare -a tracked_counts visible_counts
tracked_counts=()
visible_counts=()

for pat in "${patterns[@]}"; do
  tracked=$(git ls-files "${pat}" 2>/dev/null | wc -l | tr -d ' ')
  tracked_counts+=("$tracked")
  tracked_total=$((tracked_total + tracked))

  # Build a find expression: for each scan_dir, -name "$pat".
  # We collect all matching files and count them.
  visible=0
  for dir in "${scan_dirs[@]}"; do
    if [[ -d "$dir" ]]; then
      visible=$((visible + $(find "$dir" -name "$pat" \
        -not -path '*/bin/*' -not -path '*/obj/*' \
        2>/dev/null | wc -l | tr -d ' ')))
    fi
  done
  visible_counts+=("$visible")
  visible_total=$((visible_total + visible))
done

# 2. If no git-tracked sources at all, nothing to guard.
if (( tracked_total == 0 )); then
  exit 0
fi

# 3. Compare.
if (( visible_total == 0 )); then
  _err ""
  _err "┌──────────────────────────────────────────────────────────────────────────────┐"
  _err "│  STALE VIRTIO-FS / READDIR CACHE DETECTED                                   │"
  _err "│                                                                            │"
  _err "│  git tracks ${tracked_total} source file(s) across ${patterns[*]}, but 0 files are     │"
  _err "│  visible on disk (excluding obj/ / bin/).                                  │"
  _err "│                                                                            │"
  _err "│  MSBuild's default **/*.cs glob enumerates via readdir.  When the guest's  │"
  _err "│  directory cache is stale — a known virtio-fs bug on Tart VMs — readdir    │"
  _err "│  returns empty, so the project silently compiles ZERO sources into an      │"
  _err "│  empty assembly.  This causes cryptic CS0234 cascades downstream.          │"
  _err "│                                                                            │"
  _err "│  FIX: remount the shared filesystem.  From the host (macOS):               │"
  _err "│    • Run:  claude-vm/fix-cache.sh                                          │"
  _err "│    • Or:   sudo diskutil unmount <mount-path>                              │"
  _err "│             sudo mount -t virtiofs <tag> <mount-path>                      │"
  _err "│    • Or restart the VM entirely.                                           │"
  _err "│                                                                            │"
  _err "│  NOTE: rm -rf obj bin will NOT help — the filesystem cache is in the       │"
  _err "│  guest kernel, not on disk.  The files exist and read fine by name;        │"
  _err "│  only directory enumeration is broken.                                     │"
  _err "└──────────────────────────────────────────────────────────────────────────────┘"
  _err ""
  exit 2
fi

# Compute ratio with floating-point via awk.
ratio=$(awk -v v="$visible_total" -v t="$tracked_total" 'BEGIN { printf "%.3f", v / t }')
is_ok=$(awk -v r="$ratio" -v min="$MIN_RATIO" 'BEGIN { print (r >= min) ? 1 : 0 }')

if (( is_ok == 0 )); then
  pct=$(awk -v r="$ratio" 'BEGIN { printf "%.0f", r * 100 }')
  _err ""
  _err "┌──────────────────────────────────────────────────────────────────────────────┐"
  _err "│  STALE VIRTIO-FS / READDIR CACHE DETECTED                                   │"
  _err "│                                                                            │"
  _err "│  git tracks ${tracked_total} source file(s) across ${patterns[*]}, but only       │"
  _err "│  ${visible_total} are visible on disk (~${pct} % of tracked, below the ${MIN_RATIO/0./}% threshold).   │"
  _err "│                                                                            │"
  _err "│  MSBuild's default **/*.cs glob enumerates via readdir.  When the guest's  │"
  _err "│  directory cache is stale — a known virtio-fs bug on Tart VMs — readdir    │"
  _err "│  returns incomplete results, so the project silently compiles a SUBSET of  │"
  _err "│  its sources.  This causes cryptic CS0234 cascades downstream.             │"
  _err "│                                                                            │"
  _err "│  FIX: remount the shared filesystem.  From the host (macOS):               │"
  _err "│    • Run:  claude-vm/fix-cache.sh                                          │"
  _err "│    • Or:   sudo diskutil unmount <mount-path>                              │"
  _err "│             sudo mount -t virtiofs <tag> <mount-path>                      │"
  _err "│    • Or restart the VM entirely.                                           │"
  _err "│                                                                            │"
  _err "│  NOTE: rm -rf obj bin will NOT help — the filesystem cache is in the       │"
  _err "│  guest kernel, not on disk.  The files exist and read fine by name;        │"
  _err "│  only directory enumeration is broken.                                     │"
  _err "└──────────────────────────────────────────────────────────────────────────────┘"
  _err ""
  exit 2
fi

# All good.
exit 0
