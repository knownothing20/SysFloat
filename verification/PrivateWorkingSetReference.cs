using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Independent Windows PDH reference for the EX2 production reader.
internal static class PrivateWorkingSetReference
{
    private const uint Format = 0x400 | 0x1000;
    internal static Dictionary<int, ulong> Read()
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            Require(Open(IntPtr.Zero, UIntPtr.Zero, out query));
            Require(Add(query, @"\Process(*)\Working Set - Private", UIntPtr.Zero, out var memory));
            Require(Add(query, @"\Process(*)\ID Process", UIntPtr.Zero, out var ids));
            Require(Collect(query));
            var memoryValues = Values(memory);
            var idValues = Values(ids);
            var result = new Dictionary<int, ulong>();
            foreach (var entry in memoryValues)
            {
                if (idValues.TryGetValue(entry.Key, out var pid) && pid > 0 && pid <= int.MaxValue && entry.Value >= 0)
                    result[(int)pid] = (ulong)entry.Value;
            }
            return result;
        }
        finally { if (query != IntPtr.Zero) Close(query); }
    }

    private static Dictionary<string, long> Values(IntPtr counter)
    {
        uint bytes = 0;
        var status = Array(counter, Format, ref bytes, out var count, IntPtr.Zero);
        if (status != 0x800007D2 || bytes == 0 || bytes > 16 * 1024 * 1024)
            throw new InvalidOperationException("PDH reference sizing failed: " + status.ToString("X"));
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            Require(Array(counter, Format, ref bytes, out count, buffer));
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int stride = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(IntPtr.Add(buffer, i * stride));
                if (item.Status <= 1) result[Marshal.PtrToStringUni(item.Name)] = item.Value;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Require(uint status)
    {
        if (status != 0) throw new InvalidOperationException("PDH reference error: " + status.ToString("X"));
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Item
    {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public long Value;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW")] private static extern uint Open(IntPtr source, UIntPtr data, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint Add(IntPtr query, string path, UIntPtr data, out IntPtr counter);
    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")] private static extern uint Collect(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint Array(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")] private static extern uint Close(IntPtr query);
}
