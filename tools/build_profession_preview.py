"""Create a review sheet for every class sprite without changing the game assets."""

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Game.Client" / "wwwroot" / "art" / "professions"
OUTPUT = ROOT / "assets" / "profession-art" / "preview.png"
MANIFEST = ROOT / "assets" / "profession-art" / "manifest.json"


def main() -> None:
    classes = json.loads(MANIFEST.read_text(encoding="utf-8"))
    columns = 5
    rows = (len(classes) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * 220, rows * 310), "#101e2d")
    draw = ImageDraw.Draw(sheet)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 18) if font_path.exists() else ImageFont.load_default()
    for index, item in enumerate(classes):
        code = item["code"]
        label = f'{item["name"]} · {"基础" if item["base"] is None else "转职"}'
        left = (index % columns) * 220
        top = (index // columns) * 310
        draw.rounded_rectangle((left + 6, top + 6, left + 214, top + 302), radius=12,
                               fill="#1b3042", outline="#5c7685", width=2)
        image = Image.open(SOURCE / f"{code}.png").convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty class sprite: {code}")
        image = image.crop(bounds)
        image.thumbnail((194, 250), Image.Resampling.NEAREST)
        x = left + (220 - image.width) // 2
        y = top + 10 + (255 - image.height)
        sheet.paste(image, (x, y), image)
        draw.text((left + 18, top + 272), label, font=font, fill="#f4d08b")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
