"""
把固件版本号从 firmware/src/version.h 抽出来，写成上位机可读的 firmware.json。

为什么需要：固件语义版本号只有一处真相（version.h），但工具要展示
「要刷入的固件版本」和「URL 上的最新版本」，这两边都拿不到 C 宏。
手工再抄一份数字必然漂移 —— 这次 bug 就是这么来的（新固件和旧固件都报 0.1.0）。

所以构建时生成一份 JSON，由 PlatformIO 的 extra_scripts 调用，产物落到
.pio/build/<env>/firmware.json，和 firmware.bin 放在一起，随 bin 一起分发。
"""
import json
import os
import re

Import("env")  # noqa: F821  (PlatformIO 注入)

PROJECT_DIR = env.subst("$PROJECT_DIR")  # noqa: F821
SRC = os.path.join(PROJECT_DIR, "src", "version.h")


def parse_version_header(path):
    text = open(path, encoding="utf-8").read()

    def grab(name):
        m = re.search(r"constexpr\s+uint8_t\s+%s\s*=\s*(\d+)\s*;" % name, text)
        if not m:
            raise RuntimeError("version.h: 找不到 %s" % name)
        return int(m.group(1))

    return {
        "major": grab("Major"),
        "minor": grab("Minor"),
        "patch": grab("Patch"),
        "protocolMajor": grab("ProtocolMajor"),
        "protocolMinor": grab("ProtocolMinor"),
    }


def write_manifest(source, target, env):  # noqa: ARG001
    info = parse_version_header(SRC)
    version = "%d.%d.%d" % (info["major"], info["minor"], info["patch"])
    build_dir = env.subst("$BUILD_DIR")  # noqa: F821
    bin_path = os.path.join(build_dir, "firmware.bin")

    manifest = {
        "schema": 1,
        "version": version,
        "versionCode": (info["major"] << 16) | (info["minor"] << 8) | info["patch"],
        "protocol": "%d.%d" % (info["protocolMajor"], info["protocolMinor"]),
        "board": env.subst("$BOARD"),  # noqa: F821
        "env": env.subst("$PIOENV"),  # noqa: F821
        # 只有在 bin 真的存在时才给出大小/sha256，否则留空让上位机自己算
        "binary": "firmware.bin",
        "size": os.path.getsize(bin_path) if os.path.exists(bin_path) else None,
    }

    out = os.path.join(build_dir, "firmware.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)
        handle.write("\n")
    print("firmware.json -> %s (version %s, protocol %s)"
          % (out, version, manifest["protocol"]))


# 链接完才有 .bin，所以挂在 firmware.bin 生成之后再跑
env.AddPostAction("$BUILD_DIR/firmware.bin", write_manifest)  # noqa: F821
