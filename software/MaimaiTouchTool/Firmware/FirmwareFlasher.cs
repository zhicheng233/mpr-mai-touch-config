using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace MaimaiTouchTool.Firmware;

/// <summary>一次刷写的结果。<see cref="ApplicationPort"/> 是刷完后应用口（可能和刷写前不同）。</summary>
public sealed record FlashOutcome(bool Success, string Message, string Log, string? ApplicationPort = null);

/// <summary>
/// 用 bossac 把 firmware.bin 写进 SAMD21。
///
/// 为什么不用 <c>pio run -t upload</c>：它的 1200bps touch 自动复位成功率只有一半左右，
/// 失败时会卡在等端口上，用户看到的是"一直转圈"。这里把流程拆开、每步都能报告进度：
///
///   1. 以 1200 波特打开应用口并停留一会儿再关闭 → bootloader 复位
///   2. 按 USB PID 找到 bootloader 的 COM 口
///   3. 调 bossac 擦除 + 写入 + 校验 + 复位
///   4. 按 USB PID 找回应用口
///
/// 注意板子是 Arduino SAMD bootloader（bossac），不是 UF2，尽管 U 盘里有个 INFO_UF2.TXT。
///
/// 端口发现用注册表里的 VID/PID → PortName 映射，而不是"比较端口集合的差集"：
/// 差集在重试时会把已经进入 bootloader 的板子漏掉（它的口在上一次快照里已经存在）。
/// </summary>
public sealed class FirmwareFlasher
{
    private const string BossacRelativePath = @"packages\tool-bossac\bossac.exe";
    private const ushort SeeedVid = 0x2886;
    private const ushort BootloaderPid = 0x002F;
    private const ushort ApplicationPid = 0x802F;

    /// <summary>每行日志都会回调；界面把它塞进日志面板。</summary>
    public Action<string>? OnLog { get; init; }

    /// <summary>等应用口重新枚举的最长时间。</summary>
    public TimeSpan PortTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>单次 touch 之后等 bootloader 出现的时间。实测 bootloader 约 1 秒内枚举出来。</summary>
    public TimeSpan BootloaderTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>最多试几次 1200bps 复位。</summary>
    public int TouchAttempts { get; init; } = 4;

    /// <summary>
    /// 1200 波特打开后必须停留的时长。实测 500 ms 稳定，200/250 ms 经常静默失效 ——
    /// 太短的话固件根本看不到这次"魔法"波特率，表现就是"偶尔刷不动"。
    /// </summary>
    public int TouchHoldMs { get; init; } = 500;

    /// <summary>找 bossac.exe：先看 PlatformIO 的 packages 目录。</summary>
    public static string? FindBossac()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(home, ".platformio", BossacRelativePath);
        if (File.Exists(candidate)) return candidate;

