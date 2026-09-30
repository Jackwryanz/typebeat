#!/usr/bin/env python3
"""
Checks that every package the bundled-beatmap manifest declares resolves ONLINE as a ranked set.

WHY. bundled-maps.sh proves the bytes in typebeat.Desktop/Bundled/ are the bytes the manifest
pins. It cannot prove those bytes are the map the leaderboards know. A difficulty is identified
online by the md5 of its .osu file (the client's GetBeatmapRequest sends it as `checksum` to
/api/v2/beatmaps/lookup), so a bundled package that is even one edit older than the site's copy
imports as a DIFFERENT map: no leaderboard, no ranked status, nothing submitted. That is silent
on a fresh install, which is the only place the bundle is ever imported. Run this whenever the
bundle changes, before committing the regenerated manifest.

WHAT IT CHECKS, per manifest entry:
  * the package opens and carries at least one .osu, each with BeatmapID and BeatmapSetID
  * all of a package's difficulties belong to one set, and no two packages share a set
  * the set is "ranked" on the site
  * every .osu md5 equals the site's checksum for that BeatmapID, and the site's set has no
    difficulty the package lacks (a stale package fails here)
  * with TYPEBEAT_BEARER set, additionally GET /api/v2/beatmaps/lookup?checksum=<md5> exactly as
    the client does, and require it to answer with that beatmap id and "ranked"

The anonymous /api/v2/beatmapsets/{id} route is the default because the lookup route is bearer
only; both read the same beatmaps.checksum_md5 column, so they agree on identity.

It also REPORTS (never fails on) which difficulties declare an intro beatdrop (the `beatdrop_ms`
field of the [Lyrics] header object, which the decoder reads into IBeatmap.IntroBeatdropTime). A
fresh install's intro picks only from sets with one, so a bundle with none starts silent.

USAGE
  python .github/scripts/bundled-maps-online.py [--bundled-dir DIR] [--manifest FILE] [--api BASE]

Stdlib only. Exit status 0 when everything resolves, 1 otherwise.
"""

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
DEFAULT_MANIFEST = os.path.join(REPO_ROOT, "typebeat.Desktop", "bundled-maps.manifest")
DEFAULT_BUNDLED = os.path.join(REPO_ROOT, "typebeat.Desktop", "Bundled")
DEFAULT_API = os.environ.get("BUNDLED_MAPS_API_BASE", "https://typebeat.sh")

MANIFEST_LINE = re.compile(r"^([0-9a-f]{64})\s+(\d+)\s+(.+?)\s*$")


def read_manifest(path):
    entries = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            m = MANIFEST_LINE.match(line)
            if not m:
                raise SystemExit(f"malformed manifest line: {line!r}")
            entries.append(m.group(3))
    return entries


def parse_osu(raw):
    """Returns (beatmap_id, set_id, version, beatdrop_ms) from one .osu file's bytes."""
    text = raw.decode("utf-8-sig", errors="replace")
    section = None
    meta = {}
    beatdrop = None
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("[") and s.endswith("]"):
            section = s[1:-1]
            continue
        if section == "Metadata" and ":" in s:
            k, v = s.split(":", 1)
            meta[k.strip()] = v.strip()
        elif section == "Lyrics" and s.startswith("{"):
            # Same rule as LyricBeatmapDecoder.parseLyricLine: the header is the object with no
            # "text" key, and beatdrop_ms counts only when it is a number.
            try:
                obj = json.loads(s)
            except ValueError:
                continue
            if isinstance(obj, dict) and "text" not in obj:
                v = obj.get("beatdrop_ms")
                if isinstance(v, (int, float)) and not isinstance(v, bool):
                    beatdrop = v
    def as_int(key):
        try:
            return int(meta.get(key, ""))
        except ValueError:
            return None
    return as_int("BeatmapID"), as_int("BeatmapSetID"), meta.get("Version", "?"), beatdrop


