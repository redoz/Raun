"""Compute the next release tag from the existing v* tags, npm-style.

    git tag -l 'v*' | python3 .github/scripts/next_version.py <bump> [preid]

bump:
  prerelease  next pre-release of the current line: 0.3.0-beta.33 -> 0.3.0-beta.34
              (after a stable 0.3.0 it starts the next patch: 0.3.1-beta.1)
  patch       stable patch: 0.3.0-beta.33 -> 0.3.0 (finalizes), 0.3.0 -> 0.3.1
  minor       stable minor: 0.3.0-beta.33 -> 0.3.0 when it is already a minor line, else 0.4.0
  major       stable major: 1.0.0-beta.2 -> 1.0.0, 0.3.0 -> 1.0.0
  prepatch    first pre-release of the next patch: 0.3.0-beta.33 -> 0.3.1-beta.1
  preminor    first pre-release of the next minor: -> 0.4.0-beta.1
  premajor    first pre-release of the next major: -> 1.0.0-beta.1
preid: the pre-release label for a new line (default: the latest tag's label, else "beta").

Prints the tag, e.g. v0.3.0-beta.34. Exits non-zero on bad input.
"""
import re
import sys

TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")
BUMPS = ("prerelease", "patch", "minor", "major", "prepatch", "preminor", "premajor")


def parse(tag):
    m = TAG.match(tag.strip())
    if not m:
        return None
    major, minor, patch, pre = m.groups()
    return (int(major), int(minor), int(patch)), (pre.split(".") if pre else [])


def sort_key(version):
    core, pre = version
    # A stable version sorts above every pre-release of the same core. Numeric identifiers sort
    # numerically and below alphanumeric ones (SemVer 2.0.0, section 11).
    ids = tuple((0, int(p), "") if p.isdigit() else (1, 0, p) for p in pre)
    return core, (1,) if not pre else (0,), ids


def label_and_number(pre):
    """Split ['beta', '33'] into ('beta', 33); a label without a number counts as 0."""
    if pre and pre[-1].isdigit():
        return ".".join(pre[:-1]) or "beta", int(pre[-1])
    return ".".join(pre) or "beta", 0


def next_version(tags, bump, preid=None):
    if bump not in BUMPS:
        raise ValueError(f"unknown bump '{bump}'; expected one of {', '.join(BUMPS)}")
    versions = [v for v in (parse(t) for t in tags) if v]
    (major, minor, patch), pre = max(versions, key=sort_key) if versions else ((0, 0, 0), [])
    label = preid or (label_and_number(pre)[0] if pre else "beta")
    if not re.fullmatch(r"[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*", label):
        raise ValueError(f"invalid pre-release label '{label}'")

    if bump == "prerelease":
        if pre and (preid is None or label_and_number(pre)[0] == preid):
            current, number = label_and_number(pre)
            return f"v{major}.{minor}.{patch}-{current}.{number + 1}"
        if pre:  # same core, new label: 0.3.0-beta.4 -> 0.3.0-rc.1
            return f"v{major}.{minor}.{patch}-{label}.1"
        return f"v{major}.{minor}.{patch + 1}-{label}.1"
    if bump == "patch":
        return f"v{major}.{minor}.{patch}" if pre else f"v{major}.{minor}.{patch + 1}"
    if bump == "minor":
        return f"v{major}.{minor}.0" if pre and patch == 0 else f"v{major}.{minor + 1}.0"
    if bump == "major":
        return f"v{major}.0.0" if pre and minor == 0 and patch == 0 else f"v{major + 1}.0.0"
    if bump == "prepatch":
        return f"v{major}.{minor}.{patch + 1}-{label}.1"
    if bump == "preminor":
        return f"v{major}.{minor + 1}.0-{label}.1"
    return f"v{major + 1}.0.0-{label}.1"


def main(argv):
    if len(argv) not in (2, 3):
        print(__doc__, file=sys.stderr)
        return 2
    try:
        print(next_version(sys.stdin.read().split(), argv[1], argv[2] if len(argv) == 3 and argv[2] else None))
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
