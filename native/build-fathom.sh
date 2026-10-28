#!/bin/sh
# Builds native/linux-x64/libfathom.so (Syzygy probing) with gcc or clang. Usage: sh native/build-fathom.sh
set -e
cd "$(dirname "$0")"
mkdir -p linux-x64
${CC:-cc} -O2 -std=c11 -fPIC -shared -I fathom -o linux-x64/libfathom.so dolphin_tb.c fathom/tbprobe.c
echo "Built linux-x64/libfathom.so"
