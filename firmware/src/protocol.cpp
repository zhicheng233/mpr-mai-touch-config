#include "protocol.h"

#include "config.h"

namespace {

uint16_t readLe16(const uint8_t* value) {
  return static_cast<uint16_t>(value[0]) |
         (static_cast<uint16_t>(value[1]) << 8);
}

void writeLe16(uint8_t* output, uint16_t value) {
  output[0] = static_cast<uint8_t>(value & 0xFF);
  output[1] = static_cast<uint8_t>(value >> 8);
}

void copySyncSuffix(uint8_t* buffer, uint16_t* length, uint16_t* expected,
                    bool* active) {
  for (uint16_t start = 1; start < *length; ++start) {
    if (buffer[start] == protocol::Sync0) {
      if (start + 1 == *length || buffer[start + 1] == protocol::Sync1) {
        const uint16_t remaining = static_cast<uint16_t>(*length - start);
        for (uint16_t i = 0; i < remaining; ++i) buffer[i] = buffer[start + i];
        *length = remaining;
        *expected = 0;
        *active = true;
        return;
      }
    }
  }
  *length = 0;
  *expected = 0;
  *active = false;
}

}  // namespace

namespace protocol {

bool isSupportedVersion(uint8_t version) { return (version >> 4) == 1; }

bool isKnownRequestType(uint8_t type) {
  switch (type) {
    case TypeVersion:
    case TypeEnterConfig:
    case TypeExitConfig:
    case TypeGetConfig:
    case TypeSetConfig:
    case TypeSaveConfig:
    case TypeApplyConfig:
    case TypeCalibrate:
    case TypeStreamStart:
    case TypeStreamStop:
      return true;
    default:
      return false;
  }
}

bool validPayloadLength(uint8_t type, uint16_t length) {
  switch (type) {
    case TypeVersion:
    case TypeEnterConfig:
    case TypeExitConfig:
    case TypeGetConfig:
    case TypeSaveConfig:
    case TypeApplyConfig:
    case TypeStreamStop:
      return length == 0;
    case TypeSetConfig:
      return length == config::BlobLength;
    case TypeCalibrate:
      return length == 3;
    case TypeStreamStart:
      return length == 2;
    default:
      return false;
  }
}

void FrameParser::reset() {
  length_ = 0;
  expectedLength_ = 0;
  active_ = false;
}

FrameResult FrameParser::feed(uint8_t byte, uint32_t nowMs, Frame* output) {
  if (active_ && static_cast<uint32_t>(nowMs - lastByteMs_) >= FrameTimeoutMs) reset();
  lastByteMs_ = nowMs;
  if (!active_) {
    if (byte != Sync0) return FrameResult::None;
    buffer_[0] = byte;
    length_ = 1;
    active_ = true;
    return FrameResult::None;
  }
  if (length_ == 1 && byte != Sync1) {
    if (byte == Sync0) {
      buffer_[0] = byte;
      return FrameResult::None;
    }
    reset();
    return FrameResult::None;
  }
  if (length_ >= MaxFrameLength) {
    reset();
    return FrameResult::BadLength;
  }
  buffer_[length_++] = byte;
  if (length_ == 8) {
    const uint16_t payloadLength = readLe16(buffer_ + 6);
    if (output != nullptr) {
      output->version = buffer_[2];
      output->type = buffer_[3];
      output->sequence = readLe16(buffer_ + 4);
      output->length = payloadLength;
    }
    if (!isSupportedVersion(buffer_[2])) {
      const FrameResult result = FrameResult::BadVersion;
      copySyncSuffix(buffer_, &length_, &expectedLength_, &active_);
      return result;
    }
    if (payloadLength > MaxPayload) {
      const FrameResult result = FrameResult::BadLength;
      copySyncSuffix(buffer_, &length_, &expectedLength_, &active_);
      return result;
    }
    expectedLength_ = static_cast<uint16_t>(payloadLength + 10);
  }
  if (expectedLength_ != 0 && length_ == expectedLength_) {
    const FrameResult result = decodeFrame(buffer_, length_, output);
    if (result != FrameResult::Complete && output != nullptr) {
      output->version = buffer_[2];
      output->type = buffer_[3];
      output->sequence = readLe16(buffer_ + 4);
      output->length = readLe16(buffer_ + 6);
    }
    if (result == FrameResult::Complete) {
      reset();
    } else {
      copySyncSuffix(buffer_, &length_, &expectedLength_, &active_);
    }
    return result;
  }
  return FrameResult::None;
}

uint16_t crc16Ccitt(const uint8_t* data, uint16_t length) {
  uint16_t crc = 0xFFFF;
  for (uint16_t i = 0; i < length; ++i) {
    crc ^= static_cast<uint16_t>(data[i]) << 8;
    for (uint8_t bit = 0; bit < 8; ++bit) {
      crc = (crc & 0x8000) ? static_cast<uint16_t>((crc << 1) ^ 0x1021)
                           : static_cast<uint16_t>(crc << 1);
    }
  }
  return crc;
}

bool encodeFrame(uint8_t type, uint16_t sequence, const uint8_t* payload,
                 uint16_t payloadLength, uint8_t* output,
                 uint16_t outputCapacity, uint16_t* outputLength) {
  if (output == nullptr || outputLength == nullptr || payloadLength > MaxPayload ||
      outputCapacity < static_cast<uint16_t>(payloadLength + 10) ||
      (payloadLength != 0 && payload == nullptr)) {
    return false;
  }
  output[0] = Sync0;
  output[1] = Sync1;
  output[2] = Version;
  output[3] = type;
  writeLe16(output + 4, sequence);
  writeLe16(output + 6, payloadLength);
  for (uint16_t i = 0; i < payloadLength; ++i) output[8 + i] = payload[i];
  const uint16_t crc = crc16Ccitt(output + 2, payloadLength + 6);
  writeLe16(output + 8 + payloadLength, crc);
  *outputLength = static_cast<uint16_t>(payloadLength + 10);
  return true;
}

FrameResult decodeFrame(const uint8_t* input, uint16_t inputLength, Frame* output) {
  if (input == nullptr || output == nullptr || inputLength < 10 ||
      input[0] != Sync0 || input[1] != Sync1) {
    return FrameResult::None;
  }
  if (!isSupportedVersion(input[2])) return FrameResult::BadVersion;
  const uint16_t length = readLe16(input + 6);
  if (length > MaxPayload || inputLength != static_cast<uint16_t>(length + 10)) {
    return FrameResult::BadLength;
  }
  const uint16_t expected = readLe16(input + 8 + length);
  if (crc16Ccitt(input + 2, length + 6) != expected) return FrameResult::BadCrc;
  output->version = input[2];
  output->type = input[3];
  output->sequence = readLe16(input + 4);
  output->length = length;
  for (uint16_t i = 0; i < length; ++i) output->payload[i] = input[8 + i];
  return FrameResult::Complete;
}

uint64_t packKeyBits(const bool* keys) {
  if (keys == nullptr) return 0;
  uint64_t value = 0;
  // 与参考固件 TouchSend 逐位一致：它从 i=33 倒着左移，等价于 key i 占 value 的第 i 位，
  // 也就是 A1(key 0) 落在 bit 0、E8(key 33) 落在 bit 33。写成 bit 33-i 会把整帧镜像，
  // 游戏里按 A1 收到的是 E8。
  for (uint8_t i = 0; i < KeyCount; ++i) {
    if (keys[i]) value |= 1ULL << i;
  }
  return value;
}

void encodeLegacyGameFrame(const bool* keys, uint8_t output[LegacyFrameLength]) {
  output[0] = '(';
  uint64_t value = packKeyBits(keys);
  for (uint8_t chunk = 0; chunk < 7; ++chunk) {
    output[1 + chunk] = static_cast<uint8_t>(value & 0x1F);
    value >>= 5;
  }
  output[8] = ')';
}

bool decodeLegacyCommand(uint8_t byte, uint8_t command[LegacyCommandLength],
                         bool* complete) {
  static uint8_t buffer[LegacyCommandLength];
  static uint8_t length = 0;
  if (complete == nullptr || command == nullptr) return false;
  *complete = false;
  if (byte == '{') {
    buffer[0] = byte;
    length = 1;
    return false;
  }
  if (length == 0) return false;
  if (length < LegacyCommandLength) {
    buffer[length++] = byte;
  } else {
    length = 0;
    return false;
  }
  if (length == LegacyCommandLength) {
    const bool valid = buffer[5] == '}' && buffer[0] == '{';
    if (valid) {
      for (uint8_t i = 0; i < LegacyCommandLength; ++i) command[i] = buffer[i];
      *complete = true;
    }
    length = 0;
    return valid;
  }
  return false;
}

}  // namespace protocol
