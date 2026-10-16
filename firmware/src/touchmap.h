#pragma once

#include <stdint.h>

namespace touchmap {

constexpr uint8_t KeyCount = 34;
constexpr uint8_t BoardCount = 4;
constexpr uint8_t ChannelCount = 48;
constexpr uint8_t PortCount = 12;

struct KeyMapping {
  uint8_t board;
  uint8_t channel;
  int8_t legacyOffset;
};

constexpr uint8_t BoardAddresses[BoardCount] = {0x5A, 0x5B, 0x5C, 0x5D};

// 默认焊接顺序（参考固件 config.h touchmap[34] 的电极 → 芯片映射）。
constexpr KeyMapping Keys[KeyCount] = {
    {3, 4, -3},  {3, 0, -3},  {2, 4, -3},  {2, 0, -3},  {1, 5, 0},
    {1, 0, -3},  {0, 4, -3},  {0, 0, -3},  {3, 5, -17}, {3, 1, -18},
    {2, 5, 0},   {2, 1, 0},   {1, 6, 2},   {1, 1, 0},   {0, 5, 2},
    {0, 1, 3},   {2, 8, -17}, {1, 2, -19}, {3, 6, 0},   {3, 2, 0},
    {2, 6, 0},   {2, 2, 0},   {1, 7, 0},   {1, 3, 0},   {0, 6, 0},
    {0, 2, 0},   {3, 7, 0},   {3, 3, 0},   {2, 7, 0},   {2, 3, 0},
    {1, 8, -18}, {1, 4, -2},  {0, 7, 0},   {0, 3, 0},
};

// 每键一个打包字节 (board << 4) | channel：board 0..3，channel 0..11（合法值 0..47）。
constexpr uint8_t PackMapping(uint8_t board, uint8_t channel) {
  return static_cast<uint8_t>((board << 4) | (channel & 0x0F));
}

constexpr uint8_t UnpackBoard(uint8_t packed) { return static_cast<uint8_t>(packed >> 4); }
constexpr uint8_t UnpackChannel(uint8_t packed) { return static_cast<uint8_t>(packed & 0x0F); }

// 默认 keyMap：与 Keys[] 等价，配置未改动时游戏输出逐位一致。
constexpr uint8_t DefaultKeyMap[KeyCount] = {
    52, 48, 36, 32, 21, 16, 4,  0,  53, 49, 37, 33, 22, 17, 5,  1,  40,
    18, 54, 50, 38, 34, 23, 19, 6,  2,  55, 51, 39, 35, 24, 20, 7,  3,
};

}  // namespace touchmap
