#pragma once

#include <stdint.h>

#include "config.h"
#include "mpr121.h"

class Calibration {
 public:
  // 失败诊断：最多上报 MaxDiagnosticChannels 个「相关通道」及其越界幅度。
  static constexpr uint8_t MaxDiagnosticChannels = 12;
  struct Diagnostics {
    uint8_t count = 0;
    uint8_t channel[MaxDiagnosticChannels] = {};
    uint8_t magnitude[MaxDiagnosticChannels] = {};  // drop（触摸/噪声）或无效读数标记
  };
  void start(uint8_t mode, uint16_t holdMs, uint32_t nowMs, uint16_t sequence);
  bool active() const { return active_; }
  bool complete(uint32_t nowMs) const;
  void cancel() { active_ = false; }
  uint16_t sequence() const { return sequence_; }
  bool due(uint32_t nowMs) const;
  void observe(uint32_t nowMs, const config::Config& current,
               const uint16_t filtered[touchmap::ChannelCount],
               const uint16_t baseline[touchmap::ChannelCount],
               const uint8_t status[touchmap::BoardCount], bool allOnline);
  bool finish(const config::Config& current, config::Config* candidate,
              uint16_t baselines[touchmap::ChannelCount], uint8_t* reason,
              Diagnostics* diagnostics = nullptr) const;

 private:
  bool active_ = false;
  uint8_t mode_ = 0;
  uint16_t holdMs_ = 0;
  uint32_t startedMs_ = 0;
  uint16_t sequence_ = 0;
  uint32_t lastSampleMs_ = 0;
  uint16_t samples_ = 0;
  uint16_t invalidSamples_ = 0;
  bool touchDetected_ = false;
  uint8_t failure_ = 0;
  uint16_t minimumFiltered_[touchmap::ChannelCount] = {};
  uint16_t maximumFiltered_[touchmap::ChannelCount] = {};
  uint8_t minimumBaseline_[touchmap::ChannelCount] = {};
  uint8_t maximumBaseline_[touchmap::ChannelCount] = {};
  uint16_t maximumDrop_[touchmap::ChannelCount] = {};
  uint8_t latestBaseline_[touchmap::ChannelCount] = {};
  // 每通道「连续超出守护值的采样数」：只有连续多帧都超才认定为真实触摸（去抖）。
  uint8_t consecutiveTouch_[touchmap::ChannelCount] = {};
};

namespace calibration_validation {

enum : uint8_t {
  FailureNone = 0,
  FailureTimeout = 1,
  FailureTouchDetected = 2,
  FailureNoisy = 3,
  FailureHardwareMissing = 4,
  FailureI2cError = 5,
  FailureInvalidRequest = 6,
};

constexpr uint16_t SamplePeriodMs = 20;
constexpr uint8_t MinimumSamples = 10;
constexpr uint16_t MaximumNoiseCounts = 8;
// 守护值：单次采样 drop 超过它只记为「可疑」，需连续多次才认定为触摸。
// 1000 ms 窗口内约 50 次采样 × 48 通道 = 2400 次判定，早期「任意一次即失败」几乎必然误报。
constexpr uint16_t UntouchedGuardCounts = 16;
constexpr uint16_t NoiseMarginCounts = 8;
// 需要连续多少次采样都超出守护值，才判定面板未清空（约 60 ms @ 20 ms/采样）。
constexpr uint16_t TouchConsecutiveSamples = 3;
// 允许的瞬时无效采样数：达到该数量才判定为硬件缺失/I2C 错误（此前是「一次即失败」）。
constexpr uint16_t MaxTransientSamples = 5;
constexpr uint16_t MaxBaselineCounts = 1020;
constexpr uint16_t MaxFilteredCounts = 1023;

using ApplyConfig = bool (*)(void*, const config::Config&);
bool apply(const config::Config& candidate, config::Config* applied,
           config::Config* staged, ApplyConfig writeHardware, void* hardware);

}  // namespace calibration_validation
