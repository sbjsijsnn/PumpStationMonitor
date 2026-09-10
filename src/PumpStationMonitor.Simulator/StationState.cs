using PumpStationMonitor.Core.Protocol;
namespace PumpStationMonitor.Simulator;
public sealed class StationState
{
    private readonly object gate = new();
    private readonly ushort[] holding = new ushort[128];
    private readonly ushort[] inputs = new ushort[128];
    private readonly bool[] coils = new bool[128];
    private readonly bool[] discrete = new bool[128];
    private readonly bool[] faults = new bool[2];
    private readonly double[] runningSeconds = new double[2];
    private double lastSeconds;
    private double? forcedLevel;
    public bool Silent { get; private set; }
    public void Command(string command)
    {
        lock (gate)
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                case ["fault", "1"]: faults[0] = true; break;
                case ["fault", "2"]: faults[1] = true; break;
                case ["level", var number] when double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value is >= 0 and <= 20:
                    forcedLevel = value; break;
                case ["offline"]: Silent = true; break;
                case ["recover"]: Silent = false; forcedLevel = null; Array.Clear(faults); break;
                default: throw new ArgumentException("Commands: fault 1 | fault 2 | level 9.5 | offline | recover | quit");
            }
        }
    }
    public void Update(double seconds)
    {
        lock (gate)
        {
            double elapsed = Math.Max(0, seconds - lastSeconds);
            lastSeconds = seconds;
            for (int i = 0; i < 2; i++)
            {
                int start = i * 10;
                bool running = coils[i] && !faults[i];
                holding[start] = (ushort)(12 | (running ? 1 : 0) | (faults[i] ? 2 : 0));
                holding[start + 1] = running ? (ushort)(125 + 10 * Math.Sin(seconds + i)) : (ushort)0;
                holding[start + 2] = running ? (ushort)500 : (ushort)0;
                if (running) runningSeconds[i] += elapsed;
                holding[start + 3] = (ushort)Math.Min(ushort.MaxValue, 100 + runningSeconds[i] / 3600);
                discrete[i] = faults[i];
            }
            double level = forcedLevel ?? 4 + 1.5 * Math.Sin(seconds / 10);
            holding[20] = (ushort)(level * 100);
            holding[21] = (ushort)(200 + 30 * Math.Sin(seconds / 7));
            discrete[2] = level >= 8;
            discrete[3] = level <= 1;
            holding[22] = (ushort)((discrete[2] ? 1 : 0) | (discrete[3] ? 2 : 0));
            holding[30] = (ushort)(350 + 25 * Math.Sin(seconds / 5));
            holding[31] = (ushort)(190 + 20 * Math.Sin(seconds / 7));
            holding[32] = 750;
            Array.Copy(holding, inputs, holding.Length);
        }
    }
    public byte[]? Respond(byte unit, byte[] pdu)
    {
        lock (gate)
        {
            if (Silent || unit != 1) return null;
            byte function = pdu[0];
            byte[] Error(byte code) => [(byte)(function | 0x80), code];
            if (function is not (1 or 2 or 3 or 4 or 5 or 6 or 16)) return Error(1);
            if (pdu.Length < 5 || (function != 16 && pdu.Length != 5)) return Error(3);
            int address = ModbusFrames.ReadUInt16(pdu.AsSpan(1));
            int value = ModbusFrames.ReadUInt16(pdu.AsSpan(3));
            if (function is 1 or 2 or 3 or 4 or 16)
            {
                int max = function is 1 or 2 ? 2000 : function == 16 ? 123 : 125;
                if (value < 1 || value > max) return Error(3);
                if (address + value > 128) return Error(2);
            }
            else if (address >= 128) return Error(2);
            if (function is 1 or 2)
            {
                var source = function == 1 ? coils : discrete;
                var result = new byte[2 + (value + 7) / 8];
                result[0] = function; result[1] = (byte)(result.Length - 2);
                for (int i = 0; i < value; i++) if (source[address + i]) result[2 + i / 8] |= (byte)(1 << (i % 8));
                return result;
            }
            if (function is 3 or 4)
            {
                var source = function == 3 ? holding : inputs;
                var result = new byte[2 + value * 2];
                result[0] = function; result[1] = (byte)(value * 2);
                for (int i = 0; i < value; i++) ModbusFrames.WriteUInt16(result.AsSpan(2 + i * 2), source[address + i]);
                return result;
            }
            if (function == 5)
            {
                if (value is not (0 or 0xFF00)) return Error(3);
                coils[address] = value != 0;
            }
            if (function == 6)
            {
                holding[address] = (ushort)value;
                if (address is 40 or 41) coils[address - 40] = value != 0;
            }
            if (function == 16)
            {
                if (pdu.Length != 6 + value * 2 || pdu[5] != value * 2) return Error(3);
                for (int i = 0; i < value; i++) holding[address + i] = ModbusFrames.ReadUInt16(pdu.AsSpan(6 + i * 2));
            }
            return pdu[..5];
        }
    }
}
