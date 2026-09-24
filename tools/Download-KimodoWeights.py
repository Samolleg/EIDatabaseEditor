"""Download the editor's Kimodo SOMA RP + Q4_K text weights with upstream hashes.

Usage: python Download-KimodoWeights.py OUTPUT_DIRECTORY
"""

import hashlib
import json
import pathlib
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


SELECTED = (
    ("LocalAI-io/Kimodo-SOMA-RP-v1.1-GGML", "models/kimodo-soma-rp-v1.1-f32.gguf", "motion.gguf"),
    ("LocalAI-io/Llama-3-Kimodo-GGML", "Llama-3-Kimodo-Q4_K.gguf", "text/Llama-3-Kimodo-Q4_K.gguf"),
    ("LocalAI-io/Llama-3-Kimodo-GGML", "tokenizer.gguf", "text/tokenizer.gguf"),
)


def open_url(url, offset=0):
    headers = {"User-Agent": "DatabaseEditor-KimodoInstaller/1"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120)


def download(repo, source, target, expected):
    url = f"https://huggingface.co/{repo}/resolve/main/{urllib.parse.quote(source)}"
    part = target.with_name(target.name + ".part")
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.is_file() and verify(target, expected):
        print(f"verified existing {target}", flush=True)
        return
    for attempt in range(6):
        offset = part.stat().st_size if part.exists() else 0
        try:
            with open_url(url, offset) as response:
                append = offset > 0 and response.status == 206
                if offset and not append:
                    offset = 0
                with part.open("ab" if append else "wb") as output:
                    next_report = ((offset // (256 << 20)) + 1) * (256 << 20)
                    while True:
                        block = response.read(1 << 20)
                        if not block:
                            break
                        output.write(block)
                        offset += len(block)
                        if offset >= next_report:
                            print(f"{target.name}: {offset:,}/{expected['bytes']:,} bytes", flush=True)
                            next_report += 256 << 20
            if verify(part, expected):
                part.replace(target)
                print(f"verified {target}", flush=True)
                return
            if part.stat().st_size == expected["bytes"]:
                raise ValueError(f"SHA-256 mismatch for {target.name}")
        except (OSError, urllib.error.URLError) as error:
            print(f"retry {attempt + 1}: {error}", flush=True)
            time.sleep(min(3 * (attempt + 1), 15))
    raise RuntimeError(f"download/verification failed: {target}")


def verify(path, expected):
    if path.stat().st_size != expected["bytes"]:
        return False
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 << 20), b""):
            digest.update(block)
    return digest.hexdigest() == expected["sha256"]


def main():
    root = pathlib.Path(sys.argv[1]).resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifests = {}
    for repo in {entry[0] for entry in SELECTED}:
        base = f"https://huggingface.co/{repo}/resolve/main/"
        manifest = json.load(open_url(base + "MANIFEST.json"))
        manifests[repo] = {entry["path"]: entry for entry in manifest["files"]}
        (root / ("motion" if "SOMA" in repo else "text") / "MANIFEST.json").parent.mkdir(parents=True, exist_ok=True)
        for filename in ("MANIFEST.json", "NOTICE", "README.md"):
            location = root / ("motion" if "SOMA" in repo else "text") / filename
            location.write_bytes(open_url(base + filename).read())
        if "Llama" in repo:
            (root / "text" / "LICENSE-META-LLAMA-3.txt").write_bytes(open_url(base + "LICENSE-META-LLAMA-3.txt").read())
    for repo, source, destination in SELECTED:
        download(repo, source, root / destination, manifests[repo][source])


if __name__ == "__main__":
    main()
