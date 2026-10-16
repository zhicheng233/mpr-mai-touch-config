#include "firmware_state.h"

#include "touchmap.h"

namespace firmware_state {

uint8_t statusForRequest(Mode mode, uint8_t requestType) {
  if (requestType == protocol::TypeVersion) return protocol::StatusOk;
  if (requestType == protocol::TypeExitConfig) {
    return mode == Mode::Game ? protocol::StatusOk : protocol::StatusOk;
  }
  if (mode == Mode::Game) return protocol::StatusOk;
  if (mode == Mode::Streaming) {
    return requestType == protocol::TypeStreamStop ? protocol::StatusOk
                                                     : protocol::StatusBusy;
  }
  if (mode == Mode::Calibrating) return protocol::StatusBusy;
  if (requestType == protocol::TypeStreamStop) return protocol::StatusIllegalState;
  return protocol::StatusOk;
}

int8_t electrodeOffsetForChannel(const config::Config& value, uint8_t board,
                                 uint8_t channel) {
  return config::electrodeOffsetForChannel(value, board, channel);
}

uint16_t thresholdForChannel(const config::Config& value, uint8_t board,
                             uint8_t channel) {
  return config::touchDelta(
      value, board, config::electrodeOffsetForChannel(value, board, channel));
}

SequenceDecision classifySequence(bool cacheValid, uint16_t sequence, uint8_t type,
                                  uint16_t payloadCrc, uint16_t cachedSequence,
                                  uint8_t cachedType, uint16_t cachedPayloadCrc) {
  if (!cacheValid || sequence != cachedSequence) return SequenceDecision::Execute;
  return type == cachedType && payloadCrc == cachedPayloadCrc
             ? SequenceDecision::Replay
             : SequenceDecision::Conflict;
}

bool encodeDebugPayload(uint32_t timestampMs, const bool* keys,
                        const uint16_t filtered[touchmap::ChannelCount],
                        const uint16_t baseline[touchmap::ChannelCount],
                        const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                        uint8_t* payload, uint16_t payloadCapacity) {
  if (keys == nullptr || filtered == nullptr || baseline == nullptr || status == nullptr ||
      payload == nullptr || payloadCapacity < DebugPayloadLength) {
    return false;
  }
  for (uint16_t i = 0; i < DebugPayloadLength; ++i) payload[i] = 0;
  payload[0] = static_cast<uint8_t>(timestampMs);
  payload[1] = static_cast<uint8_t>(timestampMs >> 8);
  payload[2] = static_cast<uint8_t>(timestampMs >> 16);
  payload[3] = static_cast<uint8_t>(timestampMs >> 24);
  const uint64_t bits = protocol::packKeyBits(keys);
  for (uint8_t i = 0; i < 5; ++i) payload[4 + i] = static_cast<uint8_t>(bits >> (i * 8));
  for (uint8_t i = 0; i < touchmap::ChannelCount; ++i) {
    payload[9 + i * 2] = static_cast<uint8_t>(filtered[i]);
    payload[10 + i * 2] = static_cast<uint8_t>(filtered[i] >> 8);
    payload[105 + i * 2] = static_cast<uint8_t>(baseline[i]);
    payload[106 + i * 2] = static_cast<uint8_t>(baseline[i] >> 8);
  }
  for (uint8_t i = 0; i < touchmap::BoardCount; ++i) payload[201 + i] = status[i];
  payload[205] = static_cast<uint8_t>(i2cErrors);
  payload[206] = static_cast<uint8_t>(i2cErrors >> 8);
  return true;
}

}  // namespace firmware_state
