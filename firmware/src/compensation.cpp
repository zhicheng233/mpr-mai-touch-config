#include "compensation.h"
#include <string.h>

namespace {
int16_t distance(int16_t a, int16_t b) { return a > b ? a - b : b - a; }
}

void Compensation::reset() { memset(states_, 0, sizeof(states_)); }

void Compensation::observe(uint8_t key, uint32_t now, bool hw, uint16_t filtered,
                           uint16_t baseline, bool valid, const config::Config& value) {
  if (key >= touchmap::KeyCount) return;
  State& s = states_[key];
  if (!config::compensationSelected(value, key) || !valid || filtered == 0 || filtered > 1023 || baseline == 0 || baseline > 1023) {
    s = State{};
    return;
  }
  const int16_t drop = static_cast<int16_t>(baseline) - filtered;
  const uint8_t board = touchmap::UnpackBoard(value.keyMap[key]);
  const uint16_t original = (config::touchDelta(value, board, value.electrodeOffsets[key]) + 3) / 4 * 4;
  // Missing samples / baseline movement invalidate evidence, including learned compensation.
  if (s.initialized && (static_cast<uint32_t>(now - s.last) > 40 || distance(baseline, s.baseline) > 4)) s = State{};
  if (!s.initialized) {
    s.initialized = true;
    s.last = s.quietSince = s.heldSince = now;
    s.previous = s.floor = s.quietMin = s.quietMax = drop;
    s.baseline = baseline;
    s.held = hw;
    s.result = hw;
    return;
  }
  const uint32_t dt = now - s.last;
  if (!dt) return;
  s.last = now;
  if (s.cycles && static_cast<uint32_t>(now - s.cycleSince) > 10000) s.cycles = 0;
  if (s.threshold) {
    // Fast path: fixed learned hysteresis, no platform wait on ordinary releases.
    const config::BoardConfig& b = value.boards[board];
    const bool next = s.result ? drop > static_cast<int16_t>(s.threshold - 4) : drop >= s.threshold;
    if (next == s.result) s.pressCount = s.releaseCount = 0;
    else {
      uint8_t& count = next ? s.pressCount : s.releaseCount;
      if (++count > (next ? b.touchDebounce : b.releaseDebounce)) {
        s.result = next;
        s.pressCount = s.releaseCount = 0;
      }
    }
    // Restore original detection only after genuinely returning below the old release floor.
    if (drop <= static_cast<int16_t>(original >= 4 ? original - 4 : 0)) {
      if (!s.recovering) { s.recovering = true; s.recoveredSince = now; }
      if (static_cast<uint32_t>(now - s.recoveredSince) >= 600) s = State{};
    } else s.recovering = false;
    return;  // No stacking or learning while the compensated key is held.
  }
  if (!hw) {
    s.held = false;
    s.phase = s.cycles = 0;
    s.quietSince = now;
    s.quietMin = s.quietMax = s.floor = drop;
  } else {
    if (!s.held) { s.held = true; s.heldSince = now; }
    if (s.phase == 0) {
      if (now - s.heldSince >= 800 && now - s.quietSince >= 200 &&
          s.floor >= static_cast<int16_t>(original) && drop - s.floor >= 12 &&
          drop - s.previous >= 8 && dt <= 32) {
        s.phase = 1;
        s.pulseSince = now - dt;
        s.peak = drop;
        s.falling = false;
      } else if (distance(drop, s.floor) > 3) {
        s.floor = s.quietMin = s.quietMax = drop;
        s.quietSince = now;
      } else {
        if (drop < s.quietMin) s.quietMin = drop;
        if (drop > s.quietMax) s.quietMax = drop;
        if (s.quietMax - s.quietMin > 4) { s.quietSince = now; s.quietMin = s.quietMax = s.floor = drop; }
      }
    } else if (s.phase == 1) {
      if (drop > s.peak) s.peak = drop;
      if (s.previous - drop >= 8 && dt <= 32) s.falling = true;
      const uint32_t width = now - s.pulseSince;
      if (width > 220 || drop < s.floor - 4) {
        s.phase = s.cycles = 0;
        s.quietSince = now;
        s.floor = s.quietMin = s.quietMax = drop;
      } else if (distance(drop, s.floor) <= 4) {
        if (width >= 24 && s.peak - s.floor >= 24 && s.falling) {
          s.phase = 2;
          s.returnedSince = now;
          s.quietMin = s.quietMax = drop;
        } else {
          s.phase = s.cycles = 0;
          s.quietSince = now;
          s.floor = s.quietMin = s.quietMax = drop;
        }
      }
    } else {
      if (distance(drop, s.floor) > 4 || now - s.returnedSince > 500) {
        s.phase = s.cycles = 0;
        s.quietSince = now;
        s.floor = s.quietMin = s.quietMax = drop;
      } else {
        if (drop < s.quietMin) s.quietMin = drop;
        if (drop > s.quietMax) s.quietMax = drop;
        if (s.quietMax - s.quietMin > 4) { s.phase = s.cycles = 0; s.quietSince = now; }
        else if (now - s.returnedSince >= 180) {
          if (!s.cycles || distance(s.learnedFloor, s.floor) > 4) {
            s.cycles = 1; s.cycleSince = now; s.learnedFloor = s.quietMax; s.weakestPeak = s.peak;
          } else {
            ++s.cycles;
            if (s.quietMax > s.learnedFloor) s.learnedFloor = s.quietMax;
            if (s.peak < s.weakestPeak) s.weakestPeak = s.peak;
          }
          // release >= plateau + 8, touch <= weakest peak - 12; lift <= 64 counts.
          const uint16_t candidate = static_cast<uint16_t>((s.learnedFloor + 12 + 3) / 4 * 4);
          if (s.cycles >= 2 && candidate > original && candidate <= original + 64 &&
              candidate <= original * 3 && candidate <= 1020 && candidate <= s.weakestPeak - 12) {
            s.threshold = candidate;
            s.result = false;
          }
          s.phase = 0;
          s.quietSince = now;
          s.floor = s.quietMin = s.quietMax = drop;
        }
      }
    }
  }
  s.previous = drop;
}

bool Compensation::output(uint8_t key, bool hw, const config::Config& value) const {
  return active(key, value) ? states_[key].result : hw;
}
