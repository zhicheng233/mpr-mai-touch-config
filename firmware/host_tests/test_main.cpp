#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "../src/config.h"
#include "../src/calibration.h"
#include "../src/eeprom_store.h"
#include "../src/firmware_state.h"
#include "../src/mpr121.h"
#include "../src/protocol.h"
#include "../src/touch_state.h"

class MemoryEeprom final : public eeprom_store::Backend {
 public:
  MemoryEeprom() { memset(bytes, 0xFF, sizeof(bytes)); }
  uint8_t read(uint16_t address) const override { return bytes[address]; }
  void write(uint16_t address, uint8_t value) override { bytes[address] = value; }
  uint8_t bytes[eeprom_store::SlotSize * eeprom_store::SlotCount];
};

struct CalibrationHardware {
  config::Config installed;
  bool failNext = false;
  uint8_t writes = 0;
};

static bool writeCalibration(void* context, const config::Config& value) {
  CalibrationHardware* hardware = static_cast<CalibrationHardware*>(context);
  ++hardware->writes;
  if (hardware->failNext) {
    hardware->failNext = false;
    return false;
  }
  hardware->installed = value;
  return true;
}

static void checkCalibration() {
  config::Config original = config::defaults();
  uint16_t filtered[touchmap::ChannelCount] = {};
  uint16_t baseline[touchmap::ChannelCount] = {};
  uint16_t acceptedBaselines[touchmap::ChannelCount] = {};
  uint8_t status[touchmap::BoardCount] = {7, 7, 7, 7};
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    baseline[channel] = filtered[channel] = 400;
  }
  Calibration run;
  run.start(1, 200, 10, 42);
  assert(run.active() && run.sequence() == 42);
  for (uint8_t sample = 0; sample < 10; ++sample) {
    const uint32_t time = static_cast<uint32_t>(10 + sample * 20);
    assert(run.due(time));
    run.observe(time, original, filtered, baseline, status, true);
    assert(!run.due(time));
  }
  assert(!run.complete(209) && run.complete(210));
  config::Config candidate = {};
  uint8_t reason = 0xFF;
  assert(run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureNone);
  assert(candidate.boards[1].baseThreshold == 27);
  assert(firmware_state::thresholdForChannel(candidate, 1, 2) == 8);
  assert(firmware_state::thresholdForChannel(candidate, 1, 9) == 27);
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    assert(acceptedBaselines[channel] == 400);
  }

  CalibrationHardware hardware;
  hardware.installed = original;
  config::Config applied = original;
  config::Config staged = original;
  staged.boards[0].baseThreshold = 88;
  hardware.failNext = true;
  assert(!calibration_validation::apply(candidate, &applied, &staged,
                                        writeCalibration, &hardware));
  assert(hardware.writes == 2);
  assert(applied.boards[1].baseThreshold == 20 && staged.boards[0].baseThreshold == 88);
  assert(hardware.installed.boards[1].baseThreshold == 20);
  assert(calibration_validation::apply(candidate, &applied, &staged,
                                       writeCalibration, &hardware));
  assert(applied.boards[1].baseThreshold == 27 && staged.boards[0].baseThreshold == 88);

  // 单次超出守护值不再认定为触摸：必须连续 TouchConsecutiveSamples 次才失败。
  run.start(1, 200, 0, 43);
  filtered[0] = 380;
  run.observe(0, original, filtered, baseline, status, true);
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  // 只有 1 个有效样本，且未达去抖次数 → 不是 TouchDetected，而是样本不足。
  assert(reason != calibration_validation::FailureTouchDetected);

  // 连续多次保持按下 → 才判定 TouchDetected。
  run.start(1, 200, 0, 43);
  filtered[0] = 380;
  for (uint8_t sample = 0; sample < calibration_validation::TouchConsecutiveSamples; ++sample) {
    run.observe(sample * 20, original, filtered, baseline, status, true);
  }
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureTouchDetected);

  // 间歇抖动（中间恢复）应清零计数，不构成触摸。
  run.start(1, 200, 0, 43);
  filtered[0] = 380;
  run.observe(0, original, filtered, baseline, status, true);
  filtered[0] = 400;
  run.observe(20, original, filtered, baseline, status, true);
  filtered[0] = 380;
  run.observe(40, original, filtered, baseline, status, true);
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason != calibration_validation::FailureTouchDetected);
  filtered[0] = 400;

  run.start(1, 200, 0, 44);
  baseline[47] = 0;
  run.observe(0, original, filtered, baseline, status, true);
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureHardwareMissing);
  baseline[47] = 400;

  run.start(1, 200, 0, 45);
  for (uint8_t sample = 0; sample < 10; ++sample) {
    filtered[47] = sample == 1 ? 412 : 400;
    run.observe(sample * 20, original, filtered, baseline, status, true);
  }
  filtered[47] = 400;
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureNoisy ||
         reason == calibration_validation::FailureTouchDetected);
  filtered[47] = 400;

  run.start(1, 100, 0, 46);
  for (uint8_t sample = 0; sample < 5; ++sample) {
    run.observe(sample * 20, original, filtered, baseline, status, true);
  }
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureTimeout);
  run.cancel();
  assert(!run.active() && !run.due(200) && !run.complete(200));
  run.start(1, 200, 0, 47);
  run.observe(0, original, filtered, baseline, status, false);
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureHardwareMissing);
  run.start(1, 200, 0, 48);
  status[1] |= 8;
  run.observe(0, original, filtered, baseline, status, true);
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureI2cError);
  status[1] &= static_cast<uint8_t>(~8U);

  // 瞬时无效读数（filtered/baseline 为 0）必须被丢弃而不是立刻让整次校准失败：
  // 只有连续无效采样达到 MaxTransientSamples 才算硬件缺失。这是「校准经常不工作」的核心修复。
  run.start(1, 200, 0, 49);
  for (uint8_t sample = 0; sample < 8; ++sample) {
    const bool glitch = sample == 2 || sample == 5;
    filtered[10] = glitch ? 0 : 400;
    baseline[10] = glitch ? 0 : 400;
    run.observe(sample * 20, original, filtered, baseline, status, true);
  }
  filtered[10] = 400;
  baseline[10] = 400;
  // 6 个有效样本少于 MinimumSamples=10，因此是 Timeout 而不是 HardwareMissing：
  // 关键在于两次 0 读数没有把失败原因污染成 HardwareMissing。
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureTimeout);

  // 连续无效采样超过阈值才判硬件缺失。
  run.start(1, 200, 0, 50);
  for (uint8_t sample = 0;
       sample < calibration_validation::MaxTransientSamples; ++sample) {
    filtered[10] = 0;
    run.observe(sample * 20, original, filtered, baseline, status, true);
  }
  filtered[10] = 400;
  assert(!run.finish(original, &candidate, acceptedBaselines, &reason));
  assert(reason == calibration_validation::FailureHardwareMissing);
}

