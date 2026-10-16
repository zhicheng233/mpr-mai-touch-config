# maimai Touch Controller Protocol

Status: Phase 0 contract, protocol version 1.1. This document is the byte-level
source of truth for the firmware and the desktop tool. The USB CDC serial port
is single-owner: the game and the tool must not use it at the same time.

Protocol 1.1 adds the configurable key mapping (config schema 2, 93-byte
config blob). It is backward compatible for the legacy game output; the tool
requires the version-1.1 capability bit described in section 5.2.

## 1. Conventions

- A byte is written as `0xNN`. Byte offsets are zero-based.
- All multi-byte integers are little-endian unless a field says otherwise.
- `u8`, `i8`, `u16`, `u32` are unsigned/signed integers of exactly that width.
- The protocol version is `major=1`, `minor=1` for this contract. A device must
  reject a frame with an unsupported major version. A newer minor version is
  accepted only when the type and payload length are known; unknown fields are
  not implicitly accepted.
- Reserved bits and bytes are transmitted as zero and ignored on receipt.
- Maximum payload length is `MAX_PAYLOAD = 256` bytes. The largest complete
  frame is therefore 266 bytes: 2 sync + 1 version + 1 type + 2 sequence + 2
  length + 256 payload + 2 CRC.

## 2. Configuration/debug frame

Every new-protocol frame has this layout:

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 1 | sync0 | `0xAA` |
| 1 | 1 | sync1 | `0x55` |
| 2 | 1 | version | `0x11` for protocol 1.1 (`major << 4 | minor`) |
| 3 | 1 | type | Request, response, event, or debug type |
| 4 | 2 | seq | Little-endian request sequence number |
| 6 | 2 | len | Little-endian payload length, `0..256` |
| 8 | len | payload | Type-specific bytes |
| 8+len | 2 | crc16 | Little-endian CRC-16/CCITT |

The CRC covers bytes at offsets 2 through `7+len` inclusive: `version`,
`type`, `seq`, `len`, and `payload`. It does not cover `0xAA 0x55` or the CRC
itself. CRC-16/CCITT is non-reflected, polynomial `0x1021`, initial value
`0xFFFF`, no final XOR. For each input byte:

```text
crc ^= byte << 8
repeat 8 times:
    crc = ((crc << 1) ^ 0x1021) & 0xFFFF if (crc & 0x8000) else (crc << 1) & 0xFFFF
```

The transmitted CRC is the resulting 16-bit value, low byte first. For the
ASCII bytes `123456789`, the CRC is `0x29B1`, transmitted `B1 29`.

### 2.1 Parser requirements

The parser is a bounded byte state machine. It must read `len` only after the
header is complete, reject `len > 256` before allocating or writing payload
bytes, and never use the peer-provided length as an unbounded write size.

- In any mode, bytes are scanned for `0xAA 0x55`. A partial sync prefix is
  retained when the next byte is `0xAA`.
- The inter-byte timeout for a frame is 250 ms. On timeout, discard the partial
  frame and resume sync scanning.
- An invalid version, invalid length, or CRC failure discards the candidate
  frame and resumes scanning from the next possible sync prefix. It does not
  execute the command. In CONFIG modes, once the complete eight-byte header
  is available, the firmware sends `ERROR(BAD_FRAME)` with the header's `seq`
  and `type`; a candidate shorter than that is silent. In GAME mode all such
  errors are silent.
- A valid frame with an unknown type receives `ERROR(0x7F)` with
  `status=UNKNOWN_TYPE` when the current mode permits framed responses. The
  original `seq` is retained.
- A valid frame with a duplicate `seq` is not executed twice. During one
  session (from ENTER_CFG through EXIT_CFG, disconnect, or recovery), the
  firmware retains the most recent accepted request and response. A matching
  `(seq, type, payload CRC)` MUST replay that response; a duplicate that is no
  longer retained, or whose bytes differ, returns `ERROR(SEQUENCE_CONFLICT)`.
- For `CALIBRATE`, a duplicate request replays only the initial ACK while the
  original operation is active; it never starts a second calibration and emits
  only one `CAL_DONE` for the original request. After completion, retrying the
  same request follows the session cache rule above.
- Sequence numbers are chosen by the tool and may wrap from `0xFFFF` to `0`.
  `seq=0` is valid. Responses copy the request sequence exactly.
- CRC failures and malformed frames are silent in GAME mode so they cannot
  disturb legacy game output. CONFIG-mode framing errors use the
  `ERROR(BAD_FRAME)` response defined above.

## 3. Modes and ownership

The state machine is:

