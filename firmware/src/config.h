#pragma once

#include <stdint.h>

#include "touchmap.h"

namespace config {

constexpr uint8_t Schema = 1;
constexpr uint16_t BlobLength = 59;
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
};

Config defaults();
bool validate(const Config& value);
bool serialize(const Config& value, uint8_t* output, uint16_t outputCapacity);
bool deserialize(const uint8_t* input, uint16_t inputLength, Config* output);

uint16_t touchDelta(const Config& value, uint8_t board, int8_t electrodeOffset);
uint16_t releaseDelta(uint16_t touchDeltaValue, uint16_t hysteresis);

}  // namespace config
