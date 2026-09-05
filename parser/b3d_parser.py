#!/usr/bin/env python3
"""Minimal BAZIS B3D parser (current model only).

Validated against a BZ85 B3D sample. It extracts the large zlib model stream,
reads the string dictionary, parses the Document tree, and intentionally stops
before Undo/history data.

This is reverse-engineered for interoperability and is deliberately strict:
unknown value tags raise a descriptive error instead of silently guessing.
"""
from __future__ import annotations

import argparse
import json
import struct
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional


class B3DError(RuntimeError):
    pass


@dataclass
class Node:
    name: str
    value_type: int
    child_count: int
    value: Any = None
    children: list["Node"] | None = None
    offset: int = 0

    def child(self, name: str) -> Optional["Node"]:
        if not self.children:
            return None
        for node in self.children:
            if node.name == name:
                return node
        return None

    def children_named(self, name: str) -> list["Node"]:
        return [node for node in (self.children or []) if node.name == name]

    def to_plain(self) -> Any:
        if self.value_type == 0:
            # Preserve duplicate field names by converting to a list only when needed.
            out: dict[str, Any] = {}
            for child in self.children or []:
                value = child.to_plain()
                if child.name in out:
                    if not isinstance(out[child.name], list):
                        out[child.name] = [out[child.name]]
                    out[child.name].append(value)
                else:
                    out[child.name] = value
            return out

        if isinstance(self.value, (bytes, bytearray)):
            if self.name in ("Contour", "Trajectory"):
                try:
                    return decode_contour_blob(bytes(self.value))
                except Exception:
                    return {"$binary_hex": bytes(self.value).hex()}
            try:
                return self.value.decode("utf-8")
            except UnicodeDecodeError:
                return {"$binary_hex": bytes(self.value).hex()}

        return self.value


def decode_contour_blob(raw: bytes) -> dict[str, Any]:
    """Decode BAZIS 2D contour/trajectory binary payload.

    Confirmed segment tags in the supplied model:
      0x10: line, four float64 values (x1,y1,x2,y2)
      0x12: arc, six float64 values (three XY points) + one direction byte
    """
    if len(raw) < 4:
        raise B3DError("Contour payload too short")

    count = struct.unpack_from("<I", raw, 0)[0]
    pos = 4
    segments: list[dict[str, Any]] = []

    for _ in range(count):
        if pos >= len(raw):
            raise B3DError("Unexpected end of contour payload")

        segment_type = raw[pos]
        pos += 1

        if segment_type == 0x10:
            if pos + 32 > len(raw):
                raise B3DError("Truncated line segment")
            x1, y1, x2, y2 = struct.unpack_from("<dddd", raw, pos)
            pos += 32
            segments.append(
                {
                    "type": "line",
                    "x1": x1,
                    "y1": y1,
                    "x2": x2,
                    "y2": y2,
                }
            )

        elif segment_type == 0x12:
            if pos + 49 > len(raw):
                raise B3DError("Truncated arc segment")
            x1, y1, x2, y2, x3, y3 = struct.unpack_from("<dddddd", raw, pos)
            pos += 48
            direction = raw[pos]
            pos += 1
            segments.append(
                {
                    "type": "arc",
                    "x1": x1,
                    "y1": y1,
                    "x2": x2,
                    "y2": y2,
                    "x3": x3,
                    "y3": y3,
                    "direction": direction,
                }
            )

        else:
            raise B3DError(
                f"Unknown contour segment tag 0x{segment_type:02X} "
                f"at payload offset 0x{pos - 1:X}"
            )

    result: dict[str, Any] = {"segment_count": count, "segments": segments}
    if pos != len(raw):
        # Preserve trailing bytes explicitly: never silently discard format data.
        result["trailing_hex"] = raw[pos:].hex()
    return result


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def need(self, size: int) -> None:
        if self.pos + size > len(self.data):
            raise B3DError(f"Unexpected EOF at 0x{self.pos:X}, need {size} bytes")

    def u8(self) -> int:
        self.need(1)
        value = self.data[self.pos]
        self.pos += 1
        return value

    def u32(self) -> int:
        self.need(4)
        value = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return value

    def i32(self) -> int:
        self.need(4)
        value = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return value

    def f64(self) -> float:
        self.need(8)
        value = struct.unpack_from("<d", self.data, self.pos)[0]
        self.pos += 8
        return value

    def raw(self, size: int) -> bytes:
        self.need(size)
        value = self.data[self.pos : self.pos + size]
        self.pos += size
        return value


def find_model_stream(file_bytes: bytes) -> tuple[int, bytes]:
    """Return (offset, decompressed stream) for the largest valid zlib member."""
    candidates: list[tuple[int, bytes]] = []

    for offset in range(len(file_bytes) - 2):
        if file_bytes[offset] != 0x78 or file_bytes[offset + 1] not in (0x01, 0x5E, 0x9C, 0xDA):
            continue
        try:
            output = zlib.decompress(file_bytes[offset:])
        except zlib.error:
            continue
        if len(output) >= 1024:
            candidates.append((offset, output))

    if not candidates:
        raise B3DError("No usable zlib stream found in B3D")

    # The current-model/history stream is by far the largest in tested BZ85 files.
    return max(candidates, key=lambda item: len(item[1]))


