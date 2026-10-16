#pragma once

#include <stdint.h>

namespace protocol {

constexpr uint8_t Sync0 = 0xAA;
constexpr uint8_t Sync1 = 0x55;
constexpr uint8_t Version = 0x10;
constexpr uint16_t MaxPayload = 256;
constexpr uint16_t MaxFrameLength = 266;
constexpr uint32_t FrameTimeoutMs = 250;
constexpr uint8_t LegacyFrameLength = 9;
constexpr uint8_t LegacyCommandLength = 6;
constexpr uint8_t KeyCount = 34;

constexpr uint8_t TypeVersion = 0x01;
constexpr uint8_t TypeEnterConfig = 0x02;
constexpr uint8_t TypeExitConfig = 0x03;
constexpr uint8_t TypeGetConfig = 0x10;
constexpr uint8_t TypeSetConfig = 0x11;
constexpr uint8_t TypeSaveConfig = 0x12;
constexpr uint8_t TypeApplyConfig = 0x13;
constexpr uint8_t TypeCalibrate = 0x20;
constexpr uint8_t TypeCalibrationDone = 0x21;
constexpr uint8_t TypeStreamStart = 0x30;
constexpr uint8_t TypeStreamStop = 0x31;
constexpr uint8_t TypeError = 0x7F;
constexpr uint8_t TypeAck = 0x80;
constexpr uint8_t TypeDebugFrame = 0x90;

constexpr uint8_t StatusOk = 0x00;
constexpr uint8_t StatusBadFrame = 0x01;
constexpr uint8_t StatusUnknownType = 0x02;
constexpr uint8_t StatusBadLength = 0x03;
constexpr uint8_t StatusInvalidValue = 0x04;
constexpr uint8_t StatusIllegalState = 0x05;
constexpr uint8_t StatusBusy = 0x06;
constexpr uint8_t StatusSequenceConflict = 0x07;
constexpr uint8_t StatusAlreadyConfigured = 0x08;
constexpr uint8_t StatusHardware = 0x09;
constexpr uint8_t StatusStorage = 0x0A;
constexpr uint8_t StatusTimeout = 0x0B;

constexpr uint8_t LegacyReset = 0x45;
constexpr uint8_t LegacyHalt = 0x4C;
constexpr uint8_t LegacyStart = 0x41;
constexpr uint8_t LegacyRatio = 0x72;
constexpr uint8_t LegacySensitivity = 0x6B;

static_assert(MaxFrameLength == MaxPayload + 10, "frame bound must include fixed header and CRC");

enum class FrameResult : uint8_t {
  None,
  Complete,
  BadVersion,
  BadLength,
  BadCrc,
};

struct Frame {
  uint8_t version;
  uint8_t type;
  uint16_t sequence;
  uint16_t length;
  uint8_t payload[MaxPayload];
};

bool isSupportedVersion(uint8_t version);
bool isKnownRequestType(uint8_t type);
bool validPayloadLength(uint8_t type, uint16_t length);

class FrameParser {
 public:
  void reset();
  FrameResult feed(uint8_t byte, uint32_t nowMs, Frame* output);
  bool active() const { return active_; }

 private:
  uint8_t buffer_[MaxFrameLength] = {};
  uint16_t length_ = 0;
  uint16_t expectedLength_ = 0;
  uint32_t lastByteMs_ = 0;
  bool active_ = false;
};

uint16_t crc16Ccitt(const uint8_t* data, uint16_t length);
bool encodeFrame(uint8_t type, uint16_t sequence, const uint8_t* payload,
                 uint16_t payloadLength, uint8_t* output,
                 uint16_t outputCapacity, uint16_t* outputLength);
FrameResult decodeFrame(const uint8_t* input, uint16_t inputLength, Frame* output);

uint64_t packKeyBits(const bool* keys);
void encodeLegacyGameFrame(const bool* keys, uint8_t output[LegacyFrameLength]);
bool decodeLegacyCommand(uint8_t byte, uint8_t command[LegacyCommandLength],
                         bool* complete);

}  // namespace protocol
