"""Package frames captured by the native WPF UI check as a looping GIF."""

import argparse
from pathlib import Path

from PIL import Image


def package_frames(frame_dir: Path, output: Path) -> None:
    paths = sorted(frame_dir.glob("*.png"))
    if not paths:
        raise ValueError(f"No rendered PNG frames in {frame_dir}")

    width = height = 0
    for path in paths:
        with Image.open(path) as frame:
            width = max(width, frame.width)
            height = max(height, frame.height)

    frames = []
    duration = []
    for index, path in enumerate(paths):
        with Image.open(path) as frame:
            canvas = Image.new("RGB", (width + 24, height + 24), "#0B1012")
            rgba = frame.convert("RGBA")
            canvas.paste(rgba, (12, 12), rgba)
            frames.append(canvas)
        next_path = paths[index + 1] if index + 1 < len(paths) else None
        if next_path is None or path.name[:2] != next_path.name[:2]:
            duration.append(1300 if index == 0 or next_path is None else 850)
        else:
            delta = int(next_path.stem.rsplit("-", 1)[1]) - int(path.stem.rsplit("-", 1)[1])
            duration.append(max(10, round(delta / 10) * 10))

    output.parent.mkdir(parents=True, exist_ok=True)
    frames[0].save(output, save_all=True, append_images=frames[1:], duration=duration,
                   loop=0, optimize=True, disposal=1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("frame_dir", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    package_frames(args.frame_dir, args.output)