```text
BOOT -> GAME
GAME --valid ENTER_CFG--> CONFIG_IDLE
CONFIG_IDLE --valid STREAM_START--> STREAMING
CONFIG_IDLE --valid CALIBRATE--> CALIBRATING
STREAMING --valid STREAM_STOP--> CONFIG_IDLE
CALIBRATING --completion/failure--> CONFIG_IDLE
any CONFIG state --valid EXIT_CFG--> GAME
any CONFIG state --USB disconnect or config idle timeout--> GAME
```

### BOOT and GAME

After initialization, EEPROM load, and MPR121 discovery, the device enters
GAME. It emits the legacy 9-byte frames described in section 4 and MUST scan
for framed candidates concurrently. It must stop game output only after the
complete `ENTER_CFG` frame has a supported version, valid length, and valid
CRC. It then sends the ENTER response and enters CONFIG_IDLE.

`VERSION` is legal in GAME and does not stop game output. Its response is
interleaved with legacy output; the tool must tolerate this during handshake.
All other new-protocol requests in GAME are rejected silently until a valid
`ENTER_CFG` is received. Legacy commands remain legal in GAME.

### CONFIG_IDLE

Legal requests are `VERSION`, `GET_CFG`, `SET_CFG`, `APPLY_CFG`, `SAVE_CFG`,
`CALIBRATE`, `STREAM_START`, and `EXIT_CFG`. `ENTER_CFG` is idempotent and
returns `ERROR(ALREADY_CONFIGURED)` in this state. Legacy command bytes are not
interpreted in CONFIG mode.

### STREAMING

The device emits type `0x90` debug frames at the accepted actual rate. Legal
requests are `VERSION`, `STREAM_STOP`, and `EXIT_CFG`. Configuration, save, and
calibration requests receive `ERROR(BUSY)`. A stream frame always uses
`seq=0`; it is not a reply to the last request.

### CALIBRATING

The device stops game/debug output while calibrating. Legal requests are
`VERSION` and `EXIT_CFG`; all other requests receive `ERROR(BUSY)`. `EXIT_CFG`
cancels calibration, restores the previously applied configuration, sends its
ACK, and returns GAME. Cancellation through `EXIT_CFG` does not emit `CAL_DONE`;
the EXIT ACK has `status=OK` and `mode=GAME`. Completion sends event `CAL_DONE(0x21)` and returns to
CONFIG_IDLE. A failed calibration also restores the previously applied
configuration.

The configuration idle timeout is 30 seconds since the last valid framed
request or response-producing event. The timeout is disabled while STREAMING
and CALIBRATING, but USB disconnect detection still applies. A USB CDC
disconnect immediately aborts transient work, discards staged changes, and
returns GAME. The normal tool shutdown path sends `EXIT_CFG` and waits for its
ACK before closing.

Entering CONFIG initializes `staged = applied`. `SET_CFG` changes only staged;
`APPLY_CFG` replaces applied atomically and then sets staged equal to applied;
`SAVE_CFG` persists applied. A normal `EXIT_CFG`, idle timeout, USB disconnect,
or re-entry after a new session discards staged-only changes.

The command legality matrix is:

| Request | GAME | CONFIG_IDLE | STREAMING | CALIBRATING |
|---|---|---|---|---|
| VERSION | execute | execute | execute | execute |
| ENTER_CFG | execute and enter CONFIG_IDLE | `ERROR(ALREADY_CONFIGURED)` | `ERROR(ALREADY_CONFIGURED)` | `ERROR(ALREADY_CONFIGURED)` |
| EXIT_CFG | silently ignored | execute and enter GAME | execute and enter GAME | cancel, ACK, enter GAME |
| GET_CFG | silent until CONFIG | execute | `ERROR(BUSY)` | `ERROR(BUSY)` |
| SET_CFG | silent until CONFIG | execute | `ERROR(BUSY)` | `ERROR(BUSY)` |
| APPLY_CFG | silent until CONFIG | execute | `ERROR(BUSY)` | `ERROR(BUSY)` |
| SAVE_CFG | silent until CONFIG | execute | `ERROR(BUSY)` | `ERROR(BUSY)` |
| CALIBRATE | silent until CONFIG | execute and enter CALIBRATING | `ERROR(BUSY)` | `ERROR(BUSY)` |
| STREAM_START | silent until CONFIG | execute and enter STREAMING | `ERROR(BUSY)` | `ERROR(BUSY)` |
| STREAM_STOP | silent until CONFIG | `ERROR(ILLEGAL_STATE)` | execute and enter CONFIG_IDLE | `ERROR(BUSY)` |

`silent until CONFIG` means that no new-protocol error frame is emitted while
the device is still serving the game. A syntactically valid framed request is
still consumed and cannot be interpreted as a legacy command.

