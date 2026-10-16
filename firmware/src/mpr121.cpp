#include "mpr121.h"

#ifdef ARDUINO
#include <Adafruit_MPR121.h>
#include <Wire.h>

namespace {
Adafruit_MPR121 devices[touchmap::BoardCount];

bool writeRegister(uint8_t address, uint8_t reg, uint8_t value) {
  Wire.beginTransmission(address);
  Wire.write(reg);
  Wire.write(value);
  return Wire.endTransmission() == 0;
}

// 批量读 count 个连续寄存器（MPR121 支持地址自动递增）。
bool readRegisters(uint8_t address, uint8_t reg, uint8_t* buffer, uint8_t count) {
  Wire.beginTransmission(address);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(address, count) != count) return false;
  for (uint8_t i = 0; i < count; ++i) buffer[i] = Wire.read();
  return true;
}
}  // namespace
#endif

bool Mpr121Hardware::begin() {
#ifdef ARDUINO
  Wire.begin();
  Wire.setClock(400000UL);
  bool allPresent = true;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    online_[board] = devices[board].begin(touchmap::BoardAddresses[board], &Wire);
    initialized_[board] = online_[board];
    if (online_[board]) {
      // Adafruit 默认 begin() 只做了最小初始化（AUTOCONFIG 关闭）。这里补上参考固件验证过的
      // 基线跟踪 + AUTOCONFIG 配置，让大尺寸 ITO 电极能被自动充放电校准到目标电压。
      configureHardware(board);
    } else {
      allPresent = false;
      recordI2cError(board);
    }
  }
  return allPresent;
#else
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    online_[board] = true;
    initialized_[board] = true;
  }
  return true;
#endif
}

void Mpr121Hardware::configureHardware(uint8_t board) {
#ifdef ARDUINO
  const uint8_t address = touchmap::BoardAddresses[board];

  // 软复位（参考固件 MprSetup 同款：写 0x80=0x63，随后 delay(1)）。
  writeRegister(address, 0x80, 0x63);
  delay(1);
  // 进入 Stop Mode（ECR=0），此时才能写配置寄存器。
  writeRegister(address, 0x5E, 0x00);

  // 基线跟踪 —— 上升沿（释放方向）。
  writeRegister(address, 0x2B, 1);  // MHDR
  writeRegister(address, 0x2C, 1);  // NHDR
  writeRegister(address, 0x2D, 1);  // NCLR
  writeRegister(address, 0x2E, 0);  // FDLR
  // 基线跟踪 —— 下降沿（触摸方向）。
  writeRegister(address, 0x2F, 3);  // MHDF
  writeRegister(address, 0x30, 3);  // NHDF
  writeRegister(address, 0x31, 6);  // NCLF
  writeRegister(address, 0x32, 2);  // FDLF
  // 基线跟踪 —— 触摸。
  writeRegister(address, 0x33, 0);  // NHDT
  writeRegister(address, 0x34, 0);  // NCLT
  writeRegister(address, 0x35, 0);  // FDLT

  // 自动配置（AN3889）：FFI<<6 | RETRY<<4 | BVA<<2 | ARE<<1 | ACE
  // 0 | 0 | 2<<2 | 1<<1 | 1 = 0b00001011。ARE/ACE 让 MPR121 自动搜索每电极的充放电电流/时间。
  writeRegister(address, 0x7B, 0x0B);
  writeRegister(address, 0x7C, 0x00);  // SCTS=0：保留每电极 CDT 搜索（信号质量更好）
  writeRegister(address, 0x7D, 202);   // USL（3.3V）
  writeRegister(address, 0x7F, 182);   // TL
  writeRegister(address, 0x7E, 131);   // LSL
#else
  (void)board;
#endif
}

void Mpr121Hardware::resetAll() {
#ifdef ARDUINO
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    if (!online_[board] || !writeRegister(touchmap::BoardAddresses[board], 0x80, 0x63)) {
      initialized_[board] = false;
      recordI2cError(board);
    } else {
      initialized_[board] = true;
    }
  }
