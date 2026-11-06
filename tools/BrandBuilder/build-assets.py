#!/usr/bin/env python3
"""把 LOGO/ 下的 2048px 源图转换成仓库使用的品牌资源。

生成：
  assets/brand/logo0.png   512x512，关于页与 README 的主视觉
  assets/brand/logo1.png   512x512，关于页与 README 的应用图标形象
  assets/brand/app.ico     16/24/32/48/64/128/256，exe、任务栏与托盘共用

只用标准库（struct + zlib），不依赖 Pillow。源图是 RGBA PNG：
透明背景 + 圆形徽章，缩放时必须按 alpha 预乘，否则边缘会出现黑边。

用法（仓库根目录）：
  python tools/BrandBuilder/build-assets.py

源图约定：
  LOGO/*LOGO0*.png  主视觉
  LOGO/*LOGO1*.png  应用图标
LOGO/ 是本地设计源，不纳入版本管理；生成的三个文件需要提交。
"""

from __future__ import annotations

import glob
import os
import struct
import zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE_DIR = os.path.join(ROOT, "LOGO")
OUTPUT_DIR = os.path.join(ROOT, "assets", "brand")

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
PREVIEW_SIZE = 512


# ---------------------------------------------------------------- PNG 解码

def read_png(path: str) -> tuple[int, int, bytearray]:
    """读取 8bit RGBA PNG，返回 (宽, 高, RGBA 字节)。"""
    with open(path, "rb") as handle:
        raw = handle.read()

    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"不是 PNG：{path}")

    position = 8
    width = height = None
    idat = bytearray()

    while position < len(raw):
        length = struct.unpack(">I", raw[position : position + 4])[0]
        name = raw[position + 4 : position + 8]
        data = raw[position + 8 : position + 8 + length]
        position += length + 12

        if name == b"IHDR":
            width, height, depth, color_type = struct.unpack(">IIBB", data[:10])
            if depth != 8 or color_type != 6:
                raise ValueError(f"只支持 8bit RGBA：{path}（depth={depth}, type={color_type}）")
        elif name == b"IDAT":
            idat += data
        elif name == b"IEND":
            break

    if width is None or height is None:
        raise ValueError(f"缺少 IHDR：{path}")

    return width, height, unfilter(zlib.decompress(idat), width, height)


def unfilter(data: bytes, width: int, height: int) -> bytearray:
    """还原 PNG 扫描线的五种过滤器。"""
    stride = width * 4
    output = bytearray(height * stride)
    previous = bytearray(stride)
    offset = 0

    for y in range(height):
        filter_type = data[offset]
        offset += 1
        line = bytearray(data[offset : offset + stride])
        offset += stride

        if filter_type == 1:  # Sub
            for x in range(4, stride):
                line[x] = (line[x] + line[x - 4]) & 0xFF
        elif filter_type == 2:  # Up
            for x in range(stride):
                line[x] = (line[x] + previous[x]) & 0xFF
        elif filter_type == 3:  # Average
            for x in range(stride):
                left = line[x - 4] if x >= 4 else 0
                line[x] = (line[x] + ((left + previous[x]) >> 1)) & 0xFF
        elif filter_type == 4:  # Paeth
            for x in range(stride):
                left = line[x - 4] if x >= 4 else 0
                upper_left = previous[x - 4] if x >= 4 else 0
                line[x] = (line[x] + paeth(left, previous[x], upper_left)) & 0xFF
        elif filter_type != 0:
            raise ValueError(f"未知的 PNG 过滤器：{filter_type}")

        output[y * stride : (y + 1) * stride] = line
        previous = line

    return output


def paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


# ---------------------------------------------------------------- 缩放

def axis_weights(source_length: int, target_length: int) -> list[list[tuple[int, float]]]:
    """按面积平均计算每个目标像素覆盖的源像素及权重（权重和为 1）。"""
    scale = source_length / target_length
    result: list[list[tuple[int, float]]] = []

    for index in range(target_length):
        start = index * scale
        end = (index + 1) * scale
        first = int(start)
        last = min(int(end) + (0 if end == int(end) else 1), source_length)

        weights = []
        for source in range(first, last):
            overlap = min(end, source + 1) - max(start, source)
            if overlap > 0:
                weights.append((source, overlap / scale))

        if not weights:
            weights.append((min(first, source_length - 1), 1.0))
        result.append(weights)

    return result


def resize(image: bytearray, width: int, height: int, target_width: int, target_height: int) -> bytearray:
    """两遍可分离面积缩放：先横向，再纵向。alpha 参与加权，避免白边/黑边。"""
    horizontal = resize_horizontal(image, width, height, target_width)
    return resize_vertical(horizontal, target_width, height, target_height)


