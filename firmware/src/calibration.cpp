#include "calibration.h"

#include "firmware_state.h"

void Calibration::start(uint8_t mode, uint16_t holdMs, uint32_t nowMs, uint16_t sequence) {
  mode_ = mode;
  holdMs_ = holdMs;
  startedMs_ = nowMs;
  sequence_ = sequence;
  active_ = true;
  samples_ = 0;
  invalidSamples_ = 0;
  touchDetected_ = false;
  failure_ = calibration_validation::FailureNone;
  lastSampleMs_ = nowMs;
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    maximumDrop_[channel] = 0;
    consecutiveTouch_[channel] = 0;
  }
}

bool Calibration::complete(uint32_t nowMs) const {
  if (!active_) return false;
  const uint16_t duration = holdMs_ == 0 ? mpr121_validation::DefaultCalibrationWindowMs : holdMs_;
  return static_cast<uint32_t>(nowMs - startedMs_) >= duration;
}

bool Calibration::due(uint32_t nowMs) const {
  return active_ && (samples_ == 0 ||
                     static_cast<uint32_t>(nowMs - lastSampleMs_) >=
                         calibration_validation::SamplePeriodMs);
}

void Calibration::observe(uint32_t nowMs, const config::Config& current,
                          const uint16_t filtered[touchmap::ChannelCount],
                          const uint16_t baseline[touchmap::ChannelCount],
                          const uint8_t status[touchmap::BoardCount], bool allOnline) {
  if (!due(nowMs)) return;
  lastSampleMs_ = nowMs;
  if (!allOnline) {
    if (failure_ == calibration_validation::FailureNone) {
      failure_ = calibration_validation::FailureHardwareMissing;
    }
    return;
  }
  // 单次采样出现 I2C 错误或通道读数异常时，先丢弃本次采样（continue），只有持续异常才判定失败。
  // 早期实现是「任意一次异常即整体失败」，AUTOCONFIG 未收敛/偶发 I2C 抖动会让校准几乎必然失败。
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    if ((status[board] & 8) != 0) {
      // I2C 错误不是良性噪声：MPR121 报告总线错误意味着这次采样不可信，直接判失败。
      if (failure_ == calibration_validation::FailureNone) {
        failure_ = calibration_validation::FailureI2cError;
      }
      return;
    }
    if ((status[board] & 3) != 3) {
      ++invalidSamples_;
      if (invalidSamples_ >= calibration_validation::MaxTransientSamples &&
          failure_ == calibration_validation::FailureNone) {
        failure_ = calibration_validation::FailureHardwareMissing;
      }
      return;
    }
  }
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    // filtered/baseline 为 0 或越界是该次采样的瞬时无效读数（上电初期、AUTOCONFIG 搜索中）。
    // 整次丢弃，不污染 min/max 统计，也不立即判失败。
    if (baseline[channel] == 0 || filtered[channel] == 0 ||
        baseline[channel] > calibration_validation::MaxBaselineCounts ||
        filtered[channel] > calibration_validation::MaxFilteredCounts) {
      ++invalidSamples_;
      if (invalidSamples_ >= calibration_validation::MaxTransientSamples &&
          failure_ == calibration_validation::FailureNone) {
        failure_ = calibration_validation::FailureHardwareMissing;
      }
      return;
    }
  }
  ++samples_;
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    const uint8_t board = static_cast<uint8_t>(channel / 12);
    if (samples_ == 1) {
      minimumFiltered_[channel] = maximumFiltered_[channel] = filtered[channel];
      minimumBaseline_[channel] = maximumBaseline_[channel] =
          static_cast<uint8_t>(baseline[channel] >> 2);
    } else {
      if (filtered[channel] < minimumFiltered_[channel]) minimumFiltered_[channel] = filtered[channel];
      if (filtered[channel] > maximumFiltered_[channel]) maximumFiltered_[channel] = filtered[channel];
      const uint8_t scaledBaseline = static_cast<uint8_t>(baseline[channel] >> 2);
      if (scaledBaseline < minimumBaseline_[channel]) minimumBaseline_[channel] = scaledBaseline;
      if (scaledBaseline > maximumBaseline_[channel]) maximumBaseline_[channel] = scaledBaseline;
    }
    latestBaseline_[channel] = static_cast<uint8_t>(baseline[channel] >> 2);
    const uint16_t drop = baseline[channel] > filtered[channel]
                               ? static_cast<uint16_t>(baseline[channel] - filtered[channel])
                               : 0;
    if (drop > maximumDrop_[channel]) maximumDrop_[channel] = drop;
    // 去抖：单次超出守护值只累计计数，连续 TouchConsecutiveSamples 次才认定为真实触摸。
    // 单帧 ITO 抖动/电源毛刺不再让整次校准失败（此前「任意一次即失败」是主要误报源）。
    if (drop > calibration_validation::UntouchedGuardCounts ||
        drop >= firmware_state::thresholdForChannel(current, board, channel % 12)) {
      if (consecutiveTouch_[channel] < 255) ++consecutiveTouch_[channel];
      if (consecutiveTouch_[channel] >= calibration_validation::TouchConsecutiveSamples) {
        touchDetected_ = true;
      }
    } else {
      consecutiveTouch_[channel] = 0;
    }
  }
}