static void checkVector(uint8_t key, const uint8_t expected[7]) {
  bool keys[protocol::KeyCount] = {};
  keys[key] = true;
  uint8_t frame[protocol::LegacyFrameLength] = {};
  protocol::encodeLegacyGameFrame(keys, frame);
  assert(frame[0] == '(' && frame[8] == ')');
  assert(memcmp(frame + 1, expected, 7) == 0);
}

static void checkAllLegacyVectors() {
  const uint8_t expected[protocol::KeyCount][7] = {
      {0, 0, 0, 0, 0, 0, 8},  {0, 0, 0, 0, 0, 0, 4},  {0, 0, 0, 0, 0, 0, 2},
      {0, 0, 0, 0, 0, 0, 1},  {0, 0, 0, 0, 0, 0x10, 0}, {0, 0, 0, 0, 0, 8, 0},
      {0, 0, 0, 0, 0, 4, 0},  {0, 0, 0, 0, 0, 2, 0},  {0, 0, 0, 0, 0, 1, 0},
      {0, 0, 0, 0, 0x10, 0, 0}, {0, 0, 0, 0, 8, 0, 0},  {0, 0, 0, 0, 4, 0, 0},
      {0, 0, 0, 0, 2, 0, 0},  {0, 0, 0, 0, 1, 0, 0},  {0, 0, 0, 0x10, 0, 0, 0},
      {0, 0, 0, 8, 0, 0, 0},  {0, 0, 0, 4, 0, 0, 0},  {0, 0, 0, 2, 0, 0, 0},
      {0, 0, 0, 1, 0, 0, 0},  {0, 0, 0x10, 0, 0, 0, 0}, {0, 0, 8, 0, 0, 0, 0},
      {0, 0, 4, 0, 0, 0, 0},  {0, 0, 2, 0, 0, 0, 0},  {0, 0, 1, 0, 0, 0, 0},
      {0, 0x10, 0, 0, 0, 0, 0}, {0, 8, 0, 0, 0, 0, 0}, {0, 4, 0, 0, 0, 0, 0},
      {0, 2, 0, 0, 0, 0, 0},  {0, 1, 0, 0, 0, 0, 0},  {0x10, 0, 0, 0, 0, 0, 0},
      {8, 0, 0, 0, 0, 0, 0},  {4, 0, 0, 0, 0, 0, 0},  {2, 0, 0, 0, 0, 0, 0},
      {1, 0, 0, 0, 0, 0, 0},
  };
  for (uint8_t key = 0; key < protocol::KeyCount; ++key) {
    checkVector(key, expected[key]);
  }
}