## 4. Legacy game protocol (unchanged)

The legacy command on the wire is exactly six bytes. The reference parser stores
the opening delimiter and the four bytes before the closing delimiter, but does
not store the closing delimiter before checking `len == 5`:

```text
byte 0 = 0x7B ('{')
byte 1 = command data
byte 2 = command data
byte 3 = opcode
byte 4 = command data
byte 5 = 0x7D ('}')
```

In other words, the legacy wire shape is `{xxOx}` where `O` is the opcode; the
stored prefix before `}` is `{xxO` plus one final data byte. A compatible
implementation must accept this six-byte shape. It must not copy the reference
parser's missing opening/terminator validation or its unbounded malformed-input
behavior.

Only the opcode at byte 3 is interpreted. The reference opcodes are:

| Opcode | Name | GAME action |
|---:|---|---|
| `0x45` (`E`) | RSET | Reset/reinitialize all four MPR121 devices |
| `0x4C` (`L`) | HALT | Disable electrodes/conditioning; stop game output |
| `0x41` (`A`) | STAT | Exit conditioning, write mapped electrode thresholds, enable electrodes |
| `0x72` (`r`) | ratio | Reply with `(`, stored bytes 1..4 unchanged, `)` |
| `0x6B` (`k`) | sensitivity | Reply with `(`, stored bytes 1..4 unchanged, `)` |

For compatibility, the legacy parser accepts only the six-byte wire shape above;
unknown opcodes have no effect and no response. Legacy commands are not valid
in CONFIG modes.

### 4.1 Legacy game output

The game frame is always exactly nine bytes:

```text
byte 0 = 0x28 ('(')
byte 1..7 = seven 5-bit chunks
byte 8 = 0x29 (')')
```

Let `key[0..33]` be the mapped key states in this order:
`A1..A8, B1..B8, C1..C2, D1..D8, E1..E8`. The reference algorithm is:

```text
value = 0
for i = 33 down to 0:
    value = (value << 1) | key[i]
for chunk = 0..6:
    output[1 + chunk] = value & 0x1F
    value >>= 5
```

`key[i]` is bit `33-i` of the 34-bit value, and output byte
`1+q` contains value bits `5q..5q+4`, least-significant chunk first. Byte 7's
top two bits are zero. This output is binary bytes, not printable base64.

The framer always emits `key[0..33]` in the fixed
`A1..A8, B1..B8, C1..C2, D1..D8, E1..E8` order, so remapping a key changes which
physical channel drives `key[i]`, never the frame layout. Each `key[i]` is taken
from the MPR121 hardware touch status (`TOUCHSTATUS`, registers `0x00`/`0x01`)
of the chip/channel named by `keymap[i]` in the applied config; the firmware
relies on the MPR121's own threshold and debounce logic for the raw touch bits.
The filtered/baseline software hysteresis path is used for debug telemetry and
calibration, not to derive the game bit. A device must not combine hardware
touch status with the software state for the same key.

## 5. New request and response types

Request types are from the tool to firmware. `ACK (0x80)` and `ERROR (0x7F)`
are firmware responses. Every response copies the request `seq`; asynchronous
events also copy the originating request sequence in their frame header.

| Type | Direction | Name | Request payload |
|---:|---|---|---:|
| `0x01` | tool -> firmware | VERSION | 0 bytes |
| `0x02` | tool -> firmware | ENTER_CFG | 0 bytes |
| `0x03` | tool -> firmware | EXIT_CFG | 0 bytes |
| `0x10` | tool -> firmware | GET_CFG | 0 bytes |
| `0x11` | tool -> firmware | SET_CFG | 93-byte config blob |
| `0x12` | tool -> firmware | SAVE_CFG | 0 bytes |
| `0x13` | tool -> firmware | APPLY_CFG | 0 bytes |
| `0x20` | tool -> firmware | CALIBRATE | 3 bytes |
| `0x30` | tool -> firmware | STREAM_START | 2 bytes |
| `0x31` | tool -> firmware | STREAM_STOP | 0 bytes |
| `0x21` | firmware -> tool | CAL_DONE | asynchronous event |
| `0x7F` | firmware -> tool | ERROR | error payload |
| `0x80` | firmware -> tool | ACK | acknowledgement payload |
| `0x90` | firmware -> tool | DEBUG_FRAME | fixed 207-byte payload |

STREAM_START's two-byte request payload is `requested_rate_hz:u16`,
little-endian. `0` requests the default of 100 Hz. Nonzero values request that
rate subject to device capability and the returned actual rate.

### 5.1 ACK payload

