using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Pult.Services;

// Снимок датчиков через LibreHardwareMonitorLib. При неудаче — пустой снимок, без исключений.
public record SensorsSnapshot(
    float? CpuTemp,
    float? CpuLoad,
    float? GpuTemp,
    float? GpuLoad,
    List<(string Name, float Temp)> DriveTemps,
    List<(string Name, float Rpm)> Fans);

public static class SensorsService
{
    // Синхронное чтение датчиков. timeoutMs — общий дедлайн обхода.
    public static SensorsSnapshot Read(int timeoutMs = 8000)
    {
        var empty = new SensorsSnapshot(null, null, null, null,
            new List<(string Name, float Temp)>(), new List<(string Name, float Rpm)>());
        Computer? computer = null;
        try
        {
            if (timeoutMs <= 0)
                timeoutMs = 8000;
            var sw = Stopwatch.StartNew();
            Func<bool> expired = () =>
            {
                try { return sw.ElapsedMilliseconds > timeoutMs; }
                catch { return false; }
            };

            float? cpuTemp = null;
            float? cpuLoad = null;
            float? gpuTemp = null;
            float? gpuLoad = null;
            var cpuCoreTemps = new List<float>();
            var drives = new List<(string Name, float Temp)>();
            var fans = new List<(string Name, float Rpm)>();

            try
            {
                computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                };
                computer.Open();
            }
            catch
            {
                // Драйвер не встал (нужен админ) — пустой снимок.
                return empty;
            }

            try
            {
                IHardware[] hardware;
                try { hardware = computer.Hardware.ToArray(); }
                catch { return empty; }
                if (hardware.Length == 0)
                    return empty;

                void Visit(IHardware hw)
                {
                    try
                    {
                        if (expired())
                            return;
                        try { hw.Update(); }
                        catch { }

                        ISensor[] sensors;
                        try { sensors = hw.Sensors.ToArray(); }
                        catch { sensors = Array.Empty<ISensor>(); }

                        foreach (var s in sensors)
                        {
                            try
                            {
                                if (expired())
                                    break;
                                if (s.SensorType == SensorType.Temperature)
                                {
                                    if (!s.Value.HasValue)
                                        continue;
                                    float v = s.Value.Value;
                                    string name = s.Name ?? "";
                                    if (hw.HardwareType == HardwareType.Cpu)
                                    {
                                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                                            cpuTemp = v;
                                        else if (name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                            cpuCoreTemps.Add(v);
                                    }
                                    else if (hw.HardwareType == HardwareType.GpuNvidia
                                          || hw.HardwareType == HardwareType.GpuAmd
                                          || hw.HardwareType == HardwareType.GpuIntel)
                                    {
                                        if (name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (gpuTemp == null)
                                                gpuTemp = v;
                                        }
                                    }
                                    else if (hw.HardwareType == HardwareType.Storage)
                                    {
                                        string disk = (hw.Name ?? "").Trim();
                                        if (disk == "")
                                            disk = "Диск";
                                        drives.Add((disk, v));
                                    }
                                }
                                else if (s.SensorType == SensorType.Load)
                                {
                                    if (!s.Value.HasValue)
                                        continue;
                                    float v = s.Value.Value;
                                    string name = (s.Name ?? "").Trim();
                                    if (hw.HardwareType == HardwareType.Cpu
                                        && name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                                        cpuLoad = v;
                                    else if ((hw.HardwareType == HardwareType.GpuNvidia
                                           || hw.HardwareType == HardwareType.GpuAmd
                                           || hw.HardwareType == HardwareType.GpuIntel)
                                        && name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                                        gpuLoad = v;
                                }
                                else if (s.SensorType == SensorType.Fan)
                                {
                                    if (!s.Value.HasValue)
                                        continue;
                                    string name = (s.Name ?? "").Trim();
                                    if (name == "")
                                        name = "Вентилятор";
                                    fans.Add((name, s.Value.Value));
                                }
                            }
                            catch { }
                        }

                        IHardware[] subs;
                        try { subs = hw.SubHardware.ToArray(); }
                        catch { subs = Array.Empty<IHardware>(); }
                        foreach (var sub in subs)
                        {
                            try
                            {
                                if (expired())
                                    break;
                                Visit(sub);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                foreach (var hw in hardware)
                {
                    try
                    {
                        if (expired())
                            break;
                        Visit(hw);
                    }
                    catch { }
                }
            }
            catch
            {
                return empty;
            }

            try
            {
                if (cpuTemp == null && cpuCoreTemps.Count > 0)
                    cpuTemp = cpuCoreTemps.Average();
            }
            catch { }

            return new SensorsSnapshot(cpuTemp, cpuLoad, gpuTemp, gpuLoad, drives, fans);
        }
        catch
        {
            return new SensorsSnapshot(null, null, null, null,
                new List<(string Name, float Temp)>(), new List<(string Name, float Rpm)>());
        }
        finally
        {
            try { computer?.Close(); }
            catch { }
        }
    }
}
