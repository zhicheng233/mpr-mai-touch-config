using MaimaiTouchTool.Protocol;
using Xunit;

namespace MaimaiTouchTool.Tests;

/// <summary>
/// GameFrameParser 必须能解码真实固件产出的 9 字节游戏帧。
/// 这里的向量取自固件 host 测试 firmware/host_tests/test_main.cpp 的
/// checkAllLegacyVectors()：key k 单独按下时对应 7 个 5-bit chunk（LSB-first），
/// 即 key k 映射到 34 位值的 bit k（与参考固件 TouchSend 一致：
/// A1=bit 0、E8=bit 33）。
/// </summary>
public sealed class GameFrameParserTests
{
    private static readonly byte[][] FirmwareVectors =
    {
        new byte[] { 1, 0, 0, 0, 0, 0, 0 },  new byte[] { 2, 0, 0, 0, 0, 0, 0 },
        new byte[] { 4, 0, 0, 0, 0, 0, 0 },  new byte[] { 8, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0x10, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 1, 0, 0, 0, 0, 0 },
        new byte[] { 0, 2, 0, 0, 0, 0, 0 },  new byte[] { 0, 4, 0, 0, 0, 0, 0 },
        new byte[] { 0, 8, 0, 0, 0, 0, 0 },  new byte[] { 0, 0x10, 0, 0, 0, 0, 0 },
        new byte[] { 0, 0, 1, 0, 0, 0, 0 },  new byte[] { 0, 0, 2, 0, 0, 0, 0 },
        new byte[] { 0, 0, 4, 0, 0, 0, 0 },  new byte[] { 0, 0, 8, 0, 0, 0, 0 },
        new byte[] { 0, 0, 0x10, 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 1, 0, 0, 0 },
        new byte[] { 0, 0, 0, 2, 0, 0, 0 },  new byte[] { 0, 0, 0, 4, 0, 0, 0 },
        new byte[] { 0, 0, 0, 8, 0, 0, 0 },  new byte[] { 0, 0, 0, 0x10, 0, 0, 0 },
        new byte[] { 0, 0, 0, 0, 1, 0, 0 },  new byte[] { 0, 0, 0, 0, 2, 0, 0 },
        new byte[] { 0, 0, 0, 0, 4, 0, 0 },  new byte[] { 0, 0, 0, 0, 8, 0, 0 },
        new byte[] { 0, 0, 0, 0, 0x10, 0, 0 }, new byte[] { 0, 0, 0, 0, 0, 1, 0 },
        new byte[] { 0, 0, 0, 0, 0, 2, 0 },  new byte[] { 0, 0, 0, 0, 0, 4, 0 },
        new byte[] { 0, 0, 0, 0, 0, 8, 0 },  new byte[] { 0, 0, 0, 0, 0, 0x10, 0 },
        new byte[] { 0, 0, 0, 0, 0, 0, 1 },  new byte[] { 0, 0, 0, 0, 0, 0, 2 },
        new byte[] { 0, 0, 0, 0, 0, 0, 4 },  new byte[] { 0, 0, 0, 0, 0, 0, 8 },
    };

    private static byte[] Frame(byte[] chunks) =>
        new byte[] { (byte)'(' }.Concat(chunks).Concat(new byte[] { (byte)')' }).ToArray();

    [Fact]
    public void DecodesEveryFirmwareFrozenVector()
    {
        var parser = new GameFrameParser();
        for (var key = 0; key < 34; key++)
        {
            var decoded = parser.Push(Frame(FirmwareVectors[key]));
            Assert.Single(decoded);
            // 固件约定：key k 单独按下 -> 34 位值中 bit k 置位（与参考固件倒序左移等价）。
            Assert.Equal(1UL << key, decoded[0]);
        }
    }

    [Fact]
    public void DecodesAllKeysOn()
    {
        var parser = new GameFrameParser();
        var decoded = parser.Push(Frame(new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x0F }));
        Assert.Single(decoded);
        Assert.Equal(0x3FFFFFFFFUL, decoded[0]);
    }

    [Fact]
    public void ParsesMultipleFramesAndSkipsNoise()
    {
        var allOn = Frame(new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x0F });
        var allOff = Frame(new byte[] { 0, 0, 0, 0, 0, 0, 0 });
        var stream = new byte[] { 0x00, 0xFF, 0x12 }.Concat(allOn).Concat(new byte[] { 0xAA }).Concat(allOff).ToArray();

        var parser = new GameFrameParser();
        var results = parser.Push(stream);
        Assert.Equal(2, results.Count);
        Assert.Equal(0x3FFFFFFFFUL, results[0]);
        Assert.Equal(0UL, results[1]);
    }

    [Fact]
    public void IncompleteFrameProducesNothingAndRecovers()
    {
        var parser = new GameFrameParser();
        Assert.Empty(parser.Push(new byte[] { (byte)'(', 0x1F, 0x00, 0x03 }));

        var good = Frame(new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x0F });
        var stream = new byte[] { (byte)'(', 0x1F, 0x00, 0x99 }.Concat(good).ToArray();
        var results = parser.Push(stream);
        Assert.Single(results);
        Assert.Equal(0x3FFFFFFFFUL, results[0]);
    }

    [Fact]
    public void SplitAcrossPushesStitchesFrame()
    {
        var encoded = Frame(new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x0F });
        var parser = new GameFrameParser();
        Assert.Empty(parser.Push(encoded, 4));
        var remainder = encoded.Skip(4).ToArray();
        var results = parser.Push(remainder, remainder.Length);
        Assert.Single(results);
        Assert.Equal(0x3FFFFFFFFUL, results[0]);
    }
}
