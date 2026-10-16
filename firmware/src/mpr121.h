#pragma once

#include <stdint.h>

#include "touchmap.h"
#include "config.h"

namespace mpr121_validation {
constexpr uint16_t SoftwareHysteresis = 4;
constexpr uint16_t DefaultCalibrationWindowMs = 1000;
constexpr uint16_t MaxStreamRateHz = 100;
constexpr uint16_t HardwareThresholdScale = 4;
constexpr uint16_t MaxHardwareSoftwareDelta = 255 * HardwareThresholdScale;

// MPR121 thresholds are 8-bit while software decisions use 10-bit counts.
// Quantize upward against the baseline's four-count hardware resolution and
// reject deltas that cannot be represented instead of saturating them.
inline bool toHardwareThreshold(uint16_t softwareDelta, uint8_t* hardwareThreshold) {
  if (hardwareThreshold == nullptr || softwareDelta > MaxHardwareSoftwareDelta) return false;
  *hardwareThreshold = static_cast<uint8_t>(
      (softwareDelta + HardwareThresholdScale - 1) / HardwareThresholdScale);
  return true;
}
}

class Mpr121Hardware {
 public:
  bool begin();
  void resetAll();
  void haltAll();
  bool startAll(const config::Config& value);
  bool applyConfig(const config::Config& value);
  uint16_t filtered(uint8_t board, uint8_t channel) const;
  uint16_t baseline(uint8_t board, uint8_t channel) const;
  // 读 MPR121 硬件触摸状态寄存器（TOUCHSTATUS，0x00-0x01），返回 12 位（每 bit 一通道）。
  uint16_t touched(uint8_t board) const;
  void snapshot(uint16_t filteredValues[touchmap::ChannelCount],
                uint16_t baselineValues[touchmap::ChannelCount],
                uint8_t status[touchmap::BoardCount]);
  bool boardOnline(uint8_t board) const;
  bool allOnline() const;
  void setHostSample(uint8_t board, uint8_t channel, uint16_t filteredValue,
                     uint16_t baselineValue);
  void recordI2cError(uint8_t board);
  uint16_t i2cErrors() const { return i2cErrors_; }

 private:
  // 与参考固件 maimaiTouchControl 的 MprSetup 保持一致：开启 AUTOCONFIG 与基线跟踪。
  // 否则大尺寸 ITO 电极在固定 16µA 下充不满，filtered/baseline 趋近 0，触摸永不触发。
  void configureHardware(uint8_t board);
  bool online_[touchmap::BoardCount] = {};
  bool initialized_[touchmap::BoardCount] = {};
  uint16_t i2cErrors_ = 0;
  bool i2cErrorSinceSnapshot_[touchmap::BoardCount] = {};
#ifndef ARDUINO
  uint16_t hostFiltered_[touchmap::ChannelCount] = {};
  uint16_t hostBaseline_[touchmap::ChannelCount] = {};
#endif
};