def read_dictionary(reader: Reader) -> list[str]:
    count = reader.u32()
    if count > 10000:
        raise B3DError(f"Unreasonable dictionary size {count}")

    words: list[str] = []
    for _ in range(count):
        length = reader.u32()
        if length > 1_000_000:
            raise B3DError(f"Unreasonable dictionary string length {length}")
        words.append(reader.raw(length).decode("utf-8", errors="strict"))
    return words


def read_node(reader: Reader, words: list[str]) -> Node:
    start = reader.pos
    key = reader.u32()
    child_count = reader.u32()
    tag = reader.u8()
    name = words[key] if key < len(words) else f"#{key}"

    if tag == 0:
        children = [read_node(reader, words) for _ in range(child_count)]
        return Node(name, tag, child_count, children=children, offset=start)

    if child_count != 0:
        raise B3DError(
            f"Primitive node {name!r} has child_count={child_count} at 0x{start:X}"
        )

    # Reverse-engineered scalar tags used by the current Model branch.
    if tag == 1:
        value: Any = True
    elif tag == 2:
        value = False
    elif tag == 3:
        value = reader.u8()
    elif tag == 4:
        value = reader.i32()
    elif tag == 5:
        value = reader.f64()
    elif tag == 6:
        chars = reader.u32()
        value = reader.raw(chars * 2).decode("utf-16le", errors="replace")
    elif tag == 7:
        length = reader.u32()
        value = reader.raw(length)
    elif tag == 8:
        # Seen in Undo property variants in the sample, not in current Model.
        raise B3DError(
            f"Variant tag 8 encountered at 0x{start:X}; unsupported outside Model"
        )
    else:
        raise B3DError(f"Unknown value tag {tag} for {name!r} at 0x{start:X}")

    return Node(name, tag, child_count, value=value, offset=start)


def parse_current_model(path: str | Path) -> tuple[Node, dict[str, Any]]:
    source = Path(path)
    file_bytes = source.read_bytes()

    if file_bytes[:4] != b"BZ85":
        raise B3DError(f"Unsupported signature {file_bytes[:4]!r}; expected BZ85")

    zlib_offset, stream = find_model_stream(file_bytes)
    reader = Reader(stream)
    words = read_dictionary(reader)

    root_start = reader.pos
    root_key = struct.unpack_from("<I", stream, root_start)[0]
    if root_key >= len(words) or words[root_key] != "Document":
        raise B3DError(f"Expected Document at 0x{root_start:X}")

    # Parse Document manually and stop once Model is obtained. This deliberately
    # avoids parsing Undo/history, which uses additional variant layouts.
    reader.u32()  # Document dictionary key
    document_child_count = reader.u32()
    document_tag = reader.u8()
    if document_tag != 0:
        raise B3DError("Document is not a container")

    model: Optional[Node] = None
    for _ in range(document_child_count):
        child_start = reader.pos
        child_key = struct.unpack_from("<I", stream, child_start)[0]
        child_name = words[child_key] if child_key < len(words) else f"#{child_key}"
        if child_name == "Undo":
            break

        node = read_node(reader, words)
        if node.name == "Model":
            model = node
            break

    if model is None:
        raise B3DError("Model branch not found before Undo")

    meta = {
        "source": str(source),
        "signature": file_bytes[:4].decode("ascii", errors="replace"),
        "file_size": len(file_bytes),
        "zlib_offset": zlib_offset,
        "model_stream_size": len(stream),
        "dictionary_size": len(words),
        "dictionary_end": root_start,
    }
    return model, meta


def walk(node: Node):
    yield node
    for child in node.children or []:
        yield from walk(child)


def summarize_model(model: Node) -> dict[str, Any]:
    object_types: dict[str, int] = {}
    cuts = 0
    contours = 0
    objects: list[dict[str, Any]] = []

    for node in walk(model):
        if node.name == "Cuts":
            cuts += 1
        if node.name == "Contour":
            contours += 1

    for node in walk(model):
        if node.name != "Obj" or node.value_type != 0:
            continue

        children = {child.name: child for child in node.children or []}
        entry = {
            "ID": children.get("ID").value if children.get("ID") else None,
            "Type": children.get("Type").value if children.get("Type") else None,
            "Name": children.get("Name").value if children.get("Name") else None,
            "Thickness": (
                children.get("Thickness").value if children.get("Thickness") else None
            ),
        }

        if entry["Name"] is not None or entry["ID"] is not None:
            objects.append(entry)
            if entry["Type"] is not None:
                key = str(entry["Type"])
                object_types[key] = object_types.get(key, 0) + 1

    return {
        "object_records": len(objects),
        "object_types": object_types,
        "cut_containers": cuts,
        "contours": contours,
        "objects": objects,
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Extract current Model tree from a BAZIS BZ85 .b3d file"
    )
    parser.add_argument("input", help="Input .b3d file")
    parser.add_argument("-o", "--output", help="Output JSON path")
    parser.add_argument(
        "--summary", action="store_true", help="Print compact model summary"
    )
    args = parser.parse_args()

    model, meta = parse_current_model(args.input)
    payload = {"meta": meta, "Model": model.to_plain()}

    if args.output:
        Path(args.output).write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(args.output)

    if args.summary or not args.output:
        print(
            json.dumps(
                {"meta": meta, "summary": summarize_model(model)},
                ensure_ascii=False,
                indent=2,
            )
        )


if __name__ == "__main__":
    main()