Every ACK payload is exactly:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | `request_type` |
| 1 | 1 | `status`, normally `OK=0` |
| 2 | 1 | `mode` after the operation: `GAME=0`, `CONFIG_IDLE=1`, `STREAMING=2`, `CALIBRATING=3` |
| 3 | 1 | `flags`: bit 0 staged dirty, bit 1 applied differs from persisted |
| 4 | variable | Operation-specific result, defined below |

`ENTER_CFG`, `EXIT_CFG`, `SET_CFG`, `APPLY_CFG`, `SAVE_CFG`, and
`STREAM_STOP` have no operation-specific result, so their ACK length is 4.
`VERSION` uses response type ACK with the VERSION result below. `GET_CFG` uses
an ACK whose result is the 93-byte config blob. `STREAM_START` result is
`accepted_rate_hz:u16` then `actual_rate_hz:u16` (both little-endian), for an
ACK length of 8. `CALIBRATE` result is `calibration_deadline_ms:u32`
(little-endian), for an ACK length of 8; it is followed by `CAL_DONE`.

For implementation and codec tests, the complete response payload lengths are:

| Response to / event | Type | Payload length |
|---|---:|---:|
| VERSION | `0x80` | 20 (`4 + 16`) |
| ENTER_CFG, EXIT_CFG, SET_CFG, APPLY_CFG, SAVE_CFG, STREAM_STOP | `0x80` | 4 |
| GET_CFG | `0x80` | 97 (`4 + 93`) |
| CALIBRATE | `0x80` | 8 (`4 + 4`) |
| STREAM_START | `0x80` | 8 (`4 + 4`) |
| Any error | `0x7F` | 4 |
| CAL_DONE success | `0x21` | 196 |
| CAL_DONE failure | `0x21` | 4 or 5+2*count |
| DEBUG_FRAME | `0x90` | 207 |

### 5.2 VERSION result

The VERSION request is empty. Its ACK result is exactly 16 bytes:

| Offset in result | Size | Field |
|---:|---:|---|
| 0 | 1 | protocol major (`1`) |
| 1 | 1 | protocol minor (`1`) |
| 2 | 1 | firmware major |
| 3 | 1 | firmware minor |
| 4 | 1 | firmware patch |
| 5 | 4 | capability bits, little-endian |
| 9 | 2 | `MAX_PAYLOAD` (`256`), little-endian |
| 11 | 1 | diagnostic channel count (`48`) |
| 12 | 1 | mapped game key count (`34`) |
| 13 | 1 | MPR121 board count (`4`) |
| 14 | 1 | current mode |
| 15 | 1 | identity flags; bit 0 means USB serial identity is present |

The v1 capability bits are bit 0 GET/SET/APPLY/SAVE, bit 1 calibration, bit 2
debug streaming, bit 3 dual-slot EEPROM, and bits 4..31 zero. A USB serial
number is the tool's device identity when present. SAMD21 (XIAO) provides a
unique USB serial; ATmega32U4 does not provide a guaranteed unique hardware
identifier in this contract, so when bit 0 of byte 15 is clear the tool keys its
local JSON by the selected USB endpoint and must warn that it is not portable
across host ports.

The protocol-1.1 capability bits add bit 4, which means the firmware supports
the configurable key mapping: config schema 2, the 93-byte config blob, and
`SET_CFG` accepting 93-byte payloads. A tool that needs mapping must clear
bit 4 and refuse to connect with a clear "firmware too old" message rather than
send a 93-byte `SET_CFG` that an older firmware would reject with
`ERROR(BAD_LENGTH)`. Bits 5..31 remain zero. Because the mapping only changes
how the game output is assembled, a device that reports bit 4 set is still
bit-for-bit compatible with the legacy game protocol in section 4.

### 5.3 Configuration blob and semantics

The 93-byte config blob used by SET_CFG and GET_CFG is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | config schema (`2`) |
| 1 | 24 | `board[4]`, six bytes each |
| 25 | 34 | `electrode[34]`, one signed byte each |
| 59 | 34 | `keymap[34]`, one packed byte each |

Each board record is:

| Board offset | Size | Field | Unit/range |
|---:|---:|---|---|
| 0 | 1 | `baseThreshold` | filtered-count delta, `1..255` |
| 1 | 1 | `sensOffset` | signed filtered-count delta, `-127..127` |
| 2 | 1 | `touchDebounce` | raw MPR121 DT field, `0..7`; effective samples `DT+1` |
| 3 | 1 | `releaseDebounce` | raw MPR121 DR field, `0..7`; effective samples `DR+1` |
| 4 | 1 | `sfi` | MPR121 SFI, `0..3`, samples `4,6,10,18` |
| 5 | 1 | `esi` | MPR121 ESI, `0..7`, period `2^ESI` ms |

