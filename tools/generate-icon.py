"""Compile the two-shape SVG mark into PNG and native-size Windows ICO assets.

Requires Pillow. Geometry and colors come from the checked-in SVG, with each
ICO size rasterized separately so 16/20/24/32 px retain a clean silhouette.
"""
from pathlib import Path
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

assets = Path(__file__).resolve().parents[1] / "src/Dlss5Demo.App/Assets"
svg = ET.parse(assets / "dlss5-video-enc.svg").getroot()

def render(size):
    scale = size * 8 / 256
    image = Image.new("RGBA", (size * 8, size * 8))
    draw = ImageDraw.Draw(image)
    for element in svg:
        if element.tag.endswith("rect"):
            x, y, w, h, r = [float(element.attrib[k]) * scale for k in ("x", "y", "width", "height", "rx")]
            draw.rounded_rectangle((x, y, x + w, y + h), radius=r, fill=element.attrib["fill"])
        elif element.tag.endswith("polygon"):
            points = [tuple(float(v) * scale for v in pair.split(",")) for pair in element.attrib["points"].split()]
            draw.polygon(points, fill=element.attrib["fill"])
        else:
            raise ValueError("Unsupported icon geometry: " + element.tag)
    return image.resize((size, size), Image.Resampling.LANCZOS)

sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
images = [render(size) for size in sizes]
images[-1].save(assets / "dlss5-video-enc.png")
images[-1].save(assets / "dlss5-video-enc.ico", sizes=[(s, s) for s in sizes], append_images=images[:-1])
print("Updated PNG and ICO:", ", ".join(str(s) for s in sizes))
