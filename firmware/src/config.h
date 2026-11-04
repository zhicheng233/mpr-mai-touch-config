#pragma once

#include <stdint.h>

#include "touchmap.h"

namespace config {

constexpr uint8_t Schema = 3;
constexpr uint16_t LegacyBlobLength = 93;
constexpr uint16_t BlobLength = 99;
constexpr uint8_t BoardBlobLength = 6;

struct BoardConfig {
  uint8_t baseThreshold;
  int8_t sensOffset;
  uint8_t touchDebounce;
  uint8_t releaseDebounce;
  uint8_t sfi;
  uint8_t esi;
};

struct Config {
  uint8_t schema;
  BoardConfig boards[touchmap::BoardCount];
  int8_t electrodeOffsets[touchmap::KeyCount];
  // 每键一个打包字节 (board << 4) | channel；34 键必须映射到互不相同的 34 个通道。
  uint8_t keyMap[touchmap::KeyCount];
  uint8_t compensationMode;  // 0 off, 1 observe, 2 compensate; learned values are RAM-only.
  uint8_t compensationKeys[5];  // key i -> bit i; unused high bits must be zero.
};

Config defaults();
bool validate(const Config& value);
bool serialize(const Config& value, uint8_t* output, uint16_t outputCapacity);
bool deserialize(const uint8_t* input, uint16_t inputLength, Config* output);
inline bool compensationSelected(const Config& value, uint8_t key) {
  return value.compensationMode != 0 && key < touchmap::KeyCount &&
         (value.compensationKeys[key / 8] & (1U << (key % 8))) != 0;
}

uint16_t touchDelta(const Config& value, uint8_t board, int8_t electrodeOffset);
uint16_t releaseDelta(uint16_t touchDeltaValue, uint16_t hysteresis);

// 该通道（board*12 + channel）对应的按键索引；未被任何键映射时返回 -1。
int8_t keyForChannel(const Config& value, uint8_t board, uint8_t channel);
// 该通道对应的按键电极偏移；未映射返回 0。
int8_t electrodeOffsetForChannel(const Config& value, uint8_t board, uint8_t channel);

}  // namespace config
