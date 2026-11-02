#!/usr/bin/env bash
set -euo pipefail

sudo apt-get update
sudo apt-get install --yes bubblewrap curl
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
