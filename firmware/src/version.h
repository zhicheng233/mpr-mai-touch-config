#pragma once

// 固件版本号的唯一来源。
//
// 之前这个号码直接硬编码在 main.cpp 的 VERSION 应答里，结果「加入可配置按键映射」
// 那次改动没有动它 —— 板子上跑的新固件和旧固件报的都是 0.1.0，仅凭版本号分不出来，
// 只能靠 capability 位猜。所以现在统一放这里，并让构建脚本从本文件生成 firmware.json
// 给上位机读，避免工具和固件各写一份数字。
//
// 改版本时只改这里，然后重新 `pio run`，构建会自动更新 firmware.json。
namespace firmware_version {

constexpr uint8_t Major = 0;
constexpr uint8_t Minor = 2;
constexpr uint8_t Patch = 2;

// 协议版本跟着 PROTOCOL.md 走；与固件语义版本相互独立。
constexpr uint8_t ProtocolMajor = 1;
constexpr uint8_t ProtocolMinor = 1;

}  // namespace firmware_version

