#!/usr/bin/env python3
"""Publishes a Gizlet Launcher for Windows release to R2, behind gizlet.io/assets.

    sops decrypt --output-type json devops/secrets/release.enc.yaml |
        python3 devops/scripts/release.py TAG COMMIT INSTALLERS OUTPUT [--assemble-only] [--unsigned]

INSTALLERS holds GizletSetup-<version>-<arch>.exe built by the Windows job. The Release
workflow passes --unsigned when that job explicitly reports an unsigned build. In order, this
script:

1. checks every installer's Authenticode signature and timestamp (osslsigncode), unless --unsigned
   was passed;
2. writes update.json, the manifest the Launcher reads: the version, the release notes from
   docs/releases/TAG.{en,zh-CN}.md and each installer's path, size and SHA-256, signed with the
   update key (ssh-keygen -Y sign, namespace gizlet-windows-update) whose public half is
   devops/update-key.pub and built into the Launcher;
3. refuses a version that is not newer than the one published;
4. uploads the installers to windows/releases/TAG/ (never overwriting) and downloads each again
   through gizlet.io/assets to check its SHA-256;
5. only then uploads windows/update.json with Cache-Control: no-cache, so every request
   revalidates it, and checks that gizlet.io/assets serves it.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION = r"(?:0|[1-9][0-9]{0,8})(?:\.(?:0|[1-9][0-9]{0,8})){2}"
NAMESPACE = "gizlet-windows-update"
DL = os.environ.get("GIZLET_DL_ORIGIN", "https://gizlet.io/assets")
ARCHES = ("x64", "arm64")


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def check_authenticode(path):
    """The installer's signature, certificate chain and timestamp verify."""
    bundle = "/etc/ssl/certs/ca-certificates.crt"
    result = subprocess.run(["osslsigncode", "verify", "-CAfile", bundle, "-TSA-CAfile", bundle, "-in", str(path)],
                            capture_output=True, text=True)
    output = result.stdout + result.stderr
    if result.returncode != 0 or "Signature verification: ok" not in output:
        raise ValueError(f"{path.name} has no valid Authenticode signature:\n{output}")
    if "Timestamp" not in output:
        raise ValueError(f"{path.name} has no timestamp")


def notes(tag):
    found = {}
    for language in ("en", "zh-CN"):
        path = ROOT / "docs/releases" / f"{tag}.{language}.md"
        if path.exists():
            found[language] = path.read_text(encoding="utf-8").strip()
    if "en" not in found:
        raise ValueError(f"docs/releases/{tag}.en.md is missing: every release needs notes")
    return found


