using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class BinaryStreamFramerTests
{
    private static BinarySpec Spec() => new BinarySpec
    {
        byteOrder     = "little",
        sync          = "4f4b",                                   // magic 'OK'
        discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
        variants = new List<BinaryVariant>
        {
            new BinaryVariant { match = 1, length = 37 },         // joystick
            new BinaryVariant { match = 3, length = 31 },         // encoder
        }
    };

    private static byte[] Packet(byte msgType, int length, uint seq)
    {
        var d = new byte[length];
        d[0] = (byte)'O'; d[1] = (byte)'K'; d[2] = 1; d[3] = msgType; d[4] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(5), seq);
        return d;
    }

    [Fact]
    public void Frames_TwoBackToBackPackets()
    {
        var f = new BinaryStreamFramer(Spec());
        var a = Packet(1, 37, 10);
        var b = Packet(3, 31, 11);
        var buf = new byte[a.Length + b.Length];
        a.CopyTo(buf, 0);
        b.CopyTo(buf, a.Length);

        f.Append(buf);
        var p1 = f.Next();
        var p2 = f.Next();
        var p3 = f.Next();

        Assert.NotNull(p1); Assert.Equal(37, p1!.Length); Assert.Equal(1, p1[3]);
        Assert.NotNull(p2); Assert.Equal(31, p2!.Length); Assert.Equal(3, p2[3]);
        Assert.Null(p3);
    }

    [Fact]
    public void Frames_AcrossSplitChunks()
    {
        var f = new BinaryStreamFramer(Spec());
        var a = Packet(1, 37, 5);

        f.Append(a.AsSpan(0, 20));            // 前半:湊不滿一包
        Assert.Null(f.Next());
        f.Append(a.AsSpan(20));               // 後半:補齊
        var p = f.Next();
        Assert.NotNull(p); Assert.Equal(37, p!.Length);
        Assert.Null(f.Next());
    }

    [Fact]
    public void Resyncs_AfterLeadingGarbage()
    {
        var f = new BinaryStreamFramer(Spec());
        var a = Packet(3, 31, 7);
        var buf = new byte[3 + a.Length];
        buf[0] = 0x11; buf[1] = 0x22; buf[2] = 0x33;   // magic 前的雜訊
        a.CopyTo(buf, 3);

        f.Append(buf);
        var p = f.Next();
        Assert.NotNull(p); Assert.Equal(31, p!.Length); Assert.Equal(3, p[3]);
    }

    [Fact]
    public void EndToEnd_FramerPlusDecoder()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little", sync = "4f4b",
            discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
            variants = new List<BinaryVariant>
            {
                new BinaryVariant
                {
                    match = 3, length = 31,
                    template = "seq:{seq};e1p:{e1p}",
                    fields = new List<BinaryField>
                    {
                        new() { name = "seq", offset = 5,  type = "u32" },
                        new() { name = "e1p", offset = 19, type = "u8" },
                    }
                }
            }
        };
        var d = Packet(3, 31, 99);
        d[19] = 123;

        var f = new BinaryStreamFramer(spec);
        f.Append(d);
        var pkt = f.Next();
        Assert.NotNull(pkt);
        Assert.Equal("seq:99;e1p:123", BinaryMaskDecoder.Decode(pkt, spec));
    }
}
