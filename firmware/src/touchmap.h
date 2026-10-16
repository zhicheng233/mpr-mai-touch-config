#pragma once

#include <stdint.h>

namespace touchmap {

constexpr uint8_t KeyCount = 34;
constexpr uint8_t BoardCount = 4;
constexpr uint8_t ChannelCount = 48;

struct KeyMapping {
  uint8_t board;
  uint8_t channel;
  int8_t legacyOffset;
};

constexpr uint8_t BoardAddresses[BoardCount] = {0x5A, 0x5B, 0x5C, 0x5D};

constexpr KeyMapping Keys[KeyCount] = {
    {3, 4, -3},  {3, 0, -3},  {2, 4, -3},  {2, 0, -3},  {1, 5, 0},
    {1, 0, -3},  {0, 4, -3},  {0, 0, -3},  {3, 5, -17}, {3, 1, -18},
    {2, 5, 0},   {2, 1, 0},   {1, 6, 2},   {1, 1, 0},   {0, 5, 2},
    {0, 1, 3},   {2, 8, -17}, {1, 2, -19}, {3, 6, 0},   {3, 2, 0},
    {2, 6, 0},   {2, 2, 0},   {1, 7, 0},   {1, 3, 0},   {0, 6, 0},
    {0, 2, 0},   {3, 7, 0},   {3, 3, 0},   {2, 7, 0},   {2, 3, 0},
    {1, 8, -18}, {1, 4, -2},  {0, 7, 0},   {0, 3, 0},
};

}  // namespace touchmap
