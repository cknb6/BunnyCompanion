#!/usr/bin/env python3
"""Offline structural validation for assets, XAML wiring, and project portability."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image


# Windows CI 的非交互控制台可能默认 cp1252，中文校验结果会在 print 时失败。
for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "BunnyCompanion"
ERRORS: list[str] = []


def fail(message: str) -> None:
    ERRORS.append(message)


def validate_xml() -> None:
    files = [
        PROJECT / "BunnyCompanion.csproj",
        PROJECT / "App.xaml",
        PROJECT / "MainWindow.xaml",
        PROJECT / "SettingsWindow.xaml",
        PROJECT / "app.manifest",
    ]
    for path in files:
        try:
            ET.parse(path)
        except Exception as exception:
            fail(f"XML 无法解析：{path.relative_to(ROOT)}：{exception}")


def validate_event_handlers() -> None:
    event_names = {
        "Loaded", "Closing", "Click", "ValueChanged",
        "MouseLeftButtonDown", "MouseLeftButtonUp", "MouseMove", "MouseRightButtonUp",
        "LostMouseCapture",
    }
    for xaml_path in PROJECT.glob("*.xaml"):
        code_path = xaml_path.with_suffix(".xaml.cs")
        if not code_path.exists():
            continue
        xaml = xaml_path.read_text(encoding="utf-8")
        code = code_path.read_text(encoding="utf-8")
        for event, handler in re.findall(r"\b([A-Za-z]+)=\"([A-Za-z_][A-Za-z0-9_]*)\"", xaml):
            if event in event_names and not re.search(rf"\b{re.escape(handler)}\s*\(", code):
                fail(f"事件处理器缺失：{xaml_path.name} -> {handler}")


def validate_sprites() -> None:
    sprite_dir = PROJECT / "Assets" / "Sprites"
    sprites = sorted(sprite_dir.glob("*.png"))
    if len(sprites) != 48:
        fail(f"精灵数量应为 48，实际为 {len(sprites)}")

    names = set()
    for path in sprites:
        names.add(path.stem)
        with Image.open(path) as image:
            if image.mode != "RGBA":
                fail(f"精灵不是 RGBA：{path.name} ({image.mode})")
            if image.size != (384, 384):
                fail(f"精灵尺寸错误：{path.name} ({image.size})")
            alpha = image.getchannel("A")
            if alpha.getbbox() is None:
                fail(f"精灵完全透明：{path.name}")
            corners = [alpha.getpixel((0, 0)), alpha.getpixel((383, 0)),
                       alpha.getpixel((0, 383)), alpha.getpixel((383, 383))]
            if any(corner != 0 for corner in corners):
                fail(f"精灵角落未透明：{path.name}")
            raw_pixels = image.tobytes()
            pixels = list(zip(raw_pixels[0::4], raw_pixels[1::4], raw_pixels[2::4], raw_pixels[3::4]))
            coverage = sum(1 for red, green, blue, alpha_value in pixels if alpha_value >= 22) / len(pixels)
            if not 0.10 <= coverage <= 0.40:
                fail(f"精灵主体覆盖率异常：{path.name} ({coverage:.1%})")
            green_residue = sum(
                1 for red, green, blue, alpha_value in pixels
                if alpha_value > 25 and green > 80 and green > red * 1.35 and green > blue * 1.35
            )
            if green_residue > 10:
                fail(f"精灵疑似残留绿幕边缘：{path.name} ({green_residue} px)")

    icon_path = PROJECT / "Assets" / "BunnyCompanion.ico"
    try:
        with Image.open(icon_path) as icon:
            expected_sizes = {(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)}
            actual_sizes = set(icon.info.get("sizes", []))
            if not expected_sizes.issubset(actual_sizes):
                fail(f"ICO 缺少必要尺寸：{sorted(expected_sizes - actual_sizes)}")
    except Exception as exception:
        fail(f"应用图标无效：{exception}")

    catalog = (PROJECT / "Engine" / "PetActionCatalog.cs").read_text(encoding="utf-8")
    referenced = set(re.findall(r'F\(\"([a-z0-9_]+)\"', catalog))
    referenced.update(re.findall(r'Single\(\"([a-z0-9_]+)\"', catalog))
    missing = referenced - names
    if missing:
        fail(f"动作目录引用了不存在的精灵：{', '.join(sorted(missing))}")

    action_keys = set(re.findall(r'\[\"([a-z0-9_]+)\"\]\s*=\s*', catalog))
    main_window = (PROJECT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    triggered = set(re.findall(r'(?:PlayAction|RunTrayAction)\(\"([a-z0-9_]+)\"', main_window))
    for block in re.findall(r'var actions\s*=\s*new\[\]\s*\{([^}]+)\}', main_window):
        triggered.update(re.findall(r'\"([a-z0-9_]+)\"', block))
    unknown_actions = triggered - action_keys
    if unknown_actions:
        fail(f"界面触发了未注册动作：{', '.join(sorted(unknown_actions))}")


def validate_portability() -> None:
    forbidden = ("/workspace/", "/root/", "C:\\Users\\", "D:\\")
    for path in PROJECT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in {".cs", ".xaml", ".csproj", ".manifest"}:
            continue
        content = path.read_text(encoding="utf-8")
        for marker in forbidden:
            if marker in content:
                fail(f"源代码包含硬编码开发路径：{path.relative_to(ROOT)} -> {marker}")

    project_text = (PROJECT / "BunnyCompanion.csproj").read_text(encoding="utf-8")
    required_settings = [
        "<TargetFramework>net8.0-windows</TargetFramework>",
        "<UseWPF>true</UseWPF>",
        "<SelfContained>true</SelfContained>",
        "<PublishSingleFile>true</PublishSingleFile>",
        "<PublishTrimmed>false</PublishTrimmed>",
    ]
    for setting in required_settings:
        if setting not in project_text:
            fail(f"项目缺少发布设置：{setting}")
    if "<PackageReference" in project_text:
        fail("项目不应依赖外部 NuGet 包")


def main() -> int:
    validate_xml()
    validate_event_handlers()
    validate_sprites()
    validate_portability()
    if ERRORS:
        print("验证失败：")
        for error in ERRORS:
            print(f"- {error}")
        return 1
    print("验证通过：XML、XAML 事件、48 个透明精灵、动作引用和路径可移植性均正常。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