Each electrode offset is signed `i8` filtered-count delta, range `-127..127`,
in the 34-key order in section 4.1. `electrode[i]` is always key-indexed
(A1..E8), independent of the key map below.

Each `keymap[i]` byte is a packed `(board << 4) | channel`, with `board` in
`0..3` and `channel` in `0..11` (the port index on that chip). The high nibble is
the board, the low nibble is the channel, so valid bytes are `0..47` (`0x00` to
`0x2F`). Key `i` in the section-4.1 order reads its game bit from the chip and
channel named by `keymap[i]`. The default `keymap` is exactly the reference
wiring in section 8, so a device that has never been reconfigured produces the
golden vectors in section 8 byte-for-byte. `electrode[i]` and `keymap[i]` are
paired: the per-key offset applies to whichever channel `keymap[i]` selects.

An implementation must reject a config schema other than 2, any out-of-range
field, and any `keymap` in which a `board`/`channel` pair is invalid or the 34
entries are not all distinct (each channel can belong to only one key). Across
schema 2, all 48 channels are still sampled, but only the 34 channels named by
`keymap` are eligible for game bits; the remaining channels have no electrode
config byte and use board values plus offset zero. Rejection leaves staged,
applied, and persisted configurations unchanged. Final threshold arithmetic is
always saturated by the rules below; a sum that needs saturation is not by
itself a validation failure.

The default config uses `baseThreshold=20`, `sensOffset=0`,
`touchDebounce=1`, `releaseDebounce=1`, `sfi=2`, and `esi=0` on all four
boards. Its 34 electrode offsets are the `Legacy offset` values in section 8,
and its 34 `keymap` bytes are the `Chip`/`Port` columns in the same order. This
keeps the reference tuning and wiring as the starting point; the new
filtered/baseline decision path remains the sole source of game bits.

For key `i`, let `b = keymap[i].board` and `c = keymap[i].channel`, and let
`baseline10`/`filtered10` be the 10-bit values of that channel. The software
threshold delta is:

```text
touch_delta[i] = clamp(baseThreshold[b] + sensOffset[b] + electrode[i], 1, 1023)
release_delta[i] = touch_delta[i] - hysteresis[b]
release_delta[i] = clamp(release_delta[i], 0, touch_delta[i] - 1)
absolute_touch_limit = clamp(baseline10 - touch_delta, 0, 1023)
absolute_release_limit = clamp(baseline10 - release_delta, 0, 1023)
```

`hysteresis[b]` is a firmware calibration/runtime constant, not a wire field;
its minimum, default, and conversion to MPR121 8-bit hardware thresholds are
implementation validation items. A firmware implementation must publish those
values before hardware acceptance and must saturate all MPR121 register writes
to the device's legal 8-bit range. The 10-bit filtered value and 10-bit
baseline scale are used for software decisions; `baselineData()` is an 8-bit
register value left-shifted by two and therefore has four filtered-count
quantization.

All 48 channels (four chips, ports 0..11) are enabled for sampling, baseline
tracking, calibration, and debug diagnostics. Only the 34 channels named by
`keymap` are eligible for game bits. Each channel with no key uses board values
plus offset zero; it never affects game output.

SET_CFG validates and stores the entire blob in staged RAM only. APPLY_CFG
validates hardware feasibility, then atomically replaces the complete applied
RAM configuration; it never writes EEPROM. If any board/channel application
fails, the previous applied configuration remains wholly active. Changing solely
`keymap` reassigns which physical channel each game bit reads; it never rewrites
the legacy 9-byte game framing. SAVE_CFG writes only the most recently applied
configuration and never staged-only data. GET_CFG returns the applied
configuration, not staged or persisted data. The ACK flags expose staged
dirtiness and applied-versus-persisted difference.

### 5.4 Calibration

CALIBRATE payload is exactly three bytes:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | mode: `0=auto`, `1=manual` |
| 1 | 2 | `hold_ms`, little-endian, `0..60000` |

The firmware stops output, enters conditioning, verifies an un-touched panel,
collects all 48 channels, computes thresholds, and restores the complete
applied configuration on failure. `hold_ms=0` means the firmware-selected
validated window. The exact sample count, noise acceptance limits, and maximum
calibration duration are implementation validation items and must be reported
in firmware release notes before hardware acceptance.

Successful `CAL_DONE` updates applied RAM only. It does not modify staged or
persisted configuration; explicit `SAVE_CFG` is required to persist it.
CAL_DONE type `0x21` has the originating request `seq` copied in the frame
header and a success payload of exactly 196 bytes:

