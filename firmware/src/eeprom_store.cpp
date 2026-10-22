#include "eeprom_store.h"

#include "protocol.h"

namespace {

uint32_t readLe32(const uint8_t* value) {
  return static_cast<uint32_t>(value[0]) |
         (static_cast<uint32_t>(value[1]) << 8) |
         (static_cast<uint32_t>(value[2]) << 16) |
         (static_cast<uint32_t>(value[3]) << 24);
}

void writeLe16(uint8_t* output, uint16_t value) {
  output[0] = static_cast<uint8_t>(value);
  output[1] = static_cast<uint8_t>(value >> 8);
}

void writeLe32(uint8_t* output, uint32_t value) {
  output[0] = static_cast<uint8_t>(value);
  output[1] = static_cast<uint8_t>(value >> 8);
  output[2] = static_cast<uint8_t>(value >> 16);
  output[3] = static_cast<uint8_t>(value >> 24);
}

}  // namespace

namespace eeprom_store {

namespace {

// CRC 覆盖 magic..blob（跳过 commit marker 字节 3），不含尾部的 CRC 本身。
uint16_t recordCrc(const uint8_t* record) {
  uint8_t crcInput[RecordLength] = {};
  uint16_t index = 0;
  for (uint16_t i = 0; i <= 2; ++i) crcInput[index++] = record[i];
  for (uint16_t i = 4; i < RecordLength; ++i) crcInput[index++] = record[i];
  return protocol::crc16Ccitt(crcInput, index);
}

}  // namespace

bool serializeRecord(const config::Config& value, uint32_t generation,
                     uint8_t* output, uint16_t outputCapacity) {
  if (output == nullptr || outputCapacity < RecordLength + 2 ||
      !config::serialize(value, output + 10, config::BlobLength)) {
    return false;
  }
  writeLe16(output, Magic);
  output[2] = StorageSchema;
  output[3] = 0;
  writeLe16(output + 4, RecordLength);
  writeLe32(output + 6, generation);
  writeLe16(output + RecordLength, recordCrc(output));
  return true;
}

bool validateRecord(const uint8_t* record, uint16_t recordLength,
                    config::Config* output, uint32_t* generation) {
  if (record == nullptr || output == nullptr || generation == nullptr ||
      recordLength < SlotDataEnd ||
      (static_cast<uint16_t>(record[0]) |
       (static_cast<uint16_t>(record[1]) << 8)) != Magic ||
      record[2] != StorageSchema || record[3] != CommitMarker ||
      (static_cast<uint16_t>(record[4]) |
       (static_cast<uint16_t>(record[5]) << 8)) != RecordLength ||
      (static_cast<uint16_t>(record[RecordLength]) |
       (static_cast<uint16_t>(record[RecordLength + 1]) << 8)) != recordCrc(record)) {
    return false;
  }
  if (!config::deserialize(record + 10, config::BlobLength, output)) return false;
  *generation = readLe32(record + 6);
  return true;
}

bool load(Backend& backend, config::Config* output, uint32_t* generation,
          bool* persisted, uint8_t* activeSlot) {
  if (output == nullptr || generation == nullptr || persisted == nullptr) return false;
  config::Config candidates[SlotCount];
  uint32_t generations[SlotCount] = {};
  bool valid[SlotCount] = {};
  for (uint8_t slot = 0; slot < SlotCount; ++slot) {
    uint8_t record[SlotDataEnd] = {};
    const uint16_t base = static_cast<uint16_t>(slot) * SlotSize;
    for (uint16_t i = 0; i < SlotDataEnd; ++i) record[i] = backend.read(base + i);
    valid[slot] = validateRecord(record, SlotDataEnd, &candidates[slot],
                                  &generations[slot]);
  }
  if (!valid[0] && !valid[1]) {
    *output = config::defaults();
    *generation = 0;
    *persisted = false;
    if (activeSlot != nullptr) *activeSlot = 0;
    return true;
  }
  const uint8_t selected = valid[1] && (!valid[0] || generations[1] > generations[0]) ? 1 : 0;
  *output = candidates[selected];
  *generation = generations[selected];
  *persisted = true;
  if (activeSlot != nullptr) *activeSlot = selected;
  return true;
}

bool save(Backend& backend, const config::Config& value, uint32_t generation,
          uint8_t slot) {
  if (slot >= SlotCount) return false;
  uint8_t record[RecordLength + 2] = {};
  if (!serializeRecord(value, generation, record, sizeof(record))) return false;
  const uint16_t base = static_cast<uint16_t>(slot) * SlotSize;
  backend.write(base + 3, 0);
  for (uint16_t i = 0; i < RecordLength + 2; ++i) {
    if (i == 3) continue;
    backend.write(base + i, record[i]);
  }
  for (uint16_t i = ReservedStart; i < SlotSize; ++i) {
    backend.write(base + i, 0xFF);
  }
  for (uint16_t i = 0; i < SlotDataEnd; ++i) {
    if (backend.read(base + i) != (i == 3 ? 0 : record[i])) return false;
  }
  backend.write(base + 3, CommitMarker);
  if (backend.read(base + 3) != CommitMarker) return false;
  // 只提交当前槽：两个槽各自一块独立 flash 区域，写新槽不会擦掉旧槽，
  // 因此断电时旧槽（上一代有效记录）依然完整，双槽的掉电保护才真正成立。
  backend.commit(slot);
  return true;
}

}  // namespace eeprom_store