#endif
}

void Mpr121Hardware::haltAll() {
#ifdef ARDUINO
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    if (!online_[board] || !writeRegister(touchmap::BoardAddresses[board], 0x5E, 0)) {
      recordI2cError(board);
    }
  }
#endif
}

bool Mpr121Hardware::applyConfig(const config::Config& value) {
  if (!config::validate(value)) return false;
#ifdef ARDUINO
  bool allSuccess = true;
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    if (!online_[board]) {
      allSuccess = false;
      initialized_[board] = false;
      recordI2cError(board);
      continue;
    }
    bool boardSuccess = true;
    const uint8_t address = touchmap::BoardAddresses[board];
    const auto writeChecked = [&](uint8_t reg, uint8_t registerValue) {
      const bool success = writeRegister(address, reg, registerValue);
      if (!success) recordI2cError(board);
      boardSuccess = success && boardSuccess;
    };
    writeChecked(0x5E, 0);
    for (uint8_t channel = 0; channel < 12; ++channel) {
      uint16_t delta = config::touchDelta(value, board, 0);
      for (uint8_t key = 0; key < touchmap::KeyCount; ++key) {
        if (touchmap::Keys[key].board == board && touchmap::Keys[key].channel == channel) {
          delta = config::touchDelta(value, board, value.electrodeOffsets[key]);
          break;
        }
      }
      const uint16_t release = config::releaseDelta(delta, mpr121_validation::SoftwareHysteresis);
      uint8_t hardwareDelta = 0;
      uint8_t hardwareRelease = 0;
      if (!mpr121_validation::toHardwareThreshold(delta, &hardwareDelta) ||
          !mpr121_validation::toHardwareThreshold(release, &hardwareRelease)) {
        initialized_[board] = false;
        allSuccess = false;
        return false;
      }
      writeChecked(static_cast<uint8_t>(0x41 + channel * 2), hardwareDelta);
      writeChecked(static_cast<uint8_t>(0x42 + channel * 2), hardwareRelease);
    }
    const config::BoardConfig& boardConfig = value.boards[board];
    writeChecked(0x5B, static_cast<uint8_t>((boardConfig.releaseDebounce << 4) |
                                            boardConfig.touchDebounce));
    writeChecked(0x5D, static_cast<uint8_t>((boardConfig.sfi << 3) | boardConfig.esi));
    writeChecked(0x5E, 0x8C);
    initialized_[board] = boardSuccess;
    allSuccess = boardSuccess && allSuccess;
  }
  return allSuccess;
#else
  (void)value;
  return allOnline();
#endif
}

bool Mpr121Hardware::startAll(const config::Config& value) { return applyConfig(value); }

uint16_t Mpr121Hardware::filtered(uint8_t board, uint8_t channel) const {
#ifdef ARDUINO
  if (board < touchmap::BoardCount && channel < 12 && online_[board]) {
    return devices[board].filteredData(channel);
  }
#else
  if (board < touchmap::BoardCount && channel < 12) return hostFiltered_[board * 12 + channel];
#endif
  return 0;
}

uint16_t Mpr121Hardware::baseline(uint8_t board, uint8_t channel) const {
#ifdef ARDUINO
  if (board < touchmap::BoardCount && channel < 12 && online_[board]) {
    return devices[board].baselineData(channel);
  }
#else
  if (board < touchmap::BoardCount && channel < 12) return hostBaseline_[board * 12 + channel];
#endif
  return 0;
}

uint16_t Mpr121Hardware::touched(uint8_t board) const {
#ifdef ARDUINO
  if (board >= touchmap::BoardCount || !online_[board]) return 0;
  const uint8_t address = touchmap::BoardAddresses[board];
  Wire.beginTransmission(address);
  Wire.write(0x00);
  if (Wire.endTransmission(false) != 0) return 0;
  if (Wire.requestFrom(address, static_cast<uint8_t>(2)) != 2) return 0;
  const uint8_t lo = Wire.read();
  const uint8_t hi = Wire.read();
  return static_cast<uint16_t>(lo) | (static_cast<uint16_t>(hi) << 8);
#else
  (void)board;
  return 0;
#endif
}

