"""Create a review sheet for potion and herb icons without changing assets."""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "assets" / "item-art" / "manifest.json"
SOURCE = ROOT / "Game.Client" / "wwwroot" / "art" / "items"
OUTPUT = ROOT / "assets" / "item-art" / "preview.png"


def main() -> None:
    items = json.loads(MANIFEST.read_text(encoding="utf-8"))
    columns, cell_width, cell_height = 8, 170, 190
    rows = (len(items) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * cell_width, rows * cell_height), "#101e2d")
    draw = ImageDraw.Draw(sheet)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 15) if font_path.exists() else ImageFont.load_default()

    for index, item in enumerate(items):
        source = SOURCE / item["category"] / item["file"]
        if not source.exists():
            raise FileNotFoundError(source)
        image = Image.open(source).convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty icon: {source}")
        image = image.crop(bounds)
        image.thumbnail((138, 138), Image.Resampling.NEAREST)
        left, top = (index % columns) * cell_width, (index // columns) * cell_height
        draw.rounded_rectangle((left + 5, top + 5, left + 165, top + 185), radius=10,
                               fill="#1b3042", outline="#5c7685", width=2)
        x, y = left + (cell_width - image.width) // 2, top + 10 + (138 - image.height)
        sheet.paste(image, (x, y), image)
        draw.text((left + 12, top + 159), item["name"], font=font, fill="#f4d08b")

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