static void checkProtocolBoundaries() {
  assert(protocol::isSupportedVersion(0x10));
  assert(protocol::isSupportedVersion(0x1F));
  assert(!protocol::isSupportedVersion(0x20));
  assert(protocol::validPayloadLength(protocol::TypeCalibrate, 3));
  assert(!protocol::validPayloadLength(protocol::TypeCalibrate, 2));
  assert(protocol::validPayloadLength(protocol::TypeStreamStart, 2));
  assert(!protocol::validPayloadLength(protocol::TypeStreamStart, 3));

  uint8_t frame[protocol::MaxFrameLength] = {};
  uint16_t length = 0;
  assert(protocol::encodeFrame(protocol::TypeVersion, 0, nullptr, 0, frame, sizeof(frame),
                               &length));
  protocol::Frame decoded = {};
  protocol::FrameParser parser;
  for (uint16_t i = 0; i < length; ++i) {
    const protocol::FrameResult result = parser.feed(frame[i], i, &decoded);
    if (i + 1 == length) assert(result == protocol::FrameResult::Complete);
  }
  assert(!parser.active());

  parser.reset();
  assert(parser.feed(protocol::Sync0, 10, &decoded) == protocol::FrameResult::None);
  assert(parser.active());
  assert(parser.feed(protocol::Sync1, 260, &decoded) == protocol::FrameResult::None);
  assert(!parser.active());
  for (uint16_t i = 0; i < length; ++i) {
    const protocol::FrameResult result = parser.feed(frame[i], 300 + i, &decoded);
    if (i + 1 == length) assert(result == protocol::FrameResult::Complete);
  }

  uint8_t badCrc[protocol::MaxFrameLength] = {};
  memcpy(badCrc, frame, length);
  badCrc[length - 1] ^= 0x01;
  parser.reset();
  for (uint16_t i = 0; i < length; ++i) {
    const protocol::FrameResult result = parser.feed(badCrc[i], 400 + i, &decoded);
    if (i + 1 == length) assert(result == protocol::FrameResult::BadCrc);
  }
  for (uint16_t i = 0; i < length; ++i) {
    const protocol::FrameResult result = parser.feed(frame[i], 500 + i, &decoded);
    if (i + 1 == length) assert(result == protocol::FrameResult::Complete);
  }

  const uint8_t oversizedHeader[] = {protocol::Sync0, protocol::Sync1, protocol::Version,
                                     protocol::TypeGetConfig, 0, 0, 1, 1};
  parser.reset();
  for (uint8_t i = 0; i < sizeof(oversizedHeader); ++i) {
    const protocol::FrameResult result = parser.feed(oversizedHeader[i], 600 + i, &decoded);
    if (i + 1 == sizeof(oversizedHeader)) assert(result == protocol::FrameResult::BadLength);
  }
  const uint8_t invalidHeader[] = {protocol::Sync0, protocol::Sync1, 0x20,
                                   protocol::TypeGetConfig, 0x34, 0x12, 0, 0};
  parser.reset();
  for (uint8_t i = 0; i < sizeof(invalidHeader); ++i) {
    const protocol::FrameResult result = parser.feed(invalidHeader[i], 700 + i, &decoded);
    if (i + 1 == sizeof(invalidHeader)) assert(result == protocol::FrameResult::BadVersion);
  }

  assert(firmware_state::classifySequence(false, 7, 1, 2, 7, 1, 2) ==
         firmware_state::SequenceDecision::Execute);
  assert(firmware_state::classifySequence(true, 7, 1, 2, 7, 1, 2) ==
         firmware_state::SequenceDecision::Replay);
  assert(firmware_state::classifySequence(true, 7, 1, 3, 7, 1, 2) ==
         firmware_state::SequenceDecision::Conflict);
  assert(firmware_state::classifySequence(true, 7, 2, 2, 7, 1, 2) ==
         firmware_state::SequenceDecision::Conflict);
  assert(firmware_state::classifySequence(true, 0, 1, 2, 0xFFFF, 1, 2) ==
         firmware_state::SequenceDecision::Execute);
}