        // 也接受 PLATFORMIO_CORE_DIR 被改过的情况
        var core = Environment.GetEnvironmentVariable("PLATFORMIO_CORE_DIR");
        if (!string.IsNullOrWhiteSpace(core))
        {
            var alt = Path.Combine(core, BossacRelativePath);
            if (File.Exists(alt)) return alt;
        }
        return null;
    }

    /// <summary>
    /// 刷写。<paramref name="applicationPort"/> 是设备当前的应用口（如 COM3），
    /// 用来做 1200bps 复位；bootloader 口由本方法自己找。
    /// </summary>
    public async Task<FlashOutcome> FlashAsync(string applicationPort, string binaryPath, CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        void Say(string line)
        {
            log.AppendLine(line);
            OnLog?.Invoke(line);
        }

        if (!File.Exists(binaryPath))
            return new FlashOutcome(false, $"firmware binary not found: {binaryPath}", log.ToString());

        var bossac = FindBossac();
        if (bossac is null)
            return new FlashOutcome(false, "bossac not found; install PlatformIO (pio) first", log.ToString());

        Say($"binary : {binaryPath} ({new FileInfo(binaryPath).Length} bytes)");
        Say($"bossac : {bossac}");
        Say($"ports now: {Describe()}");

        // ---- 1+2. 1200bps touch，等 bootloader ----
        // Arduino SAMD 的自动复位本来就要撞运气（AGENTS.md 里也记着 pio upload 成功率约一半），
        // 实测第一轮常常不生效、第二轮必中，所以这里固定允许重试，不能只试一次。
        // 每轮都用 USB PID 重新查，不依赖"端口集合变化"。
        string? bootPort = null;
        for (var attempt = 1; attempt <= TouchAttempts && bootPort is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 板子可能上一轮已经进去了，先看一眼省一次多余复位
            bootPort = FindPortByPid(BootloaderPid);
            if (bootPort is not null)
            {
                Say($"bootloader already present on {bootPort}");
                break;
            }

            try
            {
                Say(attempt == 1
                    ? $"resetting {applicationPort} at 1200 baud (hold {TouchHoldMs} ms)..."
                    : $"no bootloader yet; resetting again (attempt {attempt}/{TouchAttempts})...");
                Touch1200(applicationPort, cancellationToken);
            }
            catch (Exception ex)
            {
                // 端口可能已经不在（比如用户先拔了），继续找 bootloader
                Say($"1200bps touch failed ({ex.Message})");
            }

            bootPort = await WaitForPortAsync(BootloaderPid, BootloaderTimeout, cancellationToken);
        }
        if (bootPort is null)
        {
            Say("bootloader port did not appear");
            return new FlashOutcome(false,
                "board did not enter the bootloader. Double-click RST and try again.", log.ToString());
        }
        Say($"bootloader on {bootPort}");

        // ---- 3. bossac ----
        var arguments = $"--port={bootPort} -U true -e -w -v -R \"{binaryPath}\"";
        Say($"bossac {arguments}");

        var result = await RunAsync(bossac, arguments, cancellationToken);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Say("  " + line.TrimEnd());

        if (result.ExitCode != 0)
            return new FlashOutcome(false, $"bossac failed with exit code {result.ExitCode}", log.ToString());

        // bossac 的 -v 校验失败时退出码仍可能是 0，所以还要看输出
        if (!result.Output.Contains("Verify successful", StringComparison.OrdinalIgnoreCase))
            return new FlashOutcome(false, "bossac did not report a successful verify", log.ToString());

        Say("verify ok, CPU reset");

        // ---- 4. 等应用口回来 ----
        // 用 PID 而不是端口号：刷完后很可能复用刷写前的同一个 COM 号。
        var appPort = await WaitForPortAsync(ApplicationPid, PortTimeout, cancellationToken);
        Say(appPort is null
            ? "application port did not come back yet (it may take a second)"
            : $"application port back on {appPort}");

        return new FlashOutcome(true,
            appPort is null ? "flashed and verified" : $"flashed and verified; device is on {appPort}",
            log.ToString(), appPort);
    }

    private static async Task<string?> WaitForPortAsync(ushort pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = FindPortByPid(pid);
            if (port is not null) return port;
            await Task.Delay(250, cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// 查 Seeed VID 下某个 PID 对应的 COM 口。
    ///
    /// 读注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB</c>：复合设备的 MI_00 子键下
    /// 有 <c>Device Parameters\PortName</c>，这个映射在设备枚举/去枚举时同步更新，
    /// 比 WMI 快，也不用为了它引入 System.Management 包。
    ///
    /// 关键：注册表会保留**已拔掉设备**的历史键（PortName 还写着当初分配的 COM 号），
    /// 所以必须和 <see cref="SerialPort.GetPortNames"/> 求交集，否则会对着一个
    /// 不存在的口跑 bossac（表现为 "exit code 1" 且看不出原因）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? FindPortByPid(ushort pid)
    {
        var live = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null) return null;

            var prefix = $"VID_{SeeedVid:X4}&PID_{pid:X4}";
            foreach (var keyName in usb.GetSubKeyNames())
            {
                if (!keyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                using var instanceRoot = usb.OpenSubKey(keyName);
                if (instanceRoot is null) continue;

                foreach (var instance in instanceRoot.GetSubKeyNames())
                {
                    using var parameters = instanceRoot
                        .OpenSubKey(instance)?
                        .OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is string name &&
                        name.StartsWith("COM", StringComparison.Ordinal) &&
                        live.Contains(name))
                    {
                        return name;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 注册表不可读时就当没找到；调用方会走 touch 重试而不是误判
        }
        return null;
    }

    private static string Describe()
    {
        var ports = SerialPort.GetPortNames();
        return ports.Length == 0 ? "(none)" : string.Join(", ", ports.OrderBy(p => p));
    }

    /// <summary>
    /// 以 1200 波特打开应用口再关掉 —— Arduino SAMD bootloader 靠这个波特率进入 bootloader。
    ///
    /// DTR 必须是**低**的。翻一下 Arduino SAMD 核心 <c>USB/CDC.cpp</c>：
    /// <code>
    /// if (_usbLineInfo.dwDTERate == 1200 &amp;&amp; (_usbLineInfo.lineState &amp; CDC_LINESTATE_DTR) == 0)
    ///     initiateReset(250);
    /// else
    ///     cancelReset();
    /// </code>
    /// 也就是说 DTR 拉高会**取消**已经排上的复位。之前把 DtrEnable 设成 true，
    /// 表现就是\"偶尔刷得动、多数时候刷不动\"——实际上由 DTR 的最终状态决定。
    /// </summary>
    private void Touch1200(string port, CancellationToken cancellationToken)
    {
        using var touch = new SerialPort(port, 1200)
        {
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 500,
            WriteTimeout = 1000,
        };
        touch.Open();
        cancellationToken.WaitHandle.WaitOne(TouchHoldMs);
        touch.Close();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, string arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        // WaitForExitAsync 可能在两个异步读取器排空之前就返回，
        // 不等一下会漏掉最后几行（比如 "Verify successful"）。
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }
}
