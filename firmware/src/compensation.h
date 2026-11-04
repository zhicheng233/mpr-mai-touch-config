#pragma once
#include <stdint.h>
#include "config.h"

// Experimental, conservative peak/return detector. It never writes MPR registers or flash.
// A sharp peak is evidence, NOT proof of physical release. Default is off.
class Compensation {
 public:
  Compensation() { reset(); }
  void reset();
  void observe(uint8_t key, uint32_t now, bool hardwareTouched, uint16_t filtered,
               uint16_t baseline, bool valid, const config::Config& config);
  bool output(uint8_t key, bool hardwareTouched, const config::Config& config) const;
  uint8_t threshold(uint8_t key) const { return states_[key].threshold / 4; }
  bool active(uint8_t key, const config::Config& config) const {
    return config.compensationMode == 2 && config::compensationSelected(config, key) && states_[key].threshold != 0;
  }
 private:
  struct State {
    uint32_t last, heldSince, quietSince, pulseSince, returnedSince, recoveredSince, cycleSince;
    int16_t previous, floor, quietMin, quietMax, peak, learnedFloor, weakestPeak;
    uint16_t baseline, threshold;
    uint8_t phase, cycles, pressCount, releaseCount;
    bool initialized, held, falling, result, recovering;
  };
  State states_[touchmap::KeyCount];
};
