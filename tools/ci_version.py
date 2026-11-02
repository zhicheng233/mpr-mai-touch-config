#!/usr/bin/env python3
"""Resolve a product version for CI and optionally inject the firmware base version.

CI builds use the closest reachable product tag plus the checked-out commit's
short SHA as SemVer build metadata (for example, 0.1.0+g1a2b3c4). Release builds
use the version from the pushed tag without build metadata.
"""

import argparse
import os
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FIRMWARE_HEADER = ROOT / "firmware" / "src" / "version.h"
TOOL_PROJECT = ROOT / "software" / "MaimaiTouchTool" / "MaimaiTouchTool.csproj"
VERSION_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def run_git(*args):
    return subprocess.check_output(
        ["git", *args], cwd=ROOT, text=True, stderr=subprocess.DEVNULL
    ).strip()


def numeric_version_from_header():
    text = FIRMWARE_HEADER.read_text(encoding="utf-8")
    fields = {}
    for name in ("Major", "Minor", "Patch"):
        match = re.search(r"constexpr\s+uint8_t\s+" + name + r"\s*=\s*(\d+)\s*;", text)
        if not match:
            raise RuntimeError(f"Could not find {name} in {FIRMWARE_HEADER}")
        fields[name] = int(match.group(1))
    return f"{fields['Major']}.{fields['Minor']}.{fields['Patch']}"


def tool_version_from_project():
    text = TOOL_PROJECT.read_text(encoding="utf-8")
    match = re.search(r"<Version>\s*([^<]+?)\s*</Version>", text)
    if not match:
        raise RuntimeError(f"Could not find <Version> in {TOOL_PROJECT}")
    return match.group(1)


def version_for(product, mode, tag):
    prefix = "firmware-v" if product == "firmware" else "tool-v"
    if mode == "release":
        if not tag or not tag.startswith(prefix):
            raise RuntimeError(f"Release tag must start with {prefix}")
        base = tag[len(prefix):]
    else:
        try:
            nearest = run_git("describe", "--tags", "--abbrev=0", "--match", prefix + "[0-9]*", "HEAD")
            base = nearest[len(prefix):]
        except subprocess.CalledProcessError:
            base = numeric_version_from_header() if product == "firmware" else tool_version_from_project()

    if not VERSION_RE.fullmatch(base):
        raise RuntimeError(f"Invalid {product} version: {base!r}; expected X.Y.Z")

    short_sha = run_git("rev-parse", "--short=7", "HEAD")
    version = base if mode == "release" else f"{base}+g{short_sha}"
    return base, version, short_sha


def set_firmware_header_version(base):
    major, minor, patch = (int(part) for part in base.split("."))
    if any(part > 255 for part in (major, minor, patch)):
        raise RuntimeError("Firmware version components must fit in uint8_t")
    with open(FIRMWARE_HEADER, "r", encoding="utf-8", newline="") as header:
        text = header.read()
    for name, value in (("Major", major), ("Minor", minor), ("Patch", patch)):
        pattern = r"(constexpr\s+uint8_t\s+" + name + r"\s*=\s*)\d+(\s*;)"
        text, count = re.subn(pattern, rf"\g<1>{value}\g<2>", text, count=1)
        if count != 1:
            raise RuntimeError(f"Could not update {name} in {FIRMWARE_HEADER}")
    with open(FIRMWARE_HEADER, "r", encoding="utf-8", newline="") as header:
        original = header.read()
    if text != original:
        with open(FIRMWARE_HEADER, "w", encoding="utf-8", newline="") as header:
            header.write(text)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("product", choices=("firmware", "tool"))
    parser.add_argument("--mode", choices=("ci", "release"), required=True)
    parser.add_argument("--tag", default=os.environ.get("GITHUB_REF_NAME"))
    parser.add_argument("--output", default=os.environ.get("GITHUB_OUTPUT"))
    args = parser.parse_args()

    base, version, short_sha = version_for(args.product, args.mode, args.tag)
    if args.product == "firmware":
        set_firmware_header_version(base)

    outputs = f"base_version={base}\nversion={version}\nshort_sha={short_sha}\n"
    if args.output:
        with open(args.output, "a", encoding="utf-8", newline="") as output:
            output.write(outputs)
    else:
        print(outputs, end="")
    print(f"{args.product} build version: {version}")


if __name__ == "__main__":
    main()
