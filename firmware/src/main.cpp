#include <stdint.h>

#ifdef ARDUINO
#include <Arduino.h>
#if defined(ARDUINO_ARCH_SAMD)
#include <FlashAsEEPROM.h>
#else
#include <EEPROM.h>
#endif
#endif

#include "touchmap.h"

namespace mpr121_validation {
constexpr uint8_t ChannelCount = touchmap::ChannelCount;
}

#include "calibration.h"
#include "config.h"
#include "eeprom_store.h"
#include "firmware_state.h"
#include "mpr121.h"
#include "protocol.h"
#include "touch_state.h"

namespace {

enum class FirmwareMode : uint8_t { GAME = 0, CONFIG_IDLE = 1, STREAMING = 2, CALIBRATING = 3 };
constexpr uint16_t RuntimeFrameLength = firmware_state::DebugPayloadLength + 10;

class ArduinoEeprom final : public eeprom_store::Backend {
 public:
  uint8_t read(uint16_t address) const override {
#ifdef ARDUINO
    return EEPROM.read(address);
#else
    (void)address;
    return 0xFF;
#endif
  }
  void write(uint16_t address, uint8_t value) override {
#ifdef ARDUINO
    EEPROM.update(address, value);
#else
    (void)address;
    (void)value;
#endif
  }
  void commit() override {
#if defined(ARDUINO_ARCH_SAMD)
    // SAMD21 用 FlashStorage 的 EEPROM 模拟：写缓冲，commit 才真正写 flash
    EEPROM.commit();
#else
    // AVR 真 EEPROM 立即写，无需 commit
#endif
  }
};

FirmwareMode mode = FirmwareMode::GAME;
config::Config applied = config::defaults();
config::Config staged = applied;
config::Config persistedConfig = applied;
Mpr121Hardware mpr;
Calibration calibration;
bool bootCalibration = false;
ArduinoEeprom eeprom;
uint32_t generation = 0;
bool persisted = false;
uint8_t activeSlot = 0;
bool conditioning = true;
TouchState touchState;
uint8_t frameBuffer[RuntimeFrameLength] = {};
uint8_t responseCache[RuntimeFrameLength] = {};
uint16_t responseCacheLength = 0;
uint16_t cachedSequence = 0;
uint8_t cachedType = 0;
uint16_t cachedPayloadCrc = 0;
bool responseCacheValid = false;
bool cacheResponse = false;
uint32_t lastConfigActivity = 0;
uint32_t nextStreamFrame = 0;
uint16_t streamRate = 0;
uint32_t currentTime = 0;
uint8_t legacyCommand[protocol::LegacyCommandLength] = {};
protocol::FrameParser framedParser;
protocol::Frame receivedFrame = {};

bool sameConfig(const config::Config& left, const config::Config& right) {
  uint8_t leftBlob[config::BlobLength] = {};
  uint8_t rightBlob[config::BlobLength] = {};
  if (!config::serialize(left, leftBlob, sizeof(leftBlob)) ||
      !config::serialize(right, rightBlob, sizeof(rightBlob))) {
    return false;
  }
  for (uint16_t i = 0; i < config::BlobLength; ++i) {
    if (leftBlob[i] != rightBlob[i]) return false;
  }
  return true;
}

uint8_t ackFlags() {
  uint8_t flags = sameConfig(staged, applied) ? 0 : 1;
  if (!persisted || !sameConfig(applied, persistedConfig)) flags |= 2;
  return flags;
}

void sendFrame(uint8_t type, uint16_t sequence, const uint8_t* payload, uint16_t length) {
  uint16_t encodedLength = 0;
  if (protocol::encodeFrame(type, sequence, payload, length, frameBuffer,
                             sizeof(frameBuffer), &encodedLength)) {
#ifdef ARDUINO
    Serial.write(frameBuffer, encodedLength);
#endif
    if (cacheResponse) {
      for (uint16_t i = 0; i < encodedLength; ++i) responseCache[i] = frameBuffer[i];
      responseCacheLength = encodedLength;
      responseCacheValid = true;
      cacheResponse = false;
    }
  }
}

void sendAck(uint8_t requestType, uint16_t sequence, const uint8_t* result = nullptr,
             uint16_t resultLength = 0) {
  if (resultLength > protocol::MaxPayload - 4) return;
  uint8_t payload[protocol::MaxPayload] = {};
  payload[0] = requestType;
  payload[1] = 0;
  payload[2] = static_cast<uint8_t>(mode);
  payload[3] = ackFlags();
  for (uint16_t i = 0; i < resultLength && i < protocol::MaxPayload - 4; ++i) {
    payload[4 + i] = result[i];
  }
  sendFrame(protocol::TypeAck, sequence, payload,
            static_cast<uint16_t>(4 + resultLength));
}

void sendError(uint8_t requestType, uint16_t sequence, uint8_t status) {
  const uint8_t payload[4] = {requestType, status, 0, 0};
  sendFrame(protocol::TypeError, sequence, payload, sizeof(payload));
}

void clearSession() {
  responseCacheValid = false;
  responseCacheLength = 0;
  cacheResponse = false;
  calibration.cancel();
  staged = applied;
  touchState.reset();
}

bool prepareRequest(const protocol::Frame& frame) {
  if (mode == FirmwareMode::GAME) return true;
  const uint16_t payloadCrc = protocol::crc16Ccitt(frame.payload, frame.length);
  const firmware_state::SequenceDecision decision = firmware_state::classifySequence(
      responseCacheValid, frame.sequence, frame.type, payloadCrc, cachedSequence, cachedType,
      cachedPayloadCrc);
  if (decision == firmware_state::SequenceDecision::Replay) {
#ifdef ARDUINO
    Serial.write(responseCache, responseCacheLength);
#endif
    return false;
  }
  if (decision == firmware_state::SequenceDecision::Conflict) {
    sendError(frame.type, frame.sequence, protocol::StatusSequenceConflict);
    return false;
  }
  cachedSequence = frame.sequence;
  cachedType = frame.type;
  cachedPayloadCrc = payloadCrc;
  cacheResponse = true;
  return true;
}

void handleLegacy(const uint8_t command[protocol::LegacyCommandLength]) {
  switch (command[3]) {
    case protocol::LegacyReset:
      mpr.resetAll();
      conditioning = !mpr.startAll(applied);
      break;
    case protocol::LegacyHalt:
      mpr.haltAll();
      conditioning = true;
      break;
    case protocol::LegacyStart:
      conditioning = !mpr.startAll(applied);
      break;
    case protocol::LegacyRatio:
    case protocol::LegacySensitivity:
#ifdef ARDUINO
      Serial.write('(');
      Serial.write(command + 1, 4);
      Serial.write(')');
#endif
      break;
    default:
      break;
  }
}

void handleFrame(const protocol::Frame& frame) {
  if (!protocol::isKnownRequestType(frame.type)) {
    if (mode != FirmwareMode::GAME) sendError(frame.type, frame.sequence, protocol::StatusUnknownType);
    return;
  }
  if (!protocol::validPayloadLength(frame.type, frame.length)) {
    if (mode != FirmwareMode::GAME) sendError(frame.type, frame.sequence, protocol::StatusBadLength);
    return;
  }
  if (!prepareRequest(frame)) return;
  if (mode != FirmwareMode::GAME) lastConfigActivity = currentTime;
  if (frame.type == protocol::TypeVersion && frame.length == 0) {
    uint8_t result[16] = {1, 1, 0, 1, 0, 0x1F, 0, 0, 0, 0, 1, 48, 34, 4,
                          static_cast<uint8_t>(mode), 0};
    sendAck(frame.type, frame.sequence, result, sizeof(result));
    return;
  }
  if (frame.type == protocol::TypeEnterConfig && mode == FirmwareMode::GAME &&
      frame.length == 0) {
    if (bootCalibration) {
      bootCalibration = false;
      calibration.cancel();
    }
    mode = FirmwareMode::CONFIG_IDLE;
    staged = applied;
    // 进入配置模式时清除 conditioning，否则手动校准会被 Hardware 拒绝
    // （setup() 里 conditioning=true，连接时 ENTER_CFG 取消 boot 校准后它一直卡 true）。
    conditioning = !mpr.allOnline();
    lastConfigActivity = currentTime;
    sendAck(frame.type, frame.sequence);
    return;
  }
  if (frame.type == protocol::TypeEnterConfig && mode != FirmwareMode::GAME) {
    sendError(frame.type, frame.sequence, protocol::StatusAlreadyConfigured);
    return;
  }
  if (frame.type == protocol::TypeExitConfig && mode != FirmwareMode::GAME &&
      frame.length == 0) {
    calibration.cancel();
    conditioning = !mpr.startAll(applied);
    mode = FirmwareMode::GAME;
    clearSession();
    sendAck(frame.type, frame.sequence);
    return;
  }
  if (mode == FirmwareMode::GAME) return;
  if (mode == FirmwareMode::STREAMING && frame.type == protocol::TypeStreamStop) {
    mode = FirmwareMode::CONFIG_IDLE;
    sendAck(frame.type, frame.sequence);
    return;
  }
  if (mode == FirmwareMode::STREAMING || mode == FirmwareMode::CALIBRATING) {
    if (frame.type == protocol::TypeVersion) {
      uint8_t result[16] = {1, 1, 0, 1, 0, 0x1F, 0, 0, 0, 0, 1, 48, 34, 4,
                            static_cast<uint8_t>(mode), 0};
      sendAck(frame.type, frame.sequence, result, sizeof(result));
      return;
    }
    sendError(frame.type, frame.sequence, protocol::StatusBusy);
    return;
  }
  if (mode == FirmwareMode::CONFIG_IDLE && frame.type == protocol::TypeStreamStop) {
    sendError(frame.type, frame.sequence, protocol::StatusIllegalState);
    return;
  }
  switch (frame.type) {
    case protocol::TypeGetConfig: {
      if (frame.length != 0) {
        sendError(frame.type, frame.sequence, protocol::StatusBadLength);
        return;
      }
      uint8_t result[config::BlobLength] = {};
      if (!config::serialize(applied, result, sizeof(result))) {
        sendError(frame.type, frame.sequence, protocol::StatusInvalidValue);
        return;
      }
      sendAck(frame.type, frame.sequence, result, sizeof(result));
      return;
    }
    case protocol::TypeSetConfig: {
      if (frame.length != config::BlobLength) {
        sendError(frame.type, frame.sequence, protocol::StatusBadLength);
        return;
      }
      config::Config candidate = {};
      if (!config::deserialize(frame.payload, frame.length, &candidate)) {
        sendError(frame.type, frame.sequence, protocol::StatusInvalidValue);
        return;
      }
      staged = candidate;
      sendAck(frame.type, frame.sequence);
      return;
    }
    case protocol::TypeSaveConfig: {
      if (frame.length != 0) {
        sendError(frame.type, frame.sequence, protocol::StatusBadLength);
        return;
      }
      const uint8_t targetSlot = persisted ? static_cast<uint8_t>(activeSlot ^ 1U) : 0;
      if (!eeprom_store::save(eeprom, applied, generation + 1, targetSlot)) {
        sendError(frame.type, frame.sequence, protocol::StatusStorage);
        return;
      }
      ++generation;
      activeSlot = targetSlot;
      persisted = true;
      persistedConfig = applied;
      sendAck(frame.type, frame.sequence);
      return;
    }
    case protocol::TypeApplyConfig: {
      if (!mpr.applyConfig(staged)) {
        mpr.applyConfig(applied);
        sendError(frame.type, frame.sequence, protocol::StatusHardware);
        return;
      }
      applied = staged;
      staged = applied;
      conditioning = false;
      sendAck(frame.type, frame.sequence);
      return;
    }
    case protocol::TypeCalibrate: {
      if (frame.payload[0] > 1 ||
          (static_cast<uint16_t>(frame.payload[1]) |
           (static_cast<uint16_t>(frame.payload[2]) << 8)) > 60000) {
        sendError(frame.type, frame.sequence, protocol::StatusInvalidValue);
        return;
      }
      if (!mpr.allOnline() || conditioning) {
        sendError(frame.type, frame.sequence, protocol::StatusHardware);
        return;
      }
      // Keep electrodes enabled so MPR121 baseline tracking continues during sampling.
      calibration.start(frame.payload[0], static_cast<uint16_t>(frame.payload[1]) |
                        (static_cast<uint16_t>(frame.payload[2]) << 8), currentTime,
                        frame.sequence);
      mode = FirmwareMode::CALIBRATING;
      uint8_t deadline[4] = {};
      const uint32_t duration = frame.payload[1] == 0 && frame.payload[2] == 0
                                    ? mpr121_validation::DefaultCalibrationWindowMs
                                    : static_cast<uint32_t>(frame.payload[1]) |
                                      (static_cast<uint32_t>(frame.payload[2]) << 8);
      const uint32_t deadlineMs = duration + 250;
      deadline[0] = static_cast<uint8_t>(deadlineMs);
      deadline[1] = static_cast<uint8_t>(deadlineMs >> 8);
      deadline[2] = static_cast<uint8_t>(deadlineMs >> 16);
      deadline[3] = static_cast<uint8_t>(deadlineMs >> 24);
      sendAck(frame.type, frame.sequence, deadline, sizeof(deadline));
      return;
    }
    case protocol::TypeStreamStart: {
      const uint16_t requested = static_cast<uint16_t>(frame.payload[0]) |
                                 (static_cast<uint16_t>(frame.payload[1]) << 8);
      const uint16_t accepted = requested == 0 ? 100 : requested;
      if (accepted == 0 || accepted > mpr121_validation::MaxStreamRateHz ||
          !mpr.startAll(applied)) {
        sendError(frame.type, frame.sequence, protocol::StatusHardware);
        return;
      }
      streamRate = accepted;
      nextStreamFrame = currentTime;
      mode = FirmwareMode::STREAMING;
      uint8_t rates[4] = {static_cast<uint8_t>(accepted), static_cast<uint8_t>(accepted >> 8),
                          static_cast<uint8_t>(accepted), static_cast<uint8_t>(accepted >> 8)};
      sendAck(frame.type, frame.sequence, rates, sizeof(rates));
      return;
    }
    case protocol::TypeStreamStop:
      sendAck(frame.type, frame.sequence);
      mode = FirmwareMode::CONFIG_IDLE;
      return;
    default:
      sendError(frame.type, frame.sequence, protocol::StatusUnknownType);
        return;
  }
}

void loopGame() {
#ifdef ARDUINO
  if (conditioning) return;
  uint16_t touched[touchmap::BoardCount] = {};
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) touched[board] = mpr.touched(board);
  touchState.applyHardware(applied, touched);
  uint8_t output[protocol::LegacyFrameLength];
  protocol::encodeLegacyGameFrame(touchState.keys(), output);
  Serial.write(output, sizeof(output));
#endif
}