def assemble(tag, commit, installers, output, signing_key, authenticode=True):
    version = tag[1:]
    entries = {}
    for arch in ARCHES:
        path = installers / f"GizletSetup-{version}-{arch}.exe"
        if not path.exists():
            continue
        if authenticode:
            check_authenticode(path)
        entries[arch] = {"path": path.name, "sha256": sha256(path), "size": path.stat().st_size}
    if "x64" not in entries:
        raise ValueError("No x64 installer")
    manifest = {
        "schema": 1,
        "name": "gizlet-windows",
        "version": version,
        "tag": tag,
        "commit": commit,
        "published_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "notes": notes(tag),
        "installers": entries,
    }
    text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    with tempfile.TemporaryDirectory() as key_dir:
        key = Path(key_dir) / "update"
        key.touch(mode=0o600)
        key.write_text(signing_key.rstrip("\n") + "\n")
        public = subprocess.check_output(["ssh-keygen", "-y", "-f", key], text=True).split()[:2]
        trusted = (ROOT / "devops/update-key.pub").read_text().split()[:2]
        if public != trusted:
            raise ValueError("The update signing key does not match devops/update-key.pub")
        message = Path(key_dir) / "manifest.json"
        message.write_bytes(text.encode("utf-8"))
        subprocess.run(["ssh-keygen", "-q", "-Y", "sign", "-f", key, "-n", NAMESPACE, message], check=True)
        signature = (Path(key_dir) / "manifest.json.sig").read_text()
        allowed = Path(key_dir) / "allowed_signers"
        allowed.write_text(f"gizlet-windows namespaces=\"{NAMESPACE}\" {' '.join(trusted)}\n")
        with message.open("rb") as stream:
            subprocess.run(["ssh-keygen", "-Y", "verify", "-f", allowed, "-I", "gizlet-windows", "-n", NAMESPACE,
                            "-s", Path(key_dir) / "manifest.json.sig"], stdin=stream, check=True, stdout=subprocess.DEVNULL)
    output.mkdir(parents=True, exist_ok=True)
    envelope = {"manifest": text, "signature": signature}
    (output / "update.json").write_text(json.dumps(envelope, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


def fetch(url, attempts=10):
    for attempt in range(attempts):
        request = urllib.request.Request(url, headers={"Cache-Control": "no-cache", "User-Agent": "gizlet-windows-release"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404 and attempts == 1:
                return None
            if attempt == attempts - 1:
                raise
        except OSError:
            if attempt == attempts - 1:
                raise
        time.sleep(6)


def publish(tag, manifest, installers, output, credentials):
    endpoint = credentials["R2_ENDPOINT"]
    bucket = credentials["R2_BUCKET"]
    if not re.fullmatch(r"https://[a-f0-9]{32}(?:\.(?:eu|fedramp))?\.r2\.cloudflarestorage\.com", endpoint):
        raise ValueError("Expected a Cloudflare R2 S3 endpoint")
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]", bucket):
        raise ValueError("Invalid R2 bucket name")
    environment = dict(
        os.environ,
        AWS_ACCESS_KEY_ID=credentials["R2_ACCESS_KEY_ID"],
        AWS_SECRET_ACCESS_KEY=credentials["R2_SECRET_ACCESS_KEY"],
        AWS_DEFAULT_REGION="auto",
        AWS_EC2_METADATA_DISABLED="true",
        AWS_REQUEST_CHECKSUM_CALCULATION="when_required",
    )
    for name in ("AWS_SESSION_TOKEN", "AWS_PROFILE", "SOPS_AGE_KEY"):
        environment.pop(name, None)
    s3 = ["aws", "--endpoint-url", endpoint, "--no-cli-pager", "s3api"]

    # Only ever forward: the published version must be older than this one.
    published = fetch(f"{DL}/windows/update.json", attempts=1)
    if published is not None:
        current = json.loads(json.loads(published)["manifest"])["version"]
        if version_key(current) >= version_key(manifest["version"]):
            raise ValueError(f"{current} is already published; {manifest['version']} is not newer")

    for entry in manifest["installers"].values():
        key = f"windows/releases/{tag}/{entry['path']}"
        existing = subprocess.run([*s3, "head-object", "--bucket", bucket, "--key", key], env=environment, capture_output=True, text=True)
        if existing.returncode == 0:
            raise ValueError(f"{key} already exists")
        if "(404)" not in existing.stderr and "(NotFound)" not in existing.stderr:
            raise RuntimeError("Could not check for an existing release in R2")
        subprocess.run([*s3, "put-object", "--bucket", bucket, "--key", key, "--body", str(installers / entry["path"]),
                        "--content-type", "application/vnd.microsoft.portable-executable",
                        "--cache-control", "public, max-age=31536000, immutable", "--if-none-match", "*"],
                       env=environment, stdout=subprocess.DEVNULL, check=True)
        print(f"Uploaded {key}", flush=True)
        served = hashlib.sha256(fetch(f"{DL}/{key}")).hexdigest()
        if served != entry["sha256"]:
            raise ValueError(f"{DL}/{key} serves {served}, not {entry['sha256']}")
        print(f"Checked {DL}/{key}", flush=True)

    body = (output / "update.json").read_bytes()
    subprocess.run([*s3, "put-object", "--bucket", bucket, "--key", "windows/update.json", "--body", str(output / "update.json"),
                    "--content-type", "application/json; charset=utf-8", "--cache-control", "no-cache"],
                   env=environment, stdout=subprocess.DEVNULL, check=True)
    for _ in range(10):
        if fetch(f"{DL}/windows/update.json") == body:
            print(f"Published {DL}/windows/update.json for {tag}", flush=True)
            return
        time.sleep(6)
    raise RuntimeError(f"{DL}/windows/update.json does not serve the new manifest")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("tag")
    parser.add_argument("commit")
    parser.add_argument("installers", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--assemble-only", action="store_true")
    parser.add_argument("--unsigned", action="store_true",
                        help="the installers are not Authenticode-signed (no code-signing certificate yet)")
    args = parser.parse_args()
    if not re.fullmatch(f"v{VERSION}", args.tag) or not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("expected vMAJOR.MINOR.PATCH and a full commit SHA")
    credentials = json.load(sys.stdin)
    manifest = assemble(args.tag, args.commit, args.installers, args.output, credentials["UPDATE_SIGNING_KEY"],
                        authenticode=not args.unsigned)
    if not args.assemble_only:
        publish(args.tag, manifest, args.installers, args.output, credentials)


if __name__ == "__main__":
    main()
