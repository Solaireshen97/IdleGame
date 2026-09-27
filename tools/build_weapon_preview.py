"""Create a review sheet of individually generated weapons; preserve game assets."""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "Game.Client" / "wwwroot" / "art" / "weapons"
OUTPUT = ROOT / "assets" / "weapon-art" / "added-weapons-preview.png"


def main() -> None:
    entries = json.loads((ROOT / "assets" / "weapon-art" / "manifest.json").read_text(encoding="utf-8"))
    entries = [item for item in entries if item.get("source") == "individual"]
    columns, width, height = 6, 180, 210
    rows = (len(entries) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * width, rows * height), "#102130")
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 14)
    small = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 11)
    for index, item in enumerate(entries):
        left, top = index % columns * width, index // columns * height
        draw.rounded_rectangle((left + 5, top + 5, left + width - 5, top + height - 5),
                               radius=12, fill="#1d3445", outline="#526b78", width=2)
        with Image.open(ART / item["file"]) as source:
            sprite = source.convert("RGBA")
        bounds = sprite.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty weapon image: {item['code']}")
        sprite = sprite.crop(bounds)
        sprite.thumbnail((140, 140), Image.Resampling.NEAREST)
        sheet.paste(sprite, (left + (width - sprite.width) // 2, top + 14 + (140 - sprite.height) // 2), sprite)
        draw.text((left + 12, top + 162), item["name"], font=font, fill="#f0cd86")
        draw.text((left + 12, top + 184), item["group"], font=small, fill="#b4c6cf")
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
