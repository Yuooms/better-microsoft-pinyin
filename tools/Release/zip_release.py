#!/usr/bin/env python3
"""把 publish/v<版本>/ 打成发布 zip，并生成同名的 .sha256。

版本号从根目录 Directory.Build.props 读取，避免手改脚本。
只用标准库，不依赖第三方包。
"""

from __future__ import annotations

import hashlib
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read_version() -> str:
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", props)
    if match is None:
        sys.exit("Directory.Build.props 里没有找到 <Version>")
    return match.group(1).strip()


def main() -> int:
    version = read_version()
    source = ROOT / "publish" / f"v{version}"
    if not source.is_dir():
        sys.exit(f"没有找到发布目录：{source}\n先执行 dotnet publish，见 docs/RELEASE.md")

    target = ROOT / "publish" / f"BetterMicrosoftPinyin-v{version}-win-x64.zip"
    target.parent.mkdir(parents=True, exist_ok=True)

    files = sorted(path for path in source.rglob("*") if path.is_file())
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in files:
            archive.write(path, path.relative_to(source))

        # 许可证与第三方声明也带上：解压后的目录本身就该是完整的再分发单元。
        for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
            extra = ROOT / name
            if extra.exists():
                archive.write(extra, name)

    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    checksum = target.with_suffix(target.suffix + ".sha256")
    checksum.write_text(f"{digest}  {target.name}\n", encoding="utf-8")

    print(f"版本：{version}")
    print(f"文件：{target}（{target.stat().st_size / 1024 / 1024:.1f} MB，{len(files)} 个文件）")
    print(f"SHA256：{digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