void sendDebugFrame(uint32_t nowMs) {
  uint8_t payload[firmware_state::DebugPayloadLength] = {};
  uint16_t filtered[48] = {};
  uint16_t baseline[48] = {};
  uint8_t status[4] = {};
  uint16_t touched[touchmap::BoardCount] = {};
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) touched[board] = mpr.touched(board);
  mpr.snapshot(filtered, baseline, status);
  touchState.applyHardware(applied, touched);
  firmware_state::encodeDebugPayload(nowMs, touchState.keys(), filtered, baseline, status,
                                     mpr.i2cErrors(), payload, sizeof(payload));
  sendFrame(protocol::TypeDebugFrame, 0, payload, sizeof(payload));
}

}  // namespace

#ifdef ARDUINO
void setup() {
  Serial.begin(115200);
  eeprom_store::load(eeprom, &applied, &generation, &persisted, &activeSlot);
  staged = applied;
  persistedConfig = applied;
  const bool hardwareReady = mpr.begin() && mpr.startAll(applied);
  bootCalibration = hardwareReady;
  if (bootCalibration) calibration.start(0, 0, millis(), 0);
  conditioning = true;
  touchState.reset();
  mode = FirmwareMode::GAME;
}

void loop() {
  currentTime = millis();
  if (mode != FirmwareMode::GAME && !Serial) {
    const bool hardwareReady = mpr.startAll(applied);
    mode = FirmwareMode::GAME;
    clearSession();
    conditioning = !hardwareReady;
  }
  if (bootCalibration || mode == FirmwareMode::CALIBRATING) {
    if (calibration.due(currentTime)) {
      uint16_t filtered[touchmap::ChannelCount] = {};
      uint16_t baseline[touchmap::ChannelCount] = {};
      uint8_t status[touchmap::BoardCount] = {};
      mpr.snapshot(filtered, baseline, status);
      calibration.observe(currentTime, applied, filtered, baseline, status, mpr.allOnline());
    }
  }
  while (Serial.available() > 0) {
    const uint8_t value = static_cast<uint8_t>(Serial.read());
    const protocol::FrameResult frameResult =
        framedParser.feed(value, currentTime, &receivedFrame);
    if (frameResult == protocol::FrameResult::Complete) {
      handleFrame(receivedFrame);
      continue;
    }
    if (frameResult != protocol::FrameResult::None) {
      if (mode != FirmwareMode::GAME) {
        const uint8_t type = receivedFrame.type;
        sendError(type, receivedFrame.sequence, protocol::StatusBadFrame);
      }
      continue;
    }
    if (mode == FirmwareMode::GAME && !framedParser.active()) {
      bool complete = false;
      if (protocol::decodeLegacyCommand(value, legacyCommand, &complete) && complete) {
        handleLegacy(legacyCommand);
      }
    }
  }
  if (mode == FirmwareMode::GAME && !bootCalibration) loopGame();
  if (mode == FirmwareMode::STREAMING && currentTime >= nextStreamFrame) {
    sendDebugFrame(currentTime);
    const uint32_t period = streamRate == 0 ? 10 : 1000UL / streamRate;
    nextStreamFrame = currentTime + (period == 0 ? 1 : period);
  }
  if ((bootCalibration || mode == FirmwareMode::CALIBRATING) &&
      calibration.complete(currentTime)) {
    uint8_t result[196] = {};
    uint16_t baseline[touchmap::ChannelCount] = {};
    config::Config candidate = applied;
    uint8_t failureReason = calibration_validation::FailureNone;
    Calibration::Diagnostics diagnostics;
    const bool valid =
        calibration.finish(applied, &candidate, baseline, &failureReason, &diagnostics);
    const auto writeHardware = [](void* context, const config::Config& value) -> bool {
      return static_cast<Mpr121Hardware*>(context)->startAll(value);
    };
    if (!valid || !calibration_validation::apply(candidate, &applied, &staged,
                                                 writeHardware, &mpr)) {
      if (valid) failureReason = calibration_validation::FailureI2cError;
      if (!valid) conditioning = !mpr.startAll(applied);
      else conditioning = !mpr.allOnline();
      // 失败帧：status,reason,reserved:u16 之后跟一个可选诊断块。
      // 结构：count:u8, 然后 count × { channel:u8, magnitude:u8 }。
      // count=0 时长度为 4（与原协议一致）；工具端按长度容忍。
      const uint8_t diagnosticCount = valid ? 0 : diagnostics.count;
      const uint16_t diagnosticLength =
          static_cast<uint16_t>(4 + 1 + diagnosticCount * 2);
      result[0] = 1;
      result[1] = failureReason;
      result[2] = 0;
      result[3] = 0;
      result[4] = diagnosticCount;
      for (uint8_t i = 0; i < diagnosticCount; ++i) {
        result[5 + i * 2] = diagnostics.channel[i];
        result[6 + i * 2] = diagnostics.magnitude[i];
      }
      if (!bootCalibration)
        sendFrame(protocol::TypeCalibrationDone, calibration.sequence(), result,
                  diagnosticLength);
    } else {
      conditioning = false;
      for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) {
        result[4 + i * 2] = static_cast<uint8_t>(baseline[i]);
        result[5 + i * 2] = static_cast<uint8_t>(baseline[i] >> 8);
        const uint8_t board = static_cast<uint8_t>(i / 12);
        const uint16_t threshold = firmware_state::thresholdForChannel(candidate, board, i % 12);
        result[100 + i * 2] = static_cast<uint8_t>(threshold);
        result[101 + i * 2] = static_cast<uint8_t>(threshold >> 8);
      }
      if (!bootCalibration) {
        sendFrame(protocol::TypeCalibrationDone, calibration.sequence(), result, sizeof(result));
      }
    }
    calibration.cancel();
    if (bootCalibration) {
      bootCalibration = false;
    } else {
      mode = FirmwareMode::CONFIG_IDLE;
      lastConfigActivity = currentTime;
    }
  }
  if (mode == FirmwareMode::CONFIG_IDLE &&
      static_cast<uint32_t>(currentTime - lastConfigActivity) >= 30000UL) {
    const bool hardwareReady = mpr.startAll(applied);
    mode = FirmwareMode::GAME;
    clearSession();
    conditioning = !hardwareReady;
  }
}
#endif
