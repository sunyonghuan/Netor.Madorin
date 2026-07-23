#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "FAIL reason=cross-user-probe-requires-root" >&2
  exit 2
fi

probe_user="${1:-nobody}"
dotnet_bin="${DOTNET_BIN:-/home/administrator/.dotnet/dotnet}"
dotnet_owner_home="${DOTNET_OWNER_HOME:-/home/administrator}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
probe_dll="${script_dir}/bin/Debug/net10.0/Madorin.AI.Runtime.UnixPeerCredentials.dll"
socket_path="/tmp/madorin-wrong-user-$$.sock"
server_pid=""
dotnet_owner_home_mode=""

cleanup() {
  if [[ -n "${server_pid}" ]] && kill -0 "${server_pid}" 2>/dev/null; then
    kill "${server_pid}"
    wait "${server_pid}" || true
  fi

  rm -f -- "${socket_path}"

  if [[ -n "${dotnet_owner_home_mode}" ]]; then
    chmod "${dotnet_owner_home_mode}" "${dotnet_owner_home}"
  fi
}
trap cleanup EXIT

if [[ ! -x "${dotnet_bin}" ]]; then
  echo "FAIL reason=dotnet-not-found path=${dotnet_bin}" >&2
  exit 2
fi

if [[ ! -f "${probe_dll}" ]]; then
  echo "FAIL reason=probe-not-built path=${probe_dll}" >&2
  exit 2
fi

if ! command -v runuser >/dev/null 2>&1; then
  echo "FAIL reason=runuser-not-found" >&2
  exit 2
fi

if ! id "${probe_user}" >/dev/null 2>&1; then
  echo "FAIL reason=probe-user-not-found user=${probe_user}" >&2
  exit 2
fi

if ! runuser -u "${probe_user}" -- test -x "${dotnet_bin}"; then
  dotnet_owner_home_mode="$(stat -c '%a' "${dotnet_owner_home}")"
  chmod o+x "${dotnet_owner_home}"
fi

if ! runuser -u "${probe_user}" -- test -x "${dotnet_bin}"; then
  echo "FAIL reason=dotnet-not-executable-by-probe-user path=${dotnet_bin}" >&2
  exit 2
fi

"${dotnet_bin}" "${probe_dll}" --transport-server "${socket_path}" &
server_pid=$!

for _ in $(seq 1 100); do
  if [[ -S "${socket_path}" ]]; then
    break
  fi

  sleep 0.05
done

if [[ ! -S "${socket_path}" ]]; then
  echo "FAIL reason=socket-not-created" >&2
  exit 1
fi

# The production endpoint starts at 0600. This deliberate relaxation lets the
# negative client reach the independent kernel-credential checks on both ends.
chmod 0666 "${socket_path}"
echo "INFO scenario=wrong-user-credential-check socketMode=666 probeUser=${probe_user}"

runuser -u "${probe_user}" -- \
  "${dotnet_bin}" "${probe_dll}" --transport-client "${socket_path}"
wait "${server_pid}"
server_pid=""
