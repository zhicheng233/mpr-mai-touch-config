#pragma once

#include <stdint.h>

#include "config.h"

namespace eeprom_store {

constexpr uint16_t SlotSize = 128;
constexpr uint16_t SlotCount = 2;
constexpr uint16_t RecordLength = 103;
constexpr uint16_t SlotDataEnd = RecordLength + 2;
constexpr uint16_t ReservedStart = SlotDataEnd;
constexpr uint16_t Magic = 0x4D41;
constexpr uint8_t StorageSchema = 1;
constexpr uint8_t CommitMarker = 0xA5;

static_assert(SlotDataEnd <= SlotSize, "EEPROM record must fit within a slot");

class Backend {
 public:
  virtual ~Backend() = default;
  virtual uint8_t read(uint16_t address) const = 0;
  virtual void write(uint16_t address, uint8_t value) = 0;
  // 把指定槽（0/1）的缓冲写操作落盘。flash 模拟 EEPROM 每个槽一块独立区域、
  // 擦写互不影响，因此只提交被写过的那一槽即可；AVR 真 EEPROM 立即写，无需实现。
  virtual void commit(uint8_t slot) { (void)slot; }
};

bool load(Backend& backend, config::Config* output, uint32_t* generation,
          bool* persisted, uint8_t* activeSlot = nullptr);
bool save(Backend& backend, const config::Config& value, uint32_t generation,
          uint8_t slot);
bool serializeRecord(const config::Config& value, uint32_t generation,
                     uint8_t* output, uint16_t outputCapacity);
bool validateRecord(const uint8_t* record, uint16_t recordLength,
                    config::Config* output, uint32_t* generation);

}  // namespace eeprom_store
