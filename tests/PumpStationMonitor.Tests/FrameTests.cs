using PumpStationMonitor.Core.Protocol;
using PumpStationMonitor.Core.Models;
namespace PumpStationMonitor.Tests;
public class FrameTests
{
    [Fact] public void KnownCrcAndRtuFrame()
    {
        byte[] request = [1,3,0,0,0,10];
        Assert.Equal((ushort)0xCDC5, ModbusFrames.Crc16(request));
        Assert.Equal("01030000000AC5CD", Convert.ToHexString(ModbusFrames.Rtu(1, request.AsSpan(1))));
    }
    [Theory]
    [InlineData("0103021234B533", "03021234")]
    [InlineData("010600010003980B", "0600010003")]
    public void RtuParseKnownResponse(string hex, string pdu) => Assert.Equal(pdu, Convert.ToHexString(ModbusFrames.ParseRtu(Convert.FromHexString(hex), 1)));
    [Fact] public void CorruptRtuRejected()
    {
        var frame = ModbusFrames.Rtu(1, [3,2,0,1]); frame[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => ModbusFrames.ParseRtu(frame, 1));
        Assert.Throws<InvalidDataException>(() => ModbusFrames.ParseRtu(ModbusFrames.Rtu(2, [3,2,0,1]), 1));
    }
    [Fact] public void KnownMbap()
    {
        Assert.Equal("123400000006010300140002", Convert.ToHexString(ModbusFrames.Tcp(0x1234,1,ModbusFrames.Request(3,20,2))));
        Assert.Equal(new byte[] {3,2,0,1}, ModbusFrames.ParseTcp(Convert.FromHexString("1234000000050103020001"),0x1234,1));
    }
    [Theory] [InlineData(0)] [InlineData(2)] [InlineData(4)] [InlineData(6)]
    public void BadMbapRejected(int offset)
    {
        var frame = ModbusFrames.Tcp(1,1,[3,2,0,1]); frame[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => ModbusFrames.ParseTcp(frame,1,1));
    }
    [Fact] public void MultipleWriteEncoding() => Assert.Equal("1000640002041234ABCD", Convert.ToHexString(ModbusFrames.WriteMultiple(100,[0x1234,0xABCD])));
    [Theory] [InlineData(0,0)] [InlineData(0,126)] [InlineData(65535,2)]
    public void InvalidRegisterRanges(int address,int count) => Assert.Throws<ArgumentOutOfRangeException>(()=>ModbusFrames.ValidateRange((ushort)address,count,125));
    [Fact] public void ResponseValidation()
    {
        byte[] request = ModbusFrames.Request(3,0,1);
        Assert.Equal((byte)2, Assert.Throws<ModbusException>(()=>ModbusFrames.ValidateResponse(request,[0x83,2])).Code);
        Assert.Throws<InvalidDataException>(()=>ModbusFrames.ValidateResponse(request,[4,2,0,1]));
        Assert.Throws<InvalidDataException>(()=>ModbusFrames.ValidateResponse(request,[3,4,0,1]));
        Assert.Throws<InvalidDataException>(()=>ModbusFrames.ValidateResponse(ModbusFrames.Request(6,1,2),[6,0,1,0,3]));
    }
    [Theory]
    [InlineData(0x42F6,0xE979,RegisterOrder.Abcd)]
    [InlineData(0xF642,0x79E9,RegisterOrder.Badc)]
    [InlineData(0xE979,0x42F6,RegisterOrder.Cdab)]
    [InlineData(0x79E9,0xF642,RegisterOrder.Dcba)]
    public void FloatByteOrders(int a,int b,RegisterOrder order) => Assert.Equal(123.456f,RegisterConverter.Float32((ushort)a,(ushort)b,order),3);
    [Fact] public void ScalingAndBits()
    {
        Assert.Equal(4.2,RegisterConverter.Scale(320,0.01,1),6);
        Assert.True(RegisterConverter.Bit(0x8000,15)); Assert.False(RegisterConverter.Bit(0x8000,0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>RegisterConverter.Bit(1,16));
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(16)]
    public void RtuSupportedFunctionRoundTrip(int function)
    {
        byte[] pdu=function==16?ModbusFrames.WriteMultiple(100,[12,34]):ModbusFrames.Request((byte)function,100,1);
        Assert.Equal(pdu,ModbusFrames.ParseRtu(ModbusFrames.Rtu(1,pdu),1));
    }
}
