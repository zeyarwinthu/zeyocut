// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;

namespace ZeyoCut.Render;

public sealed record HardwareInfo(string Cpu, int LogicalProcessors, long? RamBytes, long? FreeRamBytes, IReadOnlyList<string> Gpus)
{
    // What the benchmark results belong to: they are thrown away when this changes.
    public string Machine => Cpu + " | " + (Gpus.Count == 0 ? "no GPU" : string.Join(", ", Gpus));
}

// What the computer has, for system_check: read from the registry and Windows itself, no extra packages.
public static class SystemInfo
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HardwareInfo Hardware()
    {
        string? cpu = null;
        var gpus = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var processor = Registry.LocalMachine.OpenSubKey(ProcessorKey);
                cpu = (processor?.GetValue("ProcessorNameString") as string)?.Trim();
                using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
                foreach (var name in adapters?.GetSubKeyNames() ?? [])
                {
                    if (name.Length != 4 || !name.All(char.IsAsciiDigit))
                    {
                        continue;
                    }

                    try
                    {
                        using var adapter = adapters!.OpenSubKey(name);
                        if (adapter?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description) && !gpus.Contains(description.Trim()))
                        {
                            gpus.Add(description.Trim());
                        }
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        long? total = null;
        long? free = null;
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                total = (long)status.TotalPhysical;
                free = (long)status.AvailablePhysical;
            }
        }

        total ??= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and var available ? available : null;
        return new HardwareInfo(cpu ?? RuntimeInformation.ProcessArchitecture + " processor", Environment.ProcessorCount, total, free, gpus);
    }

    // "1.24.4", or why ONNX Runtime can't be loaded.
    public static string OnnxRuntime(out IReadOnlyList<string> providers)
    {
        try
        {
            var environment = AiSession.Runtime();
            providers = environment.GetAvailableProviders();
            return environment.GetVersionString();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException or OnnxRuntimeException)
        {
            providers = [];
            return $"unavailable ({ex.Message.Split('\n')[0].Trim()})";
        }
    }

    public static List<string> Describe(HardwareInfo hardware)
    {
        var ram = hardware.RamBytes is { } bytes
            ? $"RAM: {Gigabytes(bytes)} GB" + (hardware.FreeRamBytes is { } free ? $", {Gigabytes(free)} GB free" : "")
            : "RAM: unknown";
        var version = OnnxRuntime(out var providers);
        return
        [
            $"CPU: {hardware.Cpu}, {hardware.LogicalProcessors} logical processors (the AI models use {AiSession.CpuThreads} threads)",
            ram,
            hardware.Gpus.Count == 0 ? "GPU: none found (DirectML needs a DirectX 12 GPU)" : $"GPU: {string.Join(", ", hardware.Gpus)}",
            $"ONNX Runtime {version}" + (providers.Count > 0 ? $" ({string.Join(", ", providers)})" : ""),
        ];
    }

    private static string Gigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
