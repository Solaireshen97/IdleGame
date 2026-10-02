"""Validate skill art subjects and generate the client skill-art lookup."""

import argparse
import json
import re
import struct
import zlib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Game.Server" / "appsettings.json"
SUBJECTS = ROOT / "assets" / "skill-art" / "subjects.json"
MANIFEST = ROOT / "assets" / "skill-art" / "manifest.json"
SERVICE = ROOT / "Game.Client" / "Services" / "SkillArt.cs"
ART = ROOT / "Game.Client" / "wwwroot" / "art" / "skills"


def validate_png(file: Path) -> None:
    """Check the PNG container and compressed image stream without image libraries."""
    data = file.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError(f"Invalid PNG signature: {file.name}")
    offset = 8
    chunks = []
    image_data = bytearray()
    while offset < len(data):
        if offset + 12 > len(data):
            raise ValueError(f"Truncated PNG chunk: {file.name}")
        length = struct.unpack_from(">I", data, offset)[0]
        end = offset + 12 + length
        if end > len(data):
            raise ValueError(f"Truncated PNG payload: {file.name}")
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:end - 4]
        crc = struct.unpack_from(">I", data, end - 4)[0]
        if zlib.crc32(kind + payload) & 0xffffffff != crc:
            raise ValueError(f"Invalid PNG checksum: {file.name}")
        if not chunks:
            if kind != b"IHDR" or length != 13:
                raise ValueError(f"Missing PNG header: {file.name}")
            width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", payload)
            depths = {0: (1, 2, 4, 8, 16), 2: (8, 16), 3: (1, 2, 4, 8), 4: (8, 16), 6: (8, 16)}
            if not width or not height or depth not in depths.get(color, ()) or compression or filtering or interlace not in (0, 1):
                raise ValueError(f"Invalid PNG header values: {file.name}")
        elif kind == b"IHDR":
            raise ValueError(f"Repeated PNG header: {file.name}")
        chunks.append(kind)
        if kind == b"IDAT":
            image_data.extend(payload)
        offset = end
        if kind == b"IEND":
            if length or offset != len(data):
                raise ValueError(f"Invalid PNG ending: {file.name}")
            break
    if not chunks or chunks[-1] != b"IEND" or not image_data:
        raise ValueError(f"Incomplete PNG image: {file.name}")
    try:
        if not zlib.decompress(image_data):
            raise ValueError(f"Empty PNG image data: {file.name}")
    except zlib.error as error:
        raise ValueError(f"Invalid PNG image data: {file.name}") from error


def build_entries(config: dict, subjects: list, art_directory: Path) -> list:
    abilities = config["Skills"]["Abilities"]
    codes = [item["Code"] for item in abilities]
    if len(set(codes)) != len(codes):
        raise ValueError("Duplicate current skill codes in catalog")
    if any(not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", code) for code in codes):
        raise ValueError("Invalid current skill code")
    current_codes = set(codes)
    planned = {}
    for item in subjects:
        code = item["code"]
        if code not in current_codes:
            continue  # Historical subjects are preserved, but do not produce mappings.
        if code in planned:
            raise ValueError(f"Duplicate current skill subject: {code}")
        planned[code] = item
    missing = current_codes - planned.keys()
    if missing:
        raise ValueError(f"Missing skill subjects: {sorted(missing)}")
    entries = []
    checked_files = set()
    art_directory = art_directory.resolve()
    for ability in abilities:
        item = planned[ability["Code"]]
        filename = item.get("file", f"{item['code']}.png")
        if not isinstance(filename, str) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]*\.png", filename):
            raise ValueError(f"Invalid skill image filename for {ability['Code']}: {filename!r}")
        file = (art_directory / filename).resolve()
        if file.parent != art_directory:
            raise ValueError(f"Skill image escapes art directory: {filename}")
        if not file.is_file():
            raise ValueError(f"Missing skill image for {ability['Code']}: {filename}")
        if file not in checked_files:
            validate_png(file)
            checked_files.add(file)
        entries.append({
            "code": ability["Code"], "name": ability["Name"],
            "profession": ability["ProfessionCode"], "file": filename,
            "subject": item["subject"], "palette": item["palette"],
        })
    return entries


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Validate source catalog and PNG assets without writing generated files")
    args = parser.parse_args()
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    subjects = json.loads(SUBJECTS.read_text(encoding="utf-8"))
    entries = build_entries(config, subjects, ART)
    if args.check:
        print(f"Validated {len(entries)} character skills and {len({entry['file'] for entry in entries})} unique PNG assets")
        return

    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(entries, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = [
        "// Generated by tools/build_skill_manifest.py from the skill catalog.",
        "namespace Game.Client.Services;",
        "",
        "public static class SkillArt",
        "{",
        "    private static readonly Dictionary<string, string> PathsByCode = new(StringComparer.Ordinal)",
        "    {",
    ]
    lines.extend(f'        ["{item["code"]}"] = "/art/skills/{item["file"]}",' for item in entries)
    lines.extend([
        "    };",
        "",
        "    public static string? ForCode(string? code) => code is not null && PathsByCode.TryGetValue(code, out var path)",
        "        ? path : null;",
        "}",
    ])
    SERVICE.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Mapped {len(entries)} character skills")


if __name__ == "__main__":
    main()
