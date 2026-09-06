#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARSER = ROOT / "parser"
if str(PARSER) not in sys.path:
    sys.path.insert(0, str(PARSER))

from b3d_parser import parse_current_model, walk  # noqa: E402

IMAGE_MAGIC = {
    "png": b"\x89PNG\r\n\x1a\n",
    "jpeg": b"\xff\xd8\xff",
    "gif87": b"GIF87a",
    "gif89": b"GIF89a",
    "bmp": b"BM",
}


def all_offsets(data: bytes, needle: bytes):
    start = 0
    while True:
        i = data.find(needle, start)
        if i < 0:
            return
        yield i
        start = i + 1


def zlib_members(data: bytes):
    out = []
    for i in range(len(data) - 2):
        if data[i] != 0x78 or data[i + 1] not in (0x01, 0x5E, 0x9C, 0xDA):
            continue
        try:
            d = zlib.decompressobj()
            payload = d.decompress(data[i:]) + d.flush()
            consumed = len(data[i:]) - len(d.unused_data)
        except zlib.error:
            continue
        if consumed > 2 and payload:
            out.append((i, consumed, len(payload), payload))
    # avoid duplicate starts that decode to tiny false-positive members
    uniq = {}
    for item in out:
        key = (item[0], item[1], item[2])
        uniq[key] = item
    return sorted(uniq.values(), key=lambda x: x[0])


def shannon_entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = [0] * 256
    for b in data:
        counts[b] += 1
    n = len(data)
    return -sum((c / n) * math.log2(c / n) for c in counts if c)


def binary_blob_inventory(path: Path):
    model, meta = parse_current_model(path)
    blobs = []
    names = {}
    for n in walk(model):
        names[n.name] = names.get(n.name, 0) + 1
        if n.value_type == 7 and isinstance(n.value, (bytes, bytearray)):
            raw = bytes(n.value)
            sigs = {k: list(all_offsets(raw, v)) for k, v in IMAGE_MAGIC.items()}
            blobs.append({
                "name": n.name,
                "offset": n.offset,
                "size": len(raw),
                "entropy": round(shannon_entropy(raw), 4),
                "image_signatures": {k: v for k, v in sigs.items() if v},
                "head_hex": raw[:32].hex(),
            })
    return meta, names, blobs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    args = ap.parse_args()
    p = Path(args.input)
    raw = p.read_bytes()

    print(f"file={p}")
    print(f"size={len(raw)}")
    print(f"head64={raw[:64].hex()}")
    print(f"entropy={shannon_entropy(raw):.4f}")

    print("\nRAW IMAGE SIGNATURES")
    for name, magic in IMAGE_MAGIC.items():
        offs = list(all_offsets(raw, magic))
        print(name, [hex(x) for x in offs])

    print("\nZLIB MEMBERS")
    members = zlib_members(raw)
    for off, consumed, decomp, payload in members:
        sigs = {k: [hex(x) for x in all_offsets(payload, v)] for k, v in IMAGE_MAGIC.items()}
        sigs = {k: v for k, v in sigs.items() if v}
        print(json.dumps({
            "offset": hex(off),
            "compressed_bytes": consumed,
            "decompressed_bytes": decomp,
            "entropy": round(shannon_entropy(payload), 4),
            "head32": payload[:32].hex(),
            "image_signatures": sigs,
        }, ensure_ascii=False))

    meta, names, blobs = binary_blob_inventory(p)
    print("\nMODEL META")
    print(json.dumps(meta, ensure_ascii=False, indent=2))
    print("\nMOST COMMON MODEL FIELD NAMES")
    for k, v in sorted(names.items(), key=lambda kv: (-kv[1], kv[0]))[:80]:
        print(f"{v:5d} {k}")
    print("\nMODEL BINARY BLOBS")
    print(f"count={len(blobs)} total={sum(x['size'] for x in blobs)}")
    for b in sorted(blobs, key=lambda x: -x["size"])[:100]:
        print(json.dumps(b, ensure_ascii=False))

    # Pre-model-stream region is especially interesting because BAZIS can save
    # a raster preview into the model for shell/catalog thumbnails.
    model_off = int(meta["zlib_offset"])
    pre = raw[:model_off]
    print("\nPRE-MODEL REGION")
    print(f"bytes={len(pre)} entropy={shannon_entropy(pre):.4f}")
    for name, magic in IMAGE_MAGIC.items():
        print(name, [hex(x) for x in all_offsets(pre, magic)])


if __name__ == "__main__":
    main()
