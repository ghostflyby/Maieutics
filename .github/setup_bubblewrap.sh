#!/usr/bin/env bash
set -euo pipefail

apt_options=(-o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15)

# APT otherwise reports successful updates after DNS failures and silently uses
# stale indexes. Hosted runners' default mirrors share Ubuntu's DNS infrastructure.
if ! sudo apt-get "${apt_options[@]}" --error-on=any update; then
  if [[ ! -f /etc/apt/apt-mirrors.txt ]]; then
    exit 1
  fi
  echo "::warning::Default APT sources failed; retrying with the kernel.org Ubuntu mirror first."
  # Keep the distribution sources and signing keys: only mirror selection changes.
  sudo sed -i '1ihttps://mirrors.edge.kernel.org/ubuntu/' /etc/apt/apt-mirrors.txt
  sudo apt-get "${apt_options[@]}" --error-on=any update
fi
sudo apt-get "${apt_options[@]}" -o Acquire::Retries=2 install --yes bubblewrap curl
bwrap --version

probe() {
  bwrap --unshare-user --unshare-pid --disable-userns --unshare-net \
    --ro-bind / / --proc /proc --dev /dev -- /usr/bin/true
}

if probe; then
  exit 0
fi

# Ubuntu's unprivileged-userns AppArmor mediation needs a profile for this binary.
# Grant userns to bwrap itself, rather than disabling the host-wide restriction.
if command -v apparmor_parser >/dev/null && [[ -r /sys/module/apparmor/parameters/enabled ]]; then
  sudo tee /etc/apparmor.d/maieutics-ci-bwrap >/dev/null <<'PROFILE'
profile bwrap /usr/bin/bwrap flags=(unconfined) {
  userns,
}
PROFILE
  sudo apparmor_parser -r /etc/apparmor.d/maieutics-ci-bwrap
fi

# An unavailable namespace implementation is a CI failure, never a skipped sandbox test.
probe