static void checkConfigurationAndState() {
  config::Config value = config::defaults();
  assert(config::validate(value));
  value.boards[0].baseThreshold = 0;
  assert(!config::validate(value));
  value = config::defaults();
  value.boards[0].touchDebounce = 8;
  assert(!config::validate(value));
  value = config::defaults();
  value.boards[0].releaseDebounce = 8;
  assert(!config::validate(value));
  value = config::defaults();
  value.boards[0].sfi = 4;
  assert(!config::validate(value));
  value = config::defaults();
  value.boards[0].esi = 8;
  assert(!config::validate(value));
  value = config::defaults();
  value.boards[0].sensOffset = 127;
  value.electrodeOffsets[0] = 127;
  assert(config::validate(value));
  assert(config::touchDelta(value, 0, 127) == 274);
  value.boards[0].baseThreshold = 255;
  value.boards[0].sensOffset = 127;
  assert(config::touchDelta(value, 0, 127) == 509);
  assert(config::touchDelta(value, 99, 0) == 1);
  assert(config::releaseDelta(1, 4) == 0);
  assert(config::releaseDelta(20, 4) == 16);

  assert(firmware_state::statusForRequest(firmware_state::Mode::Game,
                                          protocol::TypeGetConfig) == protocol::StatusOk);
  assert(firmware_state::statusForRequest(firmware_state::Mode::ConfigIdle,
                                          protocol::TypeStreamStop) == protocol::StatusIllegalState);
  assert(firmware_state::statusForRequest(firmware_state::Mode::Streaming,
                                          protocol::TypeStreamStop) == protocol::StatusOk);
  assert(firmware_state::statusForRequest(firmware_state::Mode::Streaming,
                                          protocol::TypeGetConfig) == protocol::StatusBusy);
  assert(firmware_state::statusForRequest(firmware_state::Mode::Calibrating,
                                          protocol::TypeVersion) == protocol::StatusOk);
  assert(firmware_state::statusForRequest(firmware_state::Mode::Calibrating,
                                          protocol::TypeApplyConfig) == protocol::StatusBusy);
}