```text
status:u8, reason:u8, reserved:u16, baseline[48]:u16,
touch_threshold[48]:u16
```

The reserved field is zero. Arrays are little-endian and ordered chip 0 port
0..11, chip 1 port 0..11, chip 2 port 0..11, chip 3 port 0..11.
`status` values are `0=SUCCESS` and `1=FAILED`. For failure,
the payload is `status:u8, reason:u8, reserved:u16` and may be followed by an
optional diagnostic block. Two failure layouts exist:

```text
no diagnostics:  status:u8, reason:u8, reserved:u16                 = 4 bytes
with diagnostics: status,reason,reserved:u16,count:u8,
                  count×(channel:u8, magnitude:u8)                 = 5 + 2*count bytes
```

`count` is `0..12`. A `count` of zero is equivalent to the 4-byte form, so a
tool may treat both identically; tools must accept either length. `channel` is
the physical channel index (`0..47`, chip-major as above) and `magnitude` is the
observed violation: the peak `baseline-filtered` drop for `touch_detected`, or
`max(filtered_spread, baseline_drift)` for `noisy`. Diagnostics identify the
electrodes a user should inspect and are advisory only; they never change the
failure semantics. Older applied values remain active on any failure. Reasons
are `0=none`, `1=timeout`, `2=touch_detected`, `3=noisy`, `4=hardware_missing`,
`5=i2c_error`, `6=invalid_request`.

`touch_threshold` is the final per-channel touch threshold delta in filtered
10-bit counts after calibration, board settings, electrode offsets, and
clamping. Its 48 entries use the physical channel order above; channels with no
key use board settings with zero electrode offset.

### 5.5 Debug stream

DEBUG_FRAME type `0x90` has a fixed payload length of 207 bytes. The exact
layout is:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | `timestamp_ms:u32`, little-endian, device `millis()` modulo 2^32 |
| 4 | 5 | `touch:34bit-packed`, same order/rule as section 4.1; top 6 bits zero |
| 9 | 96 | `filtered[48]:u16`, little-endian |
| 105 | 96 | `baseline[48]:u16`, little-endian |
| 201 | 4 | `chip_status[4]:u8` |
| 205 | 2 | `i2c_errors:u16`, little-endian, cumulative saturating count |

Thus `4 + 5 + 96 + 96 + 4 + 2 = 207`, which is below `MAX_PAYLOAD=256`.
Filtered and baseline arrays use the same 48-channel order as CAL_DONE. Each
`chip_status` byte has bit 0 online, bit 1 initialized, bit 2 baseline valid,
bit 3 had an I2C error since the previous debug frame; bits 4..7 are zero.
The default requested rate is 100 Hz. The device returns both requested and
actual rates in STREAM_START ACK, or rejects a rate it cannot sustain. The
accepted request range and measured-rate tolerance are implementation
validation items; no firmware may claim 100 Hz without measuring it.

The five `touch` bytes are the little-endian encoding of the same 34-bit value
used by the legacy seven chunks: byte 0 contains value bits 0..7, byte 1 bits
8..15, byte 2 bits 16..23, byte 3 bits 24..31, and byte 4 contains value bits
32..33 in its low two bits; the upper six bits of byte 4 are zero. The value
bit for key `i` is `33-i` as defined in section 4.1. For example, A1 is
`00 00 00 00 02`, E8 is `01 00 00 00 00`, and all 34 keys are
`FF FF FF FF 03`.

## 6. Errors and timeouts

ERROR payload is exactly four bytes:

```text
request_type:u8, status:u8, detail:u16
```

Status values:

| Value | Name | Meaning |
|---:|---|---|
| `0x01` | BAD_FRAME | Invalid version, length, or CRC after CONFIG mode |
| `0x02` | UNKNOWN_TYPE | Type is not defined by this version |
| `0x03` | BAD_LENGTH | Type payload length is wrong |
| `0x04` | INVALID_VALUE | Field is outside its documented range |
| `0x05` | ILLEGAL_STATE | Request is not legal in the current mode |
| `0x06` | BUSY | Calibration or streaming operation prevents it |
| `0x07` | SEQUENCE_CONFLICT | Duplicate sequence has different content |
| `0x08` | ALREADY_CONFIGURED | ENTER_CFG while already in CONFIG |
| `0x09` | HARDWARE | MPR121/I2C operation failed |
| `0x0A` | STORAGE | EEPROM load/save/verification failed |
| `0x0B` | TIMEOUT | Operation exceeded its contract timeout |

ACK status `0x00` is `OK`; the nonzero status values above are error statuses
and are not used as successful ACK status values. `detail` is zero unless a
status-specific detail is defined by a future minor version.

