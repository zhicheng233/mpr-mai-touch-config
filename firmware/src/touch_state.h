#pragma once

#include <stdint.h>

#include "config.h"
#include "mpr121.h"

class TouchState {
 public:
  void reset();
  void update(const Mpr121Hardware& hardware, const config::Config& value);
  // 直接采用 MPR121 硬件触摸状态（TOUCHSTATUS 每板 12 位）——硬件已按阈值+去抖判好，响应快。
  void applyHardware(const uint16_t touched[touchmap::BoardCount]);
  const bool* keys() const { return keys_; }

 private:
  bool keys_[touchmap::KeyCount] = {};
  uint8_t pressCounts_[touchmap::KeyCount] = {};
  uint8_t releaseCounts_[touchmap::KeyCount] = {};
};
