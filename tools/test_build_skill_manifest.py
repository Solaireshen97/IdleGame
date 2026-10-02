"""Regression tests: python -m unittest discover -s tools -p test_build_skill_manifest.py."""

import copy
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

from build_skill_manifest import build_entries


def png_chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xffffffff)


class SkillManifestValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.art = Path(self.temp.name)
        self.config = {"Skills": {"Abilities": [{"Code": "sample-skill", "Name": "Sample", "ProfessionCode": "mage"}]}}
        self.subjects = [{"code": "sample-skill", "subject": "ice bolt", "palette": "blue"}]
        self.png = (b"\x89PNG\r\n\x1a\n"
                    + png_chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
                    + png_chunk(b"IDAT", zlib.compress(b"\x00\x00\x80\xff\xff"))
                    + png_chunk(b"IEND", b""))
        (self.art / "sample-skill.png").write_bytes(self.png)

    def test_valid_asset_and_unused_historical_subjects_are_preserved(self):
        self.subjects.extend([{"code": "old-skill"}, {"code": "old-skill"}])
        original = copy.deepcopy(self.subjects)
        entries = build_entries(self.config, self.subjects, self.art)
        self.assertEqual([entry["file"] for entry in entries], ["sample-skill.png"])
        self.assertEqual(self.subjects, original)

    def test_duplicate_catalog_codes_fail(self):
        self.config["Skills"]["Abilities"] *= 2
        with self.assertRaisesRegex(ValueError, "Duplicate current skill codes"):
            build_entries(self.config, self.subjects, self.art)

    def test_duplicate_current_subjects_fail(self):
        with self.assertRaisesRegex(ValueError, "Duplicate current skill subject"):
            build_entries(self.config, self.subjects * 2, self.art)

    def test_missing_subject_fails(self):
        with self.assertRaisesRegex(ValueError, "Missing skill subjects"):
            build_entries(self.config, [], self.art)

    def test_unsafe_or_non_png_paths_fail(self):
        for filename in ("../outside.png", "..\\outside.png", "C:\\outside.png", "/outside.png", "folder/icon.png", "icon.jpg"):
            with self.subTest(filename=filename):
                self.subjects[0]["file"] = filename
                with self.assertRaisesRegex(ValueError, "Invalid skill image filename"):
                    build_entries(self.config, self.subjects, self.art)

    def test_missing_png_fails(self):
        self.subjects[0]["file"] = "missing.png"
        with self.assertRaisesRegex(ValueError, "Missing skill image"):
            build_entries(self.config, self.subjects, self.art)

    def test_corrupt_and_truncated_png_fail(self):
        for content in (b"not a png", self.png[:30], self.png[:-12], self.png[:-1] + b"X"):
            with self.subTest(length=len(content)):
                (self.art / "sample-skill.png").write_bytes(content)
                with self.assertRaises(ValueError):
                    build_entries(self.config, self.subjects, self.art)

    def test_invalid_compressed_image_fails_even_with_correct_chunk_crc(self):
        content = (self.png[:33] + png_chunk(b"IDAT", b"invalid zlib stream") + png_chunk(b"IEND", b""))
        (self.art / "sample-skill.png").write_bytes(content)
        with self.assertRaisesRegex(ValueError, "Invalid PNG image data"):
            build_entries(self.config, self.subjects, self.art)


if __name__ == "__main__":
    unittest.main()