static void checkDebugPayload() {
  bool keys[protocol::KeyCount] = {};
  keys[0] = true;
  keys[33] = true;
  uint16_t filtered[touchmap::ChannelCount] = {};
  uint16_t baseline[touchmap::ChannelCount] = {};
  for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) {
    filtered[i] = static_cast<uint16_t>(0x100 + i);
    baseline[i] = static_cast<uint16_t>(0x200 + i);
  }
  const uint8_t status[touchmap::BoardCount] = {1, 3, 7, 15};
  uint8_t payload[firmware_state::DebugPayloadLength] = {};
  assert(firmware_state::encodeDebugPayload(0x78563412, keys, filtered, baseline, status,
                                            0xABCD, payload, sizeof(payload)));
  assert(payload[0] == 0x12 && payload[1] == 0x34 && payload[2] == 0x56 && payload[3] == 0x78);
  assert(payload[4] == 1 && payload[5] == 0 && payload[6] == 0 && payload[7] == 0 &&
         payload[8] == 2);
  assert(payload[9] == 0 && payload[10] == 1);
  assert(payload[9 + 47 * 2] == 47 + 0x00 && payload[10 + 47 * 2] == 1);
  assert(payload[105] == 0 && payload[106] == 2);
  assert(payload[105 + 47 * 2] == 47 && payload[106 + 47 * 2] == 2);
  assert(memcmp(payload + 201, status, sizeof(status)) == 0);
  assert(payload[205] == 0xCD && payload[206] == 0xAB);
  assert(!firmware_state::encodeDebugPayload(0, keys, filtered, baseline, status, 0, payload, 206));
}

static void checkDebugPayloadKeyOrder() {
  bool keys[protocol::KeyCount] = {};
  keys[0] = true;
  keys[33] = true;
  uint16_t filteredValues[touchmap::ChannelCount] = {};
  uint16_t baselineValues[touchmap::ChannelCount] = {};
  const uint8_t status[touchmap::BoardCount] = {};
  uint8_t payload[firmware_state::DebugPayloadLength] = {};
  assert(firmware_state::encodeDebugPayload(0, keys, filteredValues, baselineValues, status,
                                            0, payload, sizeof(payload)));
  assert(payload[4] == 1 && payload[5] == 0 && payload[6] == 0 && payload[7] == 0 &&
         payload[8] == 2);
}

static void checkTouchStateBoundaries() {
  Mpr121Hardware hardware;
  assert(hardware.begin());
  config::Config value = config::defaults();
  const touchmap::KeyMapping mapping = touchmap::Keys[0];
  const uint16_t baseline = 400;
  const uint16_t delta = config::touchDelta(value, mapping.board, value.electrodeOffsets[0]);
  const uint16_t release = config::releaseDelta(delta, mpr121_validation::SoftwareHysteresis);
  hardware.setHostSample(mapping.board, mapping.channel, baseline - delta, baseline);
  TouchState state;
  state.reset();
  state.update(hardware, value);
  assert(!state.keys()[0]);
  state.update(hardware, value);
  assert(state.keys()[0]);
  hardware.setHostSample(mapping.board, mapping.channel, baseline - release - 1, baseline);
  state.update(hardware, value);
  assert(state.keys()[0]);
  hardware.setHostSample(mapping.board, mapping.channel, baseline - release, baseline);
  state.update(hardware, value);
  assert(state.keys()[0]);
  state.update(hardware, value);
  assert(!state.keys()[0]);
}