void Mpr121Hardware::snapshot(uint16_t filteredValues[touchmap::ChannelCount],
                              uint16_t baselineValues[touchmap::ChannelCount],
                              uint8_t status[touchmap::BoardCount]) {
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    status[board] = static_cast<uint8_t>((online_[board] ? 1 : 0) |
                                         (initialized_[board] ? 2 : 0) |
                                         (i2cErrorSinceSnapshot_[board] ? 8 : 0));
    if (!online_[board]) {
      for (uint8_t channel = 0; channel < 12; ++channel) {
        const uint8_t index = static_cast<uint8_t>(board * 12 + channel);
        filteredValues[index] = 0;
        baselineValues[index] = 0;
      }
      continue;
    }
#ifdef ARDUINO
    const uint8_t address = touchmap::BoardAddresses[board];

    // 批量读 filtered（0x04-0x1B，24 字节）与 baseline（0x1E-0x29，12 字节），
    // 避免逐寄存器走 Adafruit BusIO 带来的大量 I2C 开销。
    uint8_t filteredRaw[24] = {};
    uint8_t baselineRaw[12] = {};
    if (!readRegisters(address, 0x04, filteredRaw, 24)) {
      recordI2cError(board);
      for (uint8_t channel = 0; channel < 12; ++channel) {
        const uint8_t index = static_cast<uint8_t>(board * 12 + channel);
        filteredValues[index] = 0;
        baselineValues[index] = 0;
      }
      continue;
    }
    const bool baselineOk = readRegisters(address, 0x1E, baselineRaw, 12);
    if (!baselineOk) recordI2cError(board);
    for (uint8_t channel = 0; channel < 12; ++channel) {
      const uint8_t index = static_cast<uint8_t>(board * 12 + channel);
      filteredValues[index] = static_cast<uint16_t>(filteredRaw[channel * 2]) |
                              (static_cast<uint16_t>(filteredRaw[channel * 2 + 1]) << 8);
      baselineValues[index] = baselineOk
                                  ? static_cast<uint16_t>(baselineRaw[channel]) << 2
                                  : 0;
      if (baselineValues[index] != 0) status[board] |= 4;
    }
    i2cErrorSinceSnapshot_[board] = false;
#else
    for (uint8_t channel = 0; channel < 12; ++channel) {
      const uint8_t index = static_cast<uint8_t>(board * 12 + channel);
      filteredValues[index] = hostFiltered_[index];
      baselineValues[index] = hostBaseline_[index];
      if (baselineValues[index] != 0) status[board] |= 4;
    }
    i2cErrorSinceSnapshot_[board] = false;
#endif
  }
}

void Mpr121Hardware::recordI2cError(uint8_t board) {
  if (board >= touchmap::BoardCount) return;
  i2cErrorSinceSnapshot_[board] = true;
  if (i2cErrors_ != 0xFFFF) ++i2cErrors_;
}

bool Mpr121Hardware::boardOnline(uint8_t board) const {
  return board < touchmap::BoardCount && online_[board];
}

bool Mpr121Hardware::allOnline() const {
  for (uint8_t board = 0; board < touchmap::BoardCount; ++board) {
    if (!online_[board]) return false;
  }
  return true;
}

void Mpr121Hardware::setHostSample(uint8_t board, uint8_t channel,
                                   uint16_t filteredValue, uint16_t baselineValue) {
#ifndef ARDUINO
  if (board < touchmap::BoardCount && channel < 12) {
    hostFiltered_[board * 12 + channel] = filteredValue;
    hostBaseline_[board * 12 + channel] = baselineValue;
  }
#else
  (void)board;
  (void)channel;
  (void)filteredValue;
  (void)baselineValue;
#endif
}
