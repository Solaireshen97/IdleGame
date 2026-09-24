"""Create a review sheet for the five class sprites without changing the game assets."""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Game.Client" / "wwwroot" / "art" / "professions"
OUTPUT = ROOT / "assets" / "profession-art" / "preview.png"
CLASSES = [
    ("swordsman", "剑士 · 基础"),
    ("acolyte", "祭司 · 基础"),
    ("knight", "骑士 · 转职"),
    ("warrior", "战士 · 转职"),
    ("priest", "牧师 · 转职"),
]


def main() -> None:
    sheet = Image.new("RGB", (1100, 310), "#101e2d")
    draw = ImageDraw.Draw(sheet)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = ImageFont.truetype(str(font_path), 18) if font_path.exists() else ImageFont.load_default()
    for index, (code, label) in enumerate(CLASSES):
        left = index * 220
        draw.rounded_rectangle((left + 6, 6, left + 214, 302), radius=12,
                               fill="#1b3042", outline="#5c7685", width=2)
        image = Image.open(SOURCE / f"{code}.png").convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError(f"Empty class sprite: {code}")
        image = image.crop(bounds)
        image.thumbnail((194, 250), Image.Resampling.NEAREST)
        x = left + (220 - image.width) // 2
        y = 10 + (255 - image.height)
        sheet.paste(image, (x, y), image)
        draw.text((left + 18, 272), label, font=font, fill="#f4d08b")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(OUTPUT, optimize=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
