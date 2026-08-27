from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ICO_PATH = ROOT / "src" / "CodexUsageBall" / "Assets" / "AppIcon.ico"
PNG_PATH = ROOT / "docs" / "images" / "app-icon.png"
SIZE = 1024


def ellipse_layer(box: tuple[int, int, int, int], fill: tuple[int, int, int, int], blur: int = 0) -> Image.Image:
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ImageDraw.Draw(layer).ellipse(box, fill=fill)
    return layer.filter(ImageFilter.GaussianBlur(blur)) if blur else layer


def arc_endpoint(box: tuple[int, int, int, int], angle_degrees: float) -> tuple[float, float]:
    left, top, right, bottom = box
    angle = math.radians(angle_degrees)
    center_x = (left + right) / 2
    center_y = (top + bottom) / 2
    radius_x = (right - left) / 2
    radius_y = (bottom - top) / 2
    return center_x + radius_x * math.cos(angle), center_y + radius_y * math.sin(angle)


def build_icon() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    image.alpha_composite(ellipse_layer((86, 108, 938, 960), (0, 0, 0, 138), 38))

    draw = ImageDraw.Draw(image)
    draw.ellipse((64, 64, 960, 960), fill=(20, 20, 19, 255), outline=(87, 87, 82, 255), width=24)
    draw.ellipse((112, 112, 912, 912), fill=(38, 38, 36, 255), outline=(9, 9, 9, 210), width=18)

    image.alpha_composite(ellipse_layer((178, 126, 710, 548), (255, 255, 250, 42), 92))
    image.alpha_composite(ellipse_layer((314, 344, 852, 902), (0, 0, 0, 92), 84))

    draw = ImageDraw.Draw(image)
    ring_box = (238, 238, 786, 786)
    ring_width = 72
    draw.ellipse(ring_box, outline=(72, 72, 68, 255), width=ring_width)

    start_angle = -84
    end_angle = 198
    accent = (185, 245, 200, 255)
    draw.arc(ring_box, start_angle, end_angle, fill=accent, width=ring_width)
    cap_radius = ring_width / 2
    cap_path = tuple(int(value + cap_radius if index < 2 else value - cap_radius) for index, value in enumerate(ring_box))
    for angle in (start_angle, end_angle):
        x, y = arc_endpoint(cap_path, angle)
        draw.ellipse(
            (x - cap_radius, y - cap_radius, x + cap_radius, y + cap_radius),
            fill=accent,
        )

    draw.ellipse((360, 360, 664, 664), fill=(22, 22, 21, 255))
    font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 230)
    text_box = draw.textbbox((0, 0), "C", font=font)
    text_width = text_box[2] - text_box[0]
    text_height = text_box[3] - text_box[1]
    draw.text(
        ((SIZE - text_width) / 2, (SIZE - text_height) / 2 - text_box[1] - 5),
        "C",
        font=font,
        fill=(245, 245, 240, 255),
    )

    return image


def main() -> None:
    icon = build_icon()
    ICO_PATH.parent.mkdir(parents=True, exist_ok=True)
    PNG_PATH.parent.mkdir(parents=True, exist_ok=True)
    icon.resize((512, 512), Image.Resampling.LANCZOS).save(PNG_PATH, optimize=True)
    icon.save(
        ICO_PATH,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(f"Generated {ICO_PATH}")
    print(f"Generated {PNG_PATH}")


if __name__ == "__main__":
    main()
