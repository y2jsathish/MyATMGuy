#!/usr/bin/env bash
# Publishes each layer test app as a self-contained single file. Usage: build/publish-testapps.sh [rid ...]
set -euo pipefail
cd "$(dirname "$0")/.."
rids=("$@"); [ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64)
for rid in "${rids[@]}"; do
  for layer in Core Application Infrastructure Presentation Monitoring; do
    dotnet publish "tools/ATM.TestApp.$layer" -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
      -o "artifacts/testapps/$rid"
  done
done

# Field test tool for real machines (see docs/ops/field-test.md)
for rid in "${rids[@]}"; do
  dotnet publish tools/ATM.FieldTest -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -o "artifacts/fieldtest/$rid"
done