The tool waits 1000 ms for VERSION/ENTER/EXIT/GET/SET/APPLY/SAVE/stream ACKs,
and 1000 ms for CALIBRATE's initial ACK. It waits for CAL_DONE up to the
firmware-reported calibration deadline; absent such a report it treats the
operation as timed out and reconnects. These host waits do not override the
250 ms inter-byte parser timeout or the 30-second firmware config idle timeout.

## 7. EEPROM storage contract

EEPROM uses two independent 128-byte slots; slot 0 starts at address 0 and
slot 1 at address 128. A slot is never a direct C struct dump. The record is:

| Slot offset | Size | Field |
|---:|---:|---|
| 0 | 2 | magic `0x4D41`, little-endian (`41 4D`) |
| 2 | 1 | storage schema `1` |
| 3 | 1 | committed marker `0xA5` |
| 4 | 2 | record length, little-endian; `103` bytes through the config blob |
| 6 | 4 | monotonically increasing generation, little-endian |
| 10 | 93 | config blob from section 5.3 |
| 103 | 2 | CRC-16/CCITT over offsets 0..2 and 4..102, little-endian; commit marker excluded |
| 105..127 | 23 | reserved, written `0xFF`, ignored |

The record length is 103 because it covers the ten-byte prefix through the
93-byte config blob: `2 + 1 + 1 + 2 + 4 + 93 = 103`, followed by two CRC bytes.
The commit marker is excluded from the CRC because it is written last. A slot
is valid only when magic, schema, committed marker, exact length, config
schema/ranges (including the key-map uniqueness rule), and CRC all validate. A
record whose length is not 103 (for example a 69-byte schema-1 record written by
older firmware) is invalid; the firmware then loads RAM defaults and reports the
persisted configuration as absent rather than migrating in place. The newest
valid generation wins; equal generations prefer slot 0.
Blank/corrupt/incompatible EEPROM causes RAM defaults to load and does not write
EEPROM automatically.
When no valid persisted slot exists, the persisted configuration is `absent`
and the applied-versus-persisted ACK flag is set until a successful SAVE_CFG.

To save, write the new record to the inactive slot with marker `0x00`, write
all other fields and the CRC (excluding the marker), verify them by reading
back, then write the committed marker `0xA5` as the final byte-changing
operation. Only after verification may the old slot be considered replaceable.
A power loss therefore leaves either the previous valid slot or the fully
committed new slot. SAVE_CFG rate limiting and EEPROM endurance policy are
implementation requirements; SAVE_CFG must never implicitly run on SET_CFG or
APPLY_CFG.

The tool's JSON mirror stores the same schema-2 config fields (including the
34-entry `keymap`), the device identity rules from VERSION, and a
dirty/applied/persisted status. On first connection, device EEPROM is
authoritative: the tool reads GET_CFG and creates or updates the JSON mirror
after readback. A user import is staged, then must pass SET_CFG, APPLY_CFG, and
GET_CFG readback before it can be applied or saved. A JSON/device conflict is
never silently merged; the tool must show both versions and require an explicit
choice.

## 8. Physical mapping and compatibility vectors

MPR121 addresses are `0x5A`, `0x5B`, `0x5C`, `0x5D`, corresponding to chip IDs
0, 1, 2, and 3. The reference 34-key mapping below is also the default
`keymap[34]` in section 5.3: for key at `Index`, the packed byte is
`(Chip << 4) | Port` (chip 3 is board 3, so A1 is `0x34` = 52, A2 is `0x30` = 48,
and so on). A tool may reorder the `Chip`/`Port` of any key to match a
differently soldered panel; the `Index`, `Key`, and `Legacy offset` columns stay
key-indexed, and the golden vectors below describe the legacy game framing, which
is unchanged by remapping.