int main() {
  const uint8_t crcInput[] = "123456789";
  assert(protocol::crc16Ccitt(crcInput, 9) == 0x29B1);
  uint8_t frame[protocol::MaxFrameLength] = {};
  uint16_t frameLength = 0;
  uint8_t payload[256] = {};
  assert(protocol::encodeFrame(0x01, 0, payload, 256, frame, sizeof(frame), &frameLength));
  assert(frameLength == 266);
  assert(!protocol::encodeFrame(0x01, 0, payload, 257, frame, sizeof(frame), &frameLength));
  protocol::Frame decoded = {};
  assert(protocol::decodeFrame(frame, frameLength, &decoded) == protocol::FrameResult::Complete);
  assert(decoded.length == 256);
  frame[6] = 0;
  frame[7] = 0;
  assert(protocol::decodeFrame(frame, frameLength, &decoded) == protocol::FrameResult::BadLength);
  frame[6] = 0;
  frame[7] = 1;
  frame[frameLength - 1] ^= 1;
  assert(protocol::decodeFrame(frame, frameLength, &decoded) == protocol::FrameResult::BadCrc);
  frame[frameLength - 1] ^= 1;

  uint8_t minorFrame[protocol::MaxFrameLength] = {};
  memcpy(minorFrame, frame, frameLength);
  minorFrame[2] = 0x11;
  const uint16_t minorCrc = protocol::crc16Ccitt(minorFrame + 2, frameLength - 4);
  minorFrame[frameLength - 2] = static_cast<uint8_t>(minorCrc);
  minorFrame[frameLength - 1] = static_cast<uint8_t>(minorCrc >> 8);
  assert(protocol::decodeFrame(minorFrame, frameLength, &decoded) == protocol::FrameResult::Complete);

  protocol::FrameParser parser;
  for (uint16_t i = 0; i < frameLength; ++i) {
    const protocol::FrameResult result = parser.feed(frame[i], i, &decoded);
    if (i + 1 == frameLength) assert(result == protocol::FrameResult::Complete);
  }
  parser.reset();
  assert(parser.feed(protocol::Sync0, 0, &decoded) == protocol::FrameResult::None);
  assert(parser.feed(protocol::Sync0, 1, &decoded) == protocol::FrameResult::None);
  assert(parser.feed(protocol::Sync1, 2, &decoded) == protocol::FrameResult::None);
  assert(parser.feed(0x10, 300, &decoded) == protocol::FrameResult::None);

  parser.reset();
  assert(parser.feed(protocol::Sync0, 0, &decoded) == protocol::FrameResult::None);
  assert(parser.feed(protocol::Sync0, 250, &decoded) == protocol::FrameResult::None);
  assert(parser.feed(protocol::Sync1, 251, &decoded) == protocol::FrameResult::None);
  uint8_t shortFrame[16] = {};
  uint16_t shortLength = 0;
  assert(protocol::encodeFrame(protocol::TypeVersion, 0x44, nullptr, 0,
                               shortFrame, sizeof(shortFrame), &shortLength));
  for (uint16_t i = 2; i < shortLength; ++i) {
    const protocol::FrameResult result = parser.feed(shortFrame[i], 251 + i - 1, &decoded);
    if (i + 1 == shortLength) assert(result == protocol::FrameResult::Complete);
  }

  parser.reset();
  const uint8_t badHeader[] = {protocol::Sync0, protocol::Sync1, 0x20, 0x77,
                               0x34, 0x12, 0, 0};
  for (uint8_t i = 0; i < sizeof(badHeader); ++i) {
    const protocol::FrameResult result = parser.feed(badHeader[i], 0, &decoded);
    if (i == sizeof(badHeader) - 1) {
      assert(result == protocol::FrameResult::BadVersion);
      assert(decoded.type == 0x77 && decoded.sequence == 0x1234);
    }
  }
  assert(protocol::isKnownRequestType(protocol::TypeApplyConfig));
  assert(!protocol::isKnownRequestType(0x7E));
  assert(protocol::validPayloadLength(protocol::TypeSetConfig, config::BlobLength));
  assert(!protocol::validPayloadLength(protocol::TypeSetConfig,
                                       config::BlobLength - 1));

  checkCalibration();
  checkProtocolBoundaries();
  checkConfigurationAndState();
  checkDebugPayloadKeyOrder();
  checkDebugPayload();
  checkTouchStateBoundaries();

  config::Config value = config::defaults();
  uint8_t blob[config::BlobLength] = {};
  assert(config::serialize(value, blob, sizeof(blob)));
  assert(sizeof(blob) == 93 && blob[0] == config::Schema);
  config::Config roundTrip = {};
  assert(config::deserialize(blob, sizeof(blob), &roundTrip));
  assert(roundTrip.boards[0].baseThreshold == 20);
  for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
    assert(roundTrip.keyMap[key] == touchmap::DefaultKeyMap[key]);
  }
  blob[0] = 3;
  assert(!config::deserialize(blob, sizeof(blob), &roundTrip));
  blob[0] = config::Schema;
  blob[1 + 1] = 0x80;
  assert(!config::deserialize(blob, sizeof(blob), &roundTrip));

  // keyMap 校验：非法 board/channel 与重复映射都必须被拒绝。
  config::Config mapping = config::defaults();
  assert(config::validate(mapping));
  mapping = config::defaults();
  mapping.keyMap[0] = touchmap::PackMapping(4, 0);
  assert(!config::validate(mapping));
  mapping = config::defaults();
  mapping.keyMap[0] = touchmap::PackMapping(0, 12);
  assert(!config::validate(mapping));
  mapping = config::defaults();
  mapping.keyMap[0] = mapping.keyMap[1];
  assert(!config::validate(mapping));

  // 通道 ↔ 按键 反向查找与默认焊接顺序一致。
  config::Config defaults = config::defaults();
  assert(config::keyForChannel(defaults, 0, 0) == 7);
  assert(config::keyForChannel(defaults, 3, 4) == 0);
  assert(config::keyForChannel(defaults, 0, 9) == -1);
  assert(config::electrodeOffsetForChannel(defaults, 0, 9) == 0);
  assert(config::electrodeOffsetForChannel(defaults, 0, 0) ==
         defaults.electrodeOffsets[7]);

  // 重映射：把 A1（key 0）改到 board 0/channel 8，阈值方向随新通道走。
  config::Config remapped = config::defaults();
  remapped.keyMap[0] = touchmap::PackMapping(0, 8);
  remapped.electrodeOffsets[0] = -8;
  assert(config::validate(remapped));
  assert(config::electrodeOffsetForChannel(remapped, 0, 8) == -8);
  assert(config::keyForChannel(remapped, 0, 8) == 0);
  assert(firmware_state::thresholdForChannel(remapped, 0, 8) ==
         static_cast<uint16_t>(20 - 8));
  {
    const uint16_t remappedTouched[touchmap::BoardCount] = {1u << 8, 0, 0, 0};
    TouchState state;
    state.reset();
    state.applyHardware(remapped, remappedTouched);
    assert(state.keys()[0]);
    assert(!state.keys()[1]);
    // 同一触摸位在默认映射下不再是 A1（默认 A1 在 board3/channel4）。
    state.applyHardware(defaults, remappedTouched);
    assert(!state.keys()[0]);
  }

  uint8_t hardwareThreshold = 0;
  assert(mpr121_validation::toHardwareThreshold(1, &hardwareThreshold) &&
         hardwareThreshold == 1);
  assert(mpr121_validation::toHardwareThreshold(4, &hardwareThreshold) &&
         hardwareThreshold == 1);
  assert(mpr121_validation::toHardwareThreshold(5, &hardwareThreshold) &&
         hardwareThreshold == 2);
  assert(mpr121_validation::toHardwareThreshold(1020, &hardwareThreshold) &&
         hardwareThreshold == 255);
  assert(!mpr121_validation::toHardwareThreshold(1021, &hardwareThreshold));
  assert(!mpr121_validation::toHardwareThreshold(1023, &hardwareThreshold));

  Mpr121Hardware hardware;
  assert(hardware.begin());
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    for (uint8_t channel = 0; channel < 12; ++channel) {
      const uint16_t sample = static_cast<uint16_t>(board * 100 + channel);
      hardware.setHostSample(board, channel, sample, static_cast<uint16_t>(sample + 1));
      assert(hardware.filtered(board, channel) == sample);
      assert(hardware.baseline(board, channel) == sample + 1);
    }
  }
  hardware.recordI2cError(2);
  hardware.recordI2cError(2);
  hardware.recordI2cError(0);
  assert(hardware.i2cErrors() == 3);
  uint16_t hardwareFiltered[touchmap::ChannelCount] = {};
  uint16_t hardwareBaseline[touchmap::ChannelCount] = {};
  uint8_t hardwareStatus[touchmap::BoardCount] = {};
  hardware.snapshot(hardwareFiltered, hardwareBaseline, hardwareStatus);
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    assert((hardwareStatus[board] & 0x07) == 0x07);
    assert((hardwareStatus[board] & 0x08) == ((board == 0 || board == 2) ? 0x08 : 0));
    for (uint8_t channel = 0; channel < 12; ++channel) {
      const uint8_t index = static_cast<uint8_t>(board * 12 + channel);
      const uint16_t sample = static_cast<uint16_t>(board * 100 + channel);
      assert(hardwareFiltered[index] == sample);
      assert(hardwareBaseline[index] == sample + 1);
    }
  }
  memset(hardwareStatus, 0, sizeof(hardwareStatus));
  hardware.snapshot(hardwareFiltered, hardwareBaseline, hardwareStatus);
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    assert((hardwareStatus[board] & 0x08) == 0);
  }

  MemoryEeprom memory;
  assert(eeprom_store::save(memory, value, 1, 0));
  assert(memory.bytes[3] == eeprom_store::CommitMarker);
  config::Config loaded = {};
  uint32_t generation = 0;
  bool persisted = false;
  uint8_t activeSlot = 0xFF;
  assert(eeprom_store::load(memory, &loaded, &generation, &persisted, &activeSlot));
  assert(persisted && generation == 1 && loaded.boards[0].baseThreshold == 20);
  assert(activeSlot == 0);
  assert(eeprom_store::RecordLength + 2 <= eeprom_store::SlotSize);
  assert(eeprom_store::ReservedStart == 105);
  assert(memory.bytes[eeprom_store::SlotSize - 1] == 0xFF);

  assert(eeprom_store::save(memory, value, 2, 1));
  assert(eeprom_store::load(memory, &loaded, &generation, &persisted, &activeSlot));
  assert(persisted && generation == 2 && activeSlot == 1);

  memory.bytes[eeprom_store::RecordLength] ^= 1;
  memory.bytes[eeprom_store::SlotSize + eeprom_store::RecordLength] ^= 1;
  assert(eeprom_store::load(memory, &loaded, &generation, &persisted));
  assert(!persisted && generation == 0);

  // keyMap 变体必须能完整存入 EEPROM 并读回（记录长度 103，双槽仍可容纳）。
  config::Config stored = config::defaults();
  stored.keyMap[0] = touchmap::PackMapping(2, 11);
  stored.keyMap[1] = touchmap::PackMapping(3, 4);
  assert(config::validate(stored));
  MemoryEeprom variant;
  assert(eeprom_store::save(variant, stored, 1, 0));
  config::Config variantLoaded = {};
  assert(eeprom_store::load(variant, &variantLoaded, &generation, &persisted, &activeSlot));
  assert(persisted && variantLoaded.keyMap[0] == stored.keyMap[0] &&
         variantLoaded.keyMap[1] == stored.keyMap[1]);

  checkAllLegacyVectors();
  const uint8_t empty[] = {0, 0, 0, 0, 0, 0, 0};
  bool noKeys[protocol::KeyCount] = {};
  protocol::encodeLegacyGameFrame(noKeys, frame);
  assert(memcmp(frame + 1, empty, 7) == 0);
  bool keys[protocol::KeyCount] = {};
  for (uint8_t i = 0; i < protocol::KeyCount; ++i) keys[i] = true;
  protocol::encodeLegacyGameFrame(keys, frame);
  const uint8_t allPressed[] = {0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x0F};
  assert(memcmp(frame + 1, allPressed, 7) == 0);
  memset(keys, 0, sizeof(keys));
  keys[0] = true;
  keys[33] = true;
  protocol::encodeLegacyGameFrame(keys, frame);
  const uint8_t edgePair[] = {1, 0, 0, 0, 0, 0, 8};
  assert(memcmp(frame + 1, edgePair, 7) == 0);
  memset(keys, 0, sizeof(keys));
  keys[4] = true;
  keys[29] = true;
  protocol::encodeLegacyGameFrame(keys, frame);
  const uint8_t middlePair[] = {0x10, 0, 0, 0, 0, 0x10, 0};
  assert(memcmp(frame + 1, middlePair, 7) == 0);

  uint8_t command[protocol::LegacyCommandLength] = {};
  bool complete = false;
  const uint8_t legacyInput[] = {'{', 'x', 'x', 'r', 'x', '}'};
  for (uint8_t byte : legacyInput) protocol::decodeLegacyCommand(byte, command, &complete);
  assert(complete && command[0] == '{' && command[3] == 'r' && command[5] == '}');
  complete = false;
  const uint8_t malformedInput[] = {'{', 'x', 'x', 'r', 'x', 'q', '}'};
  for (uint8_t byte : malformedInput) protocol::decodeLegacyCommand(byte, command, &complete);
  assert(!complete);
  puts("firmware host checks passed");
  return 0;
}
