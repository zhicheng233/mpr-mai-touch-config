#pragma once

#include <stdint.h>

#include "config.h"
#include "protocol.h"

namespace firmware_state {

enum class Mode : uint8_t { Game = 0, ConfigIdle = 1, Streaming = 2, Calibrating = 3 };
enum class SequenceDecision : uint8_t { Execute, Replay, Conflict };

constexpr uint32_t VersionCapabilities = 0x0FUL;
constexpr uint16_t DebugPayloadLength = 207;

uint8_t statusForRequest(Mode mode, uint8_t requestType);
int8_t electrodeOffsetForChannel(const config::Config& value, uint8_t board,
                                 uint8_t channel);
uint16_t thresholdForChannel(const config::Config& value, uint8_t board,
                             uint8_t channel);
SequenceDecision classifySequence(bool cacheValid, uint16_t sequence, uint8_t type,
                                  uint16_t payloadCrc, uint16_t cachedSequence,
                                  uint8_t cachedType, uint16_t cachedPayloadCrc);
bool encodeDebugPayload(uint32_t timestampMs, const bool* keys,
                        const uint16_t filtered[touchmap::ChannelCount],
                        const uint16_t baseline[touchmap::ChannelCount],
                        const uint8_t status[touchmap::BoardCount], uint16_t i2cErrors,
                        uint8_t* payload, uint16_t payloadCapacity);

}  // namespace firmware_state
