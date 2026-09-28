#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
case "$(uname -s)" in Darwin) os=osx ;; Linux) os=linux ;; *) echo 'Build on macOS or Linux.' >&2; exit 1 ;; esac
case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) echo 'Unsupported architecture.' >&2; exit 1 ;; esac
rid="$os-$arch"
dotnet build KakaoRelay.Portable.slnx -c Release
dotnet run --project tests/KakaoRelay.Portable.Tests -c Release --no-build
dotnet publish src/KakaoRelay.Portable -c Release -r "$rid" --self-contained true -o "artifacts/portable/$rid"
printf '\nRun: ./artifacts/portable/%s/KakaoRelay.Portable\n' "$rid"