bool Calibration::finish(const config::Config& current, config::Config* candidate,
                         uint16_t baselines[touchmap::ChannelCount], uint8_t* reason,
                         Diagnostics* diagnostics) const {
  if (reason == nullptr) return false;
  // 诊断输出统一在这里累加（调用方传 nullptr 表示不需要）。magnitude 语义随 reason 变化：
  //   TouchDetected → drop；Noisy → max(spread, drift)；其余 → 0。
  Diagnostics local;
  Diagnostics* diag = diagnostics != nullptr ? diagnostics : &local;
  diag->count = 0;
  const auto recordDiagnostic = [diag](uint8_t channel, uint8_t magnitude) {
    if (diag->count >= MaxDiagnosticChannels) return;
    diag->channel[diag->count] = channel;
    diag->magnitude[diag->count] = magnitude;
    ++diag->count;
  };
  if (candidate == nullptr || baselines == nullptr || !config::validate(current)) {
    *reason = calibration_validation::FailureInvalidRequest;
    return false;
  }
  if (failure_ != calibration_validation::FailureNone) {
    *reason = failure_;
    return false;
  }
  // 触摸检测优先于样本数不足上报：面板未清空是更明确、可操作的诊断。
  if (touchDetected_) {
    // 报告「确认触摸」的通道及其 drop：这些就是需要检查的电极。
    for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
      if (consecutiveTouch_[channel] >= calibration_validation::TouchConsecutiveSamples) {
        const uint16_t drop = maximumDrop_[channel];
        recordDiagnostic(channel, drop > 255 ? 255 : static_cast<uint8_t>(drop));
      }
    }
    *reason = calibration_validation::FailureTouchDetected;
    return false;
  }
  if (samples_ < calibration_validation::MinimumSamples) {
    // 从未取到任何有效采样（全都是无效读数）说明硬件确实不可用，而非单纯窗口太短。
    *reason = samples_ == 0 && invalidSamples_ > 0
                  ? calibration_validation::FailureHardwareMissing
                  : calibration_validation::FailureTimeout;
    return false;
  }
  config::Config result = current;
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    const uint16_t spread = static_cast<uint16_t>(maximumFiltered_[channel] - minimumFiltered_[channel]);
    const uint16_t drift = static_cast<uint16_t>(maximumBaseline_[channel] - minimumBaseline_[channel]) * 4;
    if (spread > calibration_validation::MaximumNoiseCounts ||
        drift > calibration_validation::MaximumNoiseCounts) {
      const uint16_t worst = spread > drift ? spread : drift;
      recordDiagnostic(channel, worst > 255 ? 255 : static_cast<uint8_t>(worst));
      *reason = calibration_validation::FailureNoisy;
      return false;
    }
    const uint8_t board = static_cast<uint8_t>(channel / 12);
    const int16_t offset = static_cast<int16_t>(current.boards[board].sensOffset) +
                           firmware_state::electrodeOffsetForChannel(current, board, channel % 12);
    // Keep the observed peak plus the full noise span below every calibrated threshold.
    const int16_t needed = static_cast<int16_t>(maximumDrop_[channel] + spread +
                                                calibration_validation::NoiseMarginCounts) - offset;
    if (needed > 255) {
      recordDiagnostic(channel, 255);
      *reason = calibration_validation::FailureNoisy;
      return false;
    }
    if (needed > result.boards[board].baseThreshold) {
      result.boards[board].baseThreshold = static_cast<uint8_t>(needed);
    }
  }
  for (uint8_t channel = 0; channel < touchmap::ChannelCount; ++channel) {
    const uint8_t board = static_cast<uint8_t>(channel / 12);
    if (firmware_state::thresholdForChannel(result, board, channel % 12) >
        mpr121_validation::MaxHardwareSoftwareDelta) {
      *reason = calibration_validation::FailureInvalidRequest;
      return false;
    }
    baselines[channel] = static_cast<uint16_t>(latestBaseline_[channel]) * 4;
  }
  *candidate = result;
  *reason = calibration_validation::FailureNone;
  return true;
}

namespace calibration_validation {

bool apply(const config::Config& candidate, config::Config* applied,
           config::Config* staged, ApplyConfig writeHardware, void* hardware) {
  if (applied == nullptr || staged == nullptr || writeHardware == nullptr) return false;
  if (!writeHardware(hardware, candidate)) {
    writeHardware(hardware, *applied);
    return false;
  }
  *applied = candidate;
  // Calibration is applied-only; staged remains the user's explicit SET_CFG state.
  (void)staged;
  return true;
}

}  // namespace calibration_validation