def resize_horizontal(image: bytearray, width: int, height: int, target_width: int) -> bytearray:
    weights = axis_weights(width, target_width)
    output = bytearray(target_width * height * 4)

    for y in range(height):
        row = y * width * 4
        out_row = y * target_width * 4
        for x, columns in enumerate(weights):
            red = green = blue = alpha = 0.0
            for source, weight in columns:
                offset = row + source * 4
                a = image[offset + 3] / 255.0
                if a == 0.0:
                    continue
                w = weight * a
                red += image[offset] * w
                green += image[offset + 1] * w
                blue += image[offset + 2] * w
                alpha += w

            offset = out_row + x * 4
            if alpha > 1e-6:
                output[offset] = min(255, round(red / alpha))
                output[offset + 1] = min(255, round(green / alpha))
                output[offset + 2] = min(255, round(blue / alpha))
                output[offset + 3] = min(255, round(alpha * 255))

    return output


def resize_vertical(image: bytearray, width: int, height: int, target_height: int) -> bytearray:
    weights = axis_weights(height, target_height)
    output = bytearray(width * target_height * 4)
    stride = width * 4

    for y, rows in enumerate(weights):
        out_row = y * stride
        for x in range(width):
            red = green = blue = alpha = 0.0
            for source, weight in rows:
                offset = source * stride + x * 4
                a = image[offset + 3] / 255.0
                if a == 0.0:
                    continue
                w = weight * a
                red += image[offset] * w
                green += image[offset + 1] * w
                blue += image[offset + 2] * w
                alpha += w

            offset = out_row + x * 4
            if alpha > 1e-6:
                output[offset] = min(255, round(red / alpha))
                output[offset + 1] = min(255, round(green / alpha))
                output[offset + 2] = min(255, round(blue / alpha))
                output[offset + 3] = min(255, round(alpha * 255))

    return output


# ---------------------------------------------------------------- 输出

def write_png(path: str, width: int, height: int, image: bytearray) -> bytes:
    """写 8bit RGBA PNG，返回编码后的字节（ICO 里直接复用）。"""
    raw = bytearray()
    stride = width * 4
    for y in range(height):
        raw.append(0)  # 过滤器 None：缩放结果已经平滑，再过滤收益不大
        raw += image[y * stride : (y + 1) * stride]

    def chunk(name: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + name
            + payload
            + struct.pack(">I", zlib.crc32(name + payload) & 0xFFFFFFFF)
        )

    encoded = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )

    with open(path, "wb") as handle:
        handle.write(encoded)
    return encoded


def write_ico(path: str, frames: list[tuple[int, bytes]]) -> None:
    """写多尺寸 ICO。每个尺寸用 PNG 压缩（Vista+ 支持）。"""
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = bytearray()
    payload = bytearray()

    for size, png in frames:
        dimension = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(png), offset)
        payload += png
        offset += len(png)

    with open(path, "wb") as handle:
        handle.write(header + entries + payload)


# ---------------------------------------------------------------- 主流程

def find_source(pattern: str) -> str:
    matches = sorted(glob.glob(os.path.join(SOURCE_DIR, pattern)))
    if not matches:
        raise SystemExit(f"找不到源图：{pattern}（放在 {SOURCE_DIR}）")
    return matches[0]


def main() -> None:
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    sources = {
        "logo0": find_source("*LOGO0*.png"),
        "logo1": find_source("*LOGO1*.png"),
    }

    for name, path in sources.items():
        width, height, image = read_png(path)
        print(f"{name}: {width}x{height} <- {os.path.basename(path)}")

        preview = resize(image, width, height, PREVIEW_SIZE, PREVIEW_SIZE)
        output = os.path.join(OUTPUT_DIR, f"{name}.png")
        write_png(output, PREVIEW_SIZE, PREVIEW_SIZE, preview)
        print(f"  -> {output}（{os.path.getsize(output) // 1024} KB）")

        if name != "logo1":
            continue

        # ICO 只从 LOGO1 生成：用户指定它作为 exe 图标。
        frames: list[tuple[int, bytes]] = []
        for size in ICO_SIZES:
            frame = resize(preview, PREVIEW_SIZE, PREVIEW_SIZE, size, size)
            frames.append((size, write_png(os.path.join(OUTPUT_DIR, f".tmp-{size}.png"), size, size, frame)))
            os.remove(os.path.join(OUTPUT_DIR, f".tmp-{size}.png"))

        icon_path = os.path.join(OUTPUT_DIR, "app.ico")
        write_ico(icon_path, frames)
        print(f"  -> {icon_path}（{os.path.getsize(icon_path) // 1024} KB，{len(frames)} 个尺寸）")


if __name__ == "__main__":
    main()
