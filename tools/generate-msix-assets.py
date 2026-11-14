"""Generate the six MSIX tile assets from the checked-in SVG mark.

Mirrors tools/generate-icon.py so the packaging project stays in sync with the
application icon. Requires Pillow. Re-run after changing dlss5-video-enc.svg.
"""
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw

repo = Path(__file__).resolve().parents[1]
svg_path = repo / "src/Dlss5Demo.App/Assets/dlss5-video-enc.svg"
output = repo / "packaging/Dlss5VideoEnc.Msix/Assets"
svg = ET.parse(svg_path).getroot()
view = float(svg.attrib["width"])
background = "#0B0D10"


def render(size: int) -> Image.Image:
    """Rasterize the SVG mark at 8x and downsample for clean small-size edges."""
    scale = size * 8 / view
    canvas = Image.new("RGBA", (size * 8, size * 8))
    draw = ImageDraw.Draw(canvas)
    for element in svg:
        if element.tag.endswith("rect"):
            x, y, w, h, r = (
                float(element.attrib[k]) * scale
                for k in ("x", "y", "width", "height", "rx")
            )
            draw.rounded_rectangle(
                (x, y, x + w, y + h), radius=r, fill=element.attrib["fill"]
            )
        elif element.tag.endswith("polygon"):
            points = [
                tuple(float(value) * scale for value in pair.split(","))
                for pair in element.attrib["points"].split()
            ]
            draw.polygon(points, fill=element.attrib["fill"])
        else:
            raise ValueError("Unsupported icon geometry: " + element.tag)
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def wide(width: int = 310, height: int = 150) -> Image.Image:
    """Wide tile: dark brand background with the square mark centered."""
    tile = Image.new("RGBA", (width, height), background)
    mark = render(128)
    tile.alpha_composite(mark, ((width - mark.width) // 2, (height - mark.height) // 2))
    return tile


def main() -> None:
    output.mkdir(parents=True, exist_ok=True)
    assets = {
        "StoreLogo.png": render(50),
        "Square44x44Logo.png": render(44),
        "Square71x71Logo.png": render(71),
        "Square150x150Logo.png": render(150),
        "Square310x310Logo.png": render(310),
        "Wide310x150Logo.png": wide(),
    }
    for name, image in assets.items():
        image.save(output / name)
        print(f"{name}: {image.width}x{image.height}")


if __name__ == "__main__":
    main()