def get_json(url, bearer=None):
    req = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "bundled-maps-online"})
    if bearer:
        req.add_header("Authorization", f"Bearer {bearer}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--bundled-dir", default=DEFAULT_BUNDLED)
    ap.add_argument("--manifest", default=DEFAULT_MANIFEST)
    ap.add_argument("--api", default=DEFAULT_API)
    args = ap.parse_args()

    bearer = os.environ.get("TYPEBEAT_BEARER") or None
    api = args.api.rstrip("/")
    names = read_manifest(args.manifest)
    failures = []
    seen_sets = {}
    beatdrop_sets = []

    def fail(msg):
        failures.append(msg)
        print(f"  FAIL {msg}")

    for name in names:
        path = os.path.join(args.bundled_dir, name)
        print(f"{name}")
        if not os.path.isfile(path):
            fail(f"{name}: not found in {args.bundled_dir}")
            continue

        with zipfile.ZipFile(path) as z:
            diffs = []
            for info in z.infolist():
                if info.filename.lower().endswith(".osu"):
                    raw = z.read(info)
                    bid, sid, version, beatdrop = parse_osu(raw)
                    diffs.append((info.filename, hashlib.md5(raw).hexdigest(), bid, sid, version, beatdrop))

        if not diffs:
            fail(f"{name}: package carries no .osu")
            continue

        set_ids = {d[3] for d in diffs}
        if None in set_ids or any(d[2] is None for d in diffs):
            fail(f"{name}: a difficulty has no BeatmapID/BeatmapSetID, so it can never resolve online")
            continue
        if len(set_ids) != 1:
            fail(f"{name}: difficulties span several sets {sorted(set_ids)}")
            continue
        sid = set_ids.pop()
        if sid in seen_sets:
            fail(f"{name}: set {sid} is already bundled as '{seen_sets[sid]}'")
        seen_sets[sid] = name

        code, remote = get_json(f"{api}/api/v2/beatmapsets/{sid}")
        if code != 200 or not isinstance(remote, dict):
            fail(f"{name}: GET /api/v2/beatmapsets/{sid} answered {code}")
            continue
        status = remote.get("status")
        print(f"  set {sid}  {remote.get('artist')} - {remote.get('title')} ({remote.get('creator')})  status={status}")
        if status != "ranked":
            fail(f"{name}: set {sid} is '{status}', not ranked")

        online = {b.get("id"): b for b in remote.get("beatmaps") or []}
        local_ids = set()
        for fname, md5, bid, _, version, beatdrop in diffs:
            local_ids.add(bid)
            b = online.get(bid)
            want = b.get("checksum") if b else None
            verdict = "ok" if want == md5 else ("NOT ONLINE" if b is None else f"STALE (site has {want})")
            drop = f"beatdrop={beatdrop:g}ms" if beatdrop is not None else "beatdrop=none"
            print(f"  [{version}] id={bid} md5={md5} {verdict}  {drop}")
            if verdict != "ok":
                fail(f"{name} [{version}]: beatmap {bid} md5 {md5} {verdict}")

            if bearer:
                lcode, lres = get_json(f"{api}/api/v2/beatmaps/lookup?checksum={md5}", bearer)
                lok = lcode == 200 and isinstance(lres, dict) and lres.get("id") == bid and lres.get("status") == "ranked"
                print(f"    lookup?checksum -> {lcode} {'ok' if lok else lres}")
                if not lok:
                    fail(f"{name} [{version}]: beatmaps/lookup did not resolve md5 {md5} to ranked beatmap {bid}")

        for missing in sorted(set(online) - local_ids):
            fail(f"{name}: site set {sid} has difficulty {missing} ('{online[missing].get('version')}') the package lacks")

        if any(d[5] is not None for d in diffs):
            beatdrop_sets.append(name)

    print()
    print(f"{len(names)} packages, {len(beatdrop_sets)} declare an intro beatdrop:")
    for n in beatdrop_sets:
        print(f"  {n}")
    if not beatdrop_sets:
        print("  NONE: a fresh install from this bundle has no intro candidate and starts silent.")
    if not bearer:
        print("(TYPEBEAT_BEARER unset: checked via the anonymous beatmapsets route, not beatmaps/lookup)")

    if failures:
        print(f"\n{len(failures)} failure(s).")
        return 1
    print("\nall bundled difficulties resolve online as ranked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
