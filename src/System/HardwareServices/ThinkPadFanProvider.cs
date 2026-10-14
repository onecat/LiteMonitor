using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// ThinkPad-specific RPM fallback for LibreHardwareMonitor.
    ///
    /// First reuse TPFanCtrl2's public, read-only telemetry mapping. Only
    /// when it is absent do we read the first fan tachometer via PawnIO.
    /// No fan-mode, fan-level, or fan-select EC registers are ever modified.
    /// Hardware operations run on a background timer, not the UI thread.
    /// </summary>
    public sealed class ThinkPadFanProvider : IDisposable
    {
        private const string MappingName = @"Global\TPFanControl_State";
        private const string EcMutexName = @"Global\Access_EC";
        private const int SampleIntervalMs = 3000;
        private const int SharedStaleMs = 15000;
        private const int DeviceRetryMs = 30000;
        private const int MaxRpm = 0x1fff;
        private const int FanLoOffset = 24, FanHiOffset = 28;
        private const int Fan2LoOffset = 32, Fan2HiOffset = 36;
        private const ushort EcStatusPort = 0x66, EcDataPort = 0x62;

        private readonly Func<bool> _enabled;
        private readonly object _gate = new();
        private readonly Timer _timer;
        private readonly bool _isThinkPad;
        private readonly Mutex? _ecMutex;
        private SafeFileHandle? _device;
        private long _nextDeviceAttempt;
        private int _busy;
        private int _disposed;
        private int _fan1 = -1;
        private int _fan2 = -1;
        private int _lastPublishedSeq;
        private long _lastPublishedAt;
        private string _lastStatus = "";

        public float? CpuRpm => DecodePublished(Volatile.Read(ref _fan1));
        public float? SecondFanRpm => DecodePublished(Volatile.Read(ref _fan2));
        public string Status { get { lock (_gate) return _lastStatus; } }

        private static float? DecodePublished(int rpm) => rpm >= 0 ? rpm : null;

        public ThinkPadFanProvider(Func<bool> enabled)
        {
            _enabled = enabled;
            _isThinkPad = DetectThinkPad();
            if (_isThinkPad)
            {
                try { _ecMutex = new Mutex(false, EcMutexName); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    Debug.WriteLine("[ThinkPadFan] EC mutex unavailable: " + ex.Message);
                }
            }
            _timer = new Timer(Poll, null, SampleIntervalMs, SampleIntervalMs);
        }

        /// <summary>Zero RPM is valid (fan stopped), while 0xffff and out-of-range readings are not.</summary>
        public static int DecodeRpm(int low, int high)
        {
            int value = ((high & 0xff) << 8) | (low & 0xff);
            return value <= MaxRpm ? value : -1;
        }

        private static bool DetectThinkPad()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                string vendor = (bios?.GetValue("SystemManufacturer") as string ?? "").Trim();
                string family = (bios?.GetValue("SystemFamily") as string ?? "").Trim();
                string product = (bios?.GetValue("SystemProductName") as string ?? "").Trim();
                // Do not issue vendor EC commands to other Lenovo products or
                // machines with an unknown BIOS family.
                return vendor.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) &&
                    (family.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase) ||
                     product.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private void SetStatus(string value)
        {
            if (_lastStatus == value) return;
            _lastStatus = value;
            Debug.WriteLine("[ThinkPadFan] " + value);
        }

        private void Poll(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _busy, 1) != 0) return;
            try
            {
                lock (_gate)
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    if (!_isThinkPad || !_enabled())
                    {
                        Publish(-1, -1);
                        if (!_isThinkPad) SetStatus("Not a recognized Lenovo ThinkPad");
                        return;
                    }

                    if (TryReadShared(out int fan1, out int fan2, out bool mappingPresent))
                    {
                        Publish(fan1, fan2);
                        SetStatus("TPFanCtrl2 shared telemetry");
                        return;
                    }
                    if (mappingPresent)
                    {
                        // Another process may still own the EC. Never open the
                        // raw EC backend while its IPC mapping exists, even if
                        // telemetry is stale/uninitialized.
                        Publish(-1, -1);
                        SetStatus("TPFanCtrl2 mapping present but telemetry unavailable/stale");
                        return;
                    }

                    if (TryReadIndependent(out fan1))
                    {
                        Publish(fan1, -1); // fan 2 requires EC 0x31 writes; prohibited.
                        SetStatus("Independent PawnIO tachometer");
                    }
                    else
                    {
                        Publish(-1, -1);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ThinkPadFan] Poll failed: " + ex.Message);
                Volatile.Write(ref _fan1, -1);
                Volatile.Write(ref _fan2, -1);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private void Publish(int fan1, int fan2)
        {
            Volatile.Write(ref _fan1, fan1);
            Volatile.Write(ref _fan2, fan2);
        }

        private bool TryReadShared(out int fan1, out int fan2, out bool exists)
        {
            fan1 = fan2 = -1;
            exists = false;
            try
            {
                using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
                exists = true;
                using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (view.Capacity < 40) return false;
                int seqBefore = view.ReadInt32(0);
                if (seqBefore == 0) return false;
                int first = DecodeRpm(view.ReadInt32(FanLoOffset), view.ReadInt32(FanHiOffset));
                int second = DecodeRpm(view.ReadInt32(Fan2LoOffset), view.ReadInt32(Fan2HiOffset));
                int seqAfter = view.ReadInt32(0);
                if (seqBefore != seqAfter || first < 0) return false;

                long now = Environment.TickCount64;
                if (seqAfter != _lastPublishedSeq || _lastPublishedAt == 0)
                {
                    _lastPublishedSeq = seqAfter;
                    _lastPublishedAt = now;
                }
                if (now - _lastPublishedAt > SharedStaleMs) return false;
                fan1 = first;
                // A single-fan TPFanCtrl2 copies fan 1 into fan 2; do not
                // fabricate a second fan when both values are identical.
                fan2 = second >= 0 && second != first ? second : -1;
                return true;
            }
            catch (FileNotFoundException)
            {
                _lastPublishedSeq = 0;
                _lastPublishedAt = 0;
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _lastPublishedSeq = 0;
                _lastPublishedAt = 0;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Mapping exists but is protected; don't attempt raw access.
                exists = true;
                return false;
            }
            catch (Exception ex)
            {
                // Treat unexpected mapping errors conservatively as "present".
                exists = true;
                Debug.WriteLine("[ThinkPadFan] Shared telemetry error: " + ex.Message);
                return false;
            }
        }

        private bool TryReadIndependent(out int rpm)
        {
            rpm = -1;
            if (_ecMutex == null) { SetStatus("EC mutex unavailable"); return false; }
            if (!EnsureDevice()) return false;

            bool acquired = false;
            try
            {
                try { acquired = _ecMutex.WaitOne(100); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { SetStatus("EC busy"); return false; }

                // The engine may have started after the first shared-memory
                // check. Detect that before any hardware access.
                if (TryReadShared(out _, out _, out bool enginePresent) || enginePresent)
                {
                    SetStatus("TPFanCtrl2 started during EC acquisition");
                    return false;
                }

                // Two complete samples, reject inconsistent reads rather
                // than displaying garbage from a busy ACPI EC.
                if (!ReadRpm(out int a) || !ReadRpm(out int b) || Math.Abs(a-b) > 500)
                {
                    SetStatus("EC read failed/inconsistent");
                    return false;
                }
                rpm = b;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus("PawnIO communication failed");
                CloseDevice();
                return false;
            }
            finally
            {
                if (acquired) _ecMutex.ReleaseMutex();
            }
        }

        private bool ReadRpm(out int rpm)
        {
            rpm = -1;
            if (!ReadEcRegister(0x84, out byte low) || !ReadEcRegister(0x85, out byte high))
                return false;
            rpm = DecodeRpm(low, high);
            return rpm >= 0;
        }

        // Read-only EC transaction: ACPI READ (0x80), then register offset.
        // This necessarily uses port writes to 0x66/0x62 to issue a read, but
        // NEVER uses ACPI WRITE (0x81), EC fan control 0x2F or selector 0x31.
        private bool ReadEcRegister(byte address, out byte data)
        {
            data = 0;
            if (!WaitForStatus(mask: 0x03, expected: 0, 80)) return false;
            if (!WritePort(EcStatusPort, 0x80)) return false;
            if (!WaitForStatus(mask: 0x02, expected: 0, 80)) return false;
            if (!WritePort(EcDataPort, address)) return false;
            if (!WaitForStatus(mask: 0x02, expected: 0, 80)) return false;
            if (!WaitForStatus(mask: 0x01, expected: 1, 80)) return false;
            return ReadPort(EcDataPort, out data);
        }

        private bool WaitForStatus(byte mask, byte expected, int timeoutMs)
        {
            long until = Environment.TickCount64 + timeoutMs;
            do
            {
                if (!ReadPort(EcStatusPort, out byte status)) return false;
                if ((status & mask) == expected) return true;
                Thread.Sleep(2);
            } while (Environment.TickCount64 < until);
            return false;
        }

        private bool EnsureDevice()
        {
            if (_device != null && !_device.IsInvalid && !_device.IsClosed) return true;
            long now = Environment.TickCount64;
            if (now < _nextDeviceAttempt) return false;
            _nextDeviceAttempt = now + DeviceRetryMs;

            try
            {
                string modulePath = Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin");
                if (!File.Exists(modulePath)) { SetStatus("LpcACPIEC.bin missing beside LiteMonitor.exe"); return false; }
                var module = File.ReadAllBytes(modulePath);
                if (module.Length < 32 || module.Length > 1024 * 1024)
                {
                    SetStatus("PawnIO module size invalid");
                    return false;
                }
                _device = Native.CreateFile(@"\\.\GLOBALROOT\Device\PawnIO",
                    0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (_device.IsInvalid)
                {
                    _device.Dispose();
                    _device = Native.CreateFile(@"\\.\PawnIO",
                        0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                }
                if (_device.IsInvalid) { CloseDevice(); SetStatus("PawnIO driver unavailable (requires driver and access)"); return false; }
                bool loaded = Native.DeviceIoControl(_device, Native.LoadBinary, module,
                    module.Length, null, 0, out _, IntPtr.Zero);
                if (!loaded) { CloseDevice(); SetStatus("PawnIO rejected signed LpcACPIEC module"); return false; }
                if (!ReadPort(EcStatusPort, out _))
                {
                    CloseDevice();
                    SetStatus("PawnIO cannot reach EC 0x66");
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CloseDevice();
                SetStatus("PawnIO initialization error: " + ex.Message);
                return false;
            }
        }

        private void CloseDevice()
        {
            _device?.Dispose();
            _device = null;
        }

        private bool ReadPort(ushort port, out byte value)
        {
            value = 0;
            if (_device == null || _device.IsInvalid) return false;
            byte[] input = Native.FunctionCall("ioctl_pio_read", port);
            byte[] output = new byte[8];
            if (!Native.DeviceIoControl(_device, Native.ExecuteFunction, input,
                input.Length, output, output.Length, out int returned, IntPtr.Zero) || returned != 8)
                return false;
            value = (byte)BitConverter.ToUInt64(output, 0);
            return true;
        }

        private bool WritePort(ushort port, byte value)
        {
            if (_device == null || _device.IsInvalid) return false;
            byte[] input = Native.FunctionCall("ioctl_pio_write", port, value);
            return Native.DeviceIoControl(_device, Native.ExecuteFunction, input,
                input.Length, null, 0, out _, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
            lock (_gate)
            {
                CloseDevice();
                _ecMutex?.Dispose();
            }
        }

        private static class Native
        {
            // Matches TPFanCtrl2's portaccess.cpp and PawnIO's pawnio_um.h.
            private const uint DeviceType = 41394;
            public const uint LoadBinary = (DeviceType << 16) | (0x821u << 2);
            public const uint ExecuteFunction = (DeviceType << 16) | (0x841u << 2);

            public static byte[] FunctionCall(string name, params ulong[] args)
            {
                byte[] bytes = new byte[32 + 8 * args.Length];
                System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
                for (int i = 0; i < args.Length; i++)
                    BitConverter.GetBytes(args[i]).CopyTo(bytes, 32 + 8 * i);
                return bytes;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
            public static extern SafeFileHandle CreateFile(
                string name, uint access, uint share, IntPtr security,
                uint creation, uint flags, IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(
                SafeFileHandle device, uint code, byte[]? input, int inputLength,
                byte[]? output, int outputLength, out int bytesReturned, IntPtr overlapped);
        }
    }
}
