#!/usr/bin/env bash
# Kept for compatibility. The real build lives in packaging/build-linux.sh.
exec "$(dirname "$0")/packaging/build-linux.sh" "$@"