| Index | Key | Chip | Address | Port | Legacy offset |
|---:|---|---:|---:|---:|---:|
| 0 | A1 | 3 | 0x5D | 4 | -3 |
| 1 | A2 | 3 | 0x5D | 0 | -3 |
| 2 | A3 | 2 | 0x5C | 4 | -3 |
| 3 | A4 | 2 | 0x5C | 0 | -3 |
| 4 | A5 | 1 | 0x5B | 5 | 0 |
| 5 | A6 | 1 | 0x5B | 0 | -3 |
| 6 | A7 | 0 | 0x5A | 4 | -3 |
| 7 | A8 | 0 | 0x5A | 0 | -3 |
| 8 | B1 | 3 | 0x5D | 5 | -17 |
| 9 | B2 | 3 | 0x5D | 1 | -18 |
| 10 | B3 | 2 | 0x5C | 5 | 0 |
| 11 | B4 | 2 | 0x5C | 1 | 0 |
| 12 | B5 | 1 | 0x5B | 6 | 2 |
| 13 | B6 | 1 | 0x5B | 1 | 0 |
| 14 | B7 | 0 | 0x5A | 5 | 2 |
| 15 | B8 | 0 | 0x5A | 1 | 3 |
| 16 | C1 | 2 | 0x5C | 8 | -17 |
| 17 | C2 | 1 | 0x5B | 2 | -19 |
| 18 | D1 | 3 | 0x5D | 6 | 0 |
| 19 | D2 | 3 | 0x5D | 2 | 0 |
| 20 | D3 | 2 | 0x5C | 6 | 0 |
| 21 | D4 | 2 | 0x5C | 2 | 0 |
| 22 | D5 | 1 | 0x5B | 7 | 0 |
| 23 | D6 | 1 | 0x5B | 3 | 0 |
| 24 | D7 | 0 | 0x5A | 6 | 0 |
| 25 | D8 | 0 | 0x5A | 2 | 0 |
| 26 | E1 | 3 | 0x5D | 7 | 0 |
| 27 | E2 | 3 | 0x5D | 3 | 0 |
| 28 | E3 | 2 | 0x5C | 7 | 0 |
| 29 | E4 | 2 | 0x5C | 3 | 0 |
| 30 | E5 | 1 | 0x5B | 8 | -18 |
| 31 | E6 | 1 | 0x5B | 4 | -2 |
| 32 | E7 | 0 | 0x5A | 7 | 0 |
| 33 | E8 | 0 | 0x5A | 3 | 0 |

For a single pressed key, the seven payload bytes below are `output[1..7]`
between the parentheses. Every other key is zero. This is a complete 34-key
golden-vector set and catches both ordering and chunk direction:

| Key | b1 b2 b3 b4 b5 b6 b7 |
|---|---|
| A1 | `00 00 00 00 00 00 08` |
| A2 | `00 00 00 00 00 00 04` |
| A3 | `00 00 00 00 00 00 02` |
| A4 | `00 00 00 00 00 00 01` |
| A5 | `00 00 00 00 00 10 00` |
| A6 | `00 00 00 00 00 08 00` |
| A7 | `00 00 00 00 00 04 00` |
| A8 | `00 00 00 00 00 02 00` |
| B1 | `00 00 00 00 00 01 00` |
| B2 | `00 00 00 00 10 00 00` |
| B3 | `00 00 00 00 08 00 00` |
| B4 | `00 00 00 00 04 00 00` |
| B5 | `00 00 00 00 02 00 00` |
| B6 | `00 00 00 00 01 00 00` |
| B7 | `00 00 00 10 00 00 00` |
| B8 | `00 00 00 08 00 00 00` |
| C1 | `00 00 00 04 00 00 00` |
| C2 | `00 00 00 02 00 00 00` |
| D1 | `00 00 00 01 00 00 00` |
| D2 | `00 00 10 00 00 00 00` |
| D3 | `00 00 08 00 00 00 00` |
| D4 | `00 00 04 00 00 00 00` |
| D5 | `00 00 02 00 00 00 00` |
| D6 | `00 00 01 00 00 00 00` |
| D7 | `00 10 00 00 00 00 00` |
| D8 | `00 08 00 00 00 00 00` |
| E1 | `00 04 00 00 00 00 00` |
| E2 | `00 02 00 00 00 00 00` |
| E3 | `01 00 00 00 00 00 00` |
| E4 | `10 00 00 00 00 00 00` |
| E5 | `08 00 00 00 00 00 00` |
| E6 | `04 00 00 00 00 00 00` |
| E7 | `02 00 00 00 00 00 00` |
| E8 | `01 00 00 00 00 00 00` |

Additional mandatory vectors:

- all released: `( 00 00 00 00 00 00 00 )`
- all 34 pressed: `( 1F 1F 1F 1F 1F 1F 0F )`
- key 0 and key 33 pressed: `( 01 00 00 00 00 00 08 )`
- key 4 and key 29 pressed: `( 10 00 00 00 00 10 00 )`

## 9. Hardware validation register

The following are deliberately not guessed by this contract and must be
validated on the target board before declaring Phase 1 complete: USB CDC
disconnect behavior and actual baud/line-coding handling; MPR121 presence and
I2C timeout/retry policy; the safe hardware threshold conversion and software
hysteresis constant; calibration sample window/noise limits; maximum sustainable
debug rate and accepted rate range; EEPROM write endurance and timing; and the
firmware version/identity reporting implementation. These items cannot change
the frame widths, IDs, byte order, state legality, mapping, or compatibility
vectors defined above without a protocol-version change.
