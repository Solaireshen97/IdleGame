"""Create a review sheet of monster artwork without changing the game assets."""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "assets" / "monster-art" / "manifest.json"
SOURCE = ROOT / "Game.Client" / "wwwroot" / "art" / "monsters"
OUTPUT = ROOT / "assets" / "monster-art" / "preview.png"


def main() -> None:
    monsters = json.loads(MANIFEST.read_text(encoding="utf-8"))
    columns = 8
    cell_width, cell_height = 170, 194
    rows = (len(monsters) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * cell_width, rows * cell_height), "#101e2d")
    draw = ImageDraw.Draw(sheet)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 15) if font_path.exists() else ImageFont.load_default()

    for index, monster in enumerate(monsters):
        source = SOURCE / monster["file"]
        if not source.exists():
            raise FileNotFoundError(source)
        image = Image.open(source).convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty monster sprite: {source}")
        image = image.crop(bounds)
        image.thumbnail((148, 145), Image.Resampling.NEAREST)
        left = (index % columns) * cell_width
        top = (index // columns) * cell_height
        draw.rounded_rectangle((left + 5, top + 5, left + 165, top + 189), radius=10,
                               fill="#1b3042", outline="#5c7685", width=2)
        x = left + (cell_width - image.width) // 2
        y = top + 12 + (145 - image.height)
        sheet.paste(image, (x, y), image)
        draw.text((left + 12, top + 161), f"{index + 1:02d} {monster['name']}", font=font, fill="#f4d08b")

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
