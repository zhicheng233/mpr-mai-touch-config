#include "config.h"

namespace {

int16_t clampSigned(int16_t value, int16_t minimum, int16_t maximum) {
  if (value < minimum) return minimum;
  if (value > maximum) return maximum;
  return value;
}

uint16_t clampUnsigned(int32_t value, uint16_t minimum, uint16_t maximum) {
  if (value < minimum) return minimum;
  if (value > maximum) return maximum;
  return static_cast<uint16_t>(value);
}

}  // namespace

namespace config {

Config defaults() {
  Config value = {};
  value.schema = Schema;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    value.boards[board] = {20, 0, 1, 1, 2, 0};
  }
  const int8_t offsets[touchmap::KeyCount] = {
      -3, -3, -3, -3, 0, -3, -3, -3,
      -17, -18, 0, 0, 2, 0, 2, 3,
      -17, -19,
      0, 0, 0, 0, 0, 0, 0, 0,
      0, 0, 0, 0, -18, -2, 0, 0,
  };
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    value.electrodeOffsets[i] = offsets[i];
    value.keyMap[i] = touchmap::DefaultKeyMap[i];
  }
  return value;
}

bool validate(const Config& value) {
  if (value.schema != Schema) return false;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    const BoardConfig& item = value.boards[board];
    if (item.baseThreshold < 1 || item.sensOffset < -127 ||
        item.touchDebounce > 7 ||
        item.releaseDebounce > 7 || item.sfi > 3 || item.esi > 7) {
      return false;
    }
  }
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    if (value.electrodeOffsets[i] < -127) return false;
  }
  // keyMap：每键合法（board<4、channel<12）且 34 键互不重复。
  bool used[touchmap::ChannelCount] = {};
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    const uint8_t packed = value.keyMap[i];
    const uint8_t board = touchmap::UnpackBoard(packed);
    const uint8_t channel = touchmap::UnpackChannel(packed);
    if (board >= touchmap::BoardCount || channel >= touchmap::PortCount) return false;
    const uint8_t index = static_cast<uint8_t>(board * touchmap::PortCount + channel);
    if (used[index]) return false;
    used[index] = true;
  }
  return true;
}

bool serialize(const Config& value, uint8_t* output, uint16_t outputCapacity) {
  if (output == nullptr || outputCapacity < BlobLength || !validate(value)) {
    return false;
  }
  output[0] = value.schema;
  uint16_t offset = 1;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    const BoardConfig& item = value.boards[board];
    output[offset++] = item.baseThreshold;
    output[offset++] = static_cast<uint8_t>(item.sensOffset);
    output[offset++] = item.touchDebounce;
    output[offset++] = item.releaseDebounce;
    output[offset++] = item.sfi;
    output[offset++] = item.esi;
  }
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    output[offset++] = static_cast<uint8_t>(value.electrodeOffsets[i]);
  }
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    output[offset++] = value.keyMap[i];
  }
  return offset == BlobLength;
}

bool deserialize(const uint8_t* input, uint16_t inputLength, Config* output) {
  if (input == nullptr || output == nullptr || inputLength != BlobLength ||
      input[0] != Schema) {
    return false;
  }
  Config candidate = {};
  candidate.schema = input[0];
  uint16_t offset = 1;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    BoardConfig& item = candidate.boards[board];
    item.baseThreshold = input[offset++];
    item.sensOffset = static_cast<int8_t>(input[offset++]);
    item.touchDebounce = input[offset++];
    item.releaseDebounce = input[offset++];
    item.sfi = input[offset++];
    item.esi = input[offset++];
  }
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    candidate.electrodeOffsets[i] = static_cast<int8_t>(input[offset++]);
  }
  for (uint8_t i = 0; i < touchmap::KeyCount; ++i) {
    candidate.keyMap[i] = input[offset++];
  }
  if (offset != BlobLength || !validate(candidate)) return false;
  *output = candidate;
  return true;
}

uint16_t touchDelta(const Config& value, uint8_t board, int8_t electrodeOffset) {
  if (board >= touchmap::BoardCount) return 1;
  const BoardConfig& item = value.boards[board];
  return clampUnsigned(static_cast<int32_t>(item.baseThreshold) + item.sensOffset +
                           electrodeOffset,
                       1, 1023);
}

uint16_t releaseDelta(uint16_t touchDeltaValue, uint16_t hysteresis) {
  const int16_t maximum = static_cast<int16_t>(touchDeltaValue) - 1;
  return static_cast<uint16_t>(clampSigned(
      static_cast<int16_t>(touchDeltaValue) - static_cast<int16_t>(hysteresis), 0,
      maximum < 0 ? 0 : maximum));
}

int8_t keyForChannel(const Config& value, uint8_t board, uint8_t channel) {
  if (board >= touchmap::BoardCount || channel >= touchmap::PortCount) return -1;
  const uint8_t packed = touchmap::PackMapping(board, channel);
  for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
    if (value.keyMap[key] == packed) return static_cast<int8_t>(key);
  }
  return -1;
}

int8_t electrodeOffsetForChannel(const Config& value, uint8_t board, uint8_t channel) {
  const int8_t key = keyForChannel(value, board, channel);
  return key < 0 ? 0 : value.electrodeOffsets[key];
}

}  // namespace config
