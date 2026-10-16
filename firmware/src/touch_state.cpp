#include "touch_state.h"

void TouchState::reset() {
  for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
    keys_[key] = false;
    pressCounts_[key] = 0;
    releaseCounts_[key] = 0;
  }
}

void TouchState::applyHardware(const uint16_t touched[touchmap::BoardCount]) {
  for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
    const touchmap::KeyMapping& mapping = touchmap::Keys[key];
    keys_[key] = (touched[mapping.board] >> mapping.channel) & 1;
  }
}

void TouchState::update(const Mpr121Hardware& hardware, const config::Config& value) {
  for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
    const touchmap::KeyMapping& mapping = touchmap::Keys[key];
    const uint16_t baseline = hardware.baseline(mapping.board, mapping.channel);
    const uint16_t filtered = hardware.filtered(mapping.board, mapping.channel);
    const uint16_t delta = config::touchDelta(value, mapping.board, value.electrodeOffsets[key]);
    const uint16_t release = config::releaseDelta(delta, mpr121_validation::SoftwareHysteresis);
    const uint16_t drop = baseline > filtered ? static_cast<uint16_t>(baseline - filtered) : 0;
    const bool pressed = drop >= delta;
    const bool released = drop <= release;
    const uint8_t pressNeeded = static_cast<uint8_t>(value.boards[mapping.board].touchDebounce + 1);
    const uint8_t releaseNeeded = static_cast<uint8_t>(value.boards[mapping.board].releaseDebounce + 1);

    if (!keys_[key]) {
      releaseCounts_[key] = 0;
      if (pressed) {
        if (pressCounts_[key] < pressNeeded) ++pressCounts_[key];
        if (pressCounts_[key] >= pressNeeded) {
          keys_[key] = true;
          pressCounts_[key] = 0;
        }
      } else {
        pressCounts_[key] = 0;
      }
    } else {
      pressCounts_[key] = 0;
      if (released) {
        if (releaseCounts_[key] < releaseNeeded) ++releaseCounts_[key];
        if (releaseCounts_[key] >= releaseNeeded) {
          keys_[key] = false;
          releaseCounts_[key] = 0;
        }
      } else {
        releaseCounts_[key] = 0;
      }
    }
  }
}
