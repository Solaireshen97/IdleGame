"""Create a review sheet for character skill icons without changing assets."""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "assets" / "skill-art" / "manifest.json"
SOURCE = ROOT / "Game.Client" / "wwwroot" / "art" / "skills"
OUTPUT = ROOT / "assets" / "skill-art" / "preview.png"


def main() -> None:
    skills = json.loads(MANIFEST.read_text(encoding="utf-8"))
    columns, cell_width, cell_height = 6, 200, 215
    rows = (len(skills) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * cell_width, rows * cell_height), "#101e2d")
    draw = ImageDraw.Draw(sheet)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 16) if font_path.exists() else ImageFont.load_default()

    for index, skill in enumerate(skills):
        source = SOURCE / skill["file"]
        if not source.exists():
            raise FileNotFoundError(source)
        image = Image.open(source).convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty skill icon: {source}")
        image = image.crop(bounds)
        image.thumbnail((168, 160), Image.Resampling.NEAREST)
        left, top = (index % columns) * cell_width, (index // columns) * cell_height
        draw.rounded_rectangle((left + 5, top + 5, left + 195, top + 210), radius=12,
                               fill="#1b3042", outline="#5c7685", width=2)
        x, y = left + (cell_width - image.width) // 2, top + 10 + (160 - image.height)
        sheet.paste(image, (x, y), image)
        draw.text((left + 14, top + 177), skill["name"], font=font, fill="#f4d08b")

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
