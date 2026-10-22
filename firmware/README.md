# 固件与硬件指南 (firmware)

基于 Seeed Studio XIAO SAMD21 (Cortex-M0+) 与 4 片 MPR121 的 maimai 触摸固件。

---

## 硬件连接

### 1. I2C 地址与引脚

4 片 MPR121 挂载在主控同一条 I2C 总线上，通过 `ADDR` 跳线设定地址：

| 板号 | ADDR 接线 | I2C 地址 | 对应物理通道 |
| :---: | :---: | :---: | :---: |
| Board 0 | GND | `0x5A` | 0 ~ 11 |
| Board 1 | 3.3V | `0x5B` | 12 ~ 23 |
| Board 2 | SDA | `0x5C` | 24 ~ 35 |
| Board 3 | SCL | `0x5D` | 36 ~ 47 |

主控接线：
- **3V3 / GND**：供电（3.3V 逻辑电平，切勿接 5V）
- **SDA (D4 / PA08) / SCL (D5 / PA09)**：I2C 数据与时钟（4 片并联，总线需保证有上拉电阻）

---

## 核心实现要点

1. **防粘键参数**：上升侧 `NCLR` 严格设为 `0x0E`（`MHDR/NHDR/NCLR/FDLR = 1/1/0x0E/0`），避免高频连按时基线如棘轮般抬升导致卡死不松。
2. **充放电参数 (CDT)**：`CONFIG2 (0x5D)` 保留 `CDT=1` (0.5µs)，避免电极充放电不足导致触发迟钝。
3. **硬件级判定**：直接读取芯片 `TOUCHSTATUS` (0x00~0x01) 寄存器判定触摸，去抖在芯片内部完成，游戏主循环扫描速率达数百 FPS。
4. **双扇区 Flash 存储**：使用两块独立的 256 字节物理 Flash 扇区轮换备份配置，带 CRC16 校验，防止断电导致配置丢失。
5. **30s 自动安全退回**：配置模式无操作 30 秒自动退回游戏模式，保证上机可用性。

---

## 编译与测试

依赖 [PlatformIO](https://platformio.org/)：

```bash
# 编译固件 (生成 .pio/build/seeed_xiao/firmware.bin)
pio run -e seeed_xiao

# 运行主机端单元测试 (需 CMake)
cd host_tests
cmake -B build && cmake --build build
ctest --test-dir build --output-on-failure
```

---

## 烧录方式

- **方式一（推荐）**：启动桌面配置工具，在「固件」页选择端口一键烧录。
- **方式二（命令行）**：
  1. 触碰 XIAO 复位焊盘 2 次（或向串口发 1200bps）进入 Bootloader。
  2. 使用 bossac 写入：
     ```bash
     bossac -p COMx -U true -e -w -v -R .pio/build/seeed_xiao/firmware.bin
     ```
