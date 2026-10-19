// MachineIdentity.cs - who this PC is to the xman studio license server.
//
// Ported from WinXTools (NetX.Core/System/MachineIdentity.cs, 2026-09-23),
// which learned the hard way that an id built from computer name + Windows
// user name "moves" a license on a rename and hands every new Windows account
// a fresh trial. The id comes from the Windows installation (MachineGuid) and
// the motherboard (SMBIOS system UUID), read straight from the firmware
// table: no WMI, so it cannot hang, and it is the same for every account and
// every process on the machine — the client and brainx-mcp agree on it, which
// is what lets the MCP check the license the window saved.
//
// BrainX had no desktop license before, so there is no legacy id to migrate.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BrainX.Core.Services.License;

public static class MachineIdentity
{
    private static readonly Lazy<Snapshot> Current = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>64 lowercase hex characters (the server accepts 32-64).</summary>
    public static string MachineId => Current.Value.MachineId;

    /// <summary>
    /// Hash of the motherboard identifiers, which survive a Windows reinstall;
    /// the server uses it to spot a trial restarted on the same hardware. Null
    /// when the firmware reports only placeholders, so unrelated PCs are never
    /// lumped together.
    /// </summary>
    public static string? HardwareHash => Current.Value.HardwareHash;

    /// <summary>The Windows installation id, or null. Part of the license
    /// cache's seal, so a copied license file does not open on another PC.</summary>
    public static string? MachineGuid => Current.Value.MachineGuid;

    public static string MachineName => Environment.MachineName;

    public static string OsVersion => Environment.OSVersion.VersionString;

    /// <summary>Computes the ids in the background so the first license call never waits.</summary>
    public static void Warm() => _ = Task.Run(() => Current.Value);

    private sealed record Snapshot(string MachineId, string? HardwareHash, string? MachineGuid);

    private static Snapshot Build()
    {
        var machineGuid = ReadMachineGuid();
        var smbios = OperatingSystem.IsWindows() ? SmbiosReader.Read() : new SmbiosReader.Result(null, null, null);

        var uuid = smbios.SystemUuid is { } raw && !IsPlaceholderUuid(raw) ? Convert.ToHexString(raw) : null;
        var boardSerial = CleanSerial(smbios.BoardSerial);
        var systemSerial = CleanSerial(smbios.SystemSerial);

        // No MachineGuid (not Windows, or a locked-down registry): fall back to
        // what is left, which is weaker but still stable for this account.
        var machineId = Sha256Hex(machineGuid != null
            ? $"brainx-machine-v1|{machineGuid}|{uuid ?? ""}"
            : $"brainx-machine-fallback-v1|{Environment.MachineName}|{Environment.UserName}|{uuid ?? ""}");

        string? hardwareHash = uuid != null || boardSerial != null || systemSerial != null
            ? Sha256Hex($"brainx-hardware-v1|{uuid ?? ""}|{boardSerial ?? ""}|{systemSerial ?? ""}")
            : null;

        return new Snapshot(machineId, hardwareHash, machineGuid);
    }

    private static string? ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // 64-bit view: a 32-bit view is redirected to WOW6432Node, which has no MachineGuid.
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = (key?.GetValue("MachineGuid") as string)?.Trim().ToLowerInvariant();
            return Guid.TryParse(value, out var guid) && guid != Guid.Empty ? value : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MachineGuid unavailable: {ex.Message}");
            return null;
        }
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Boards that never had a UUID burned in report all-zero, all-FF, or this
    // well-known sequence ("03000200-0400-0500-0006-000700080009"); thousands of PCs share them.
    private static readonly byte[] DefaultUuid =
        [0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09];

    private static bool IsPlaceholderUuid(byte[] uuid) =>
        uuid.Length != 16 || uuid.All(b => b == uuid[0]) || uuid.AsSpan().SequenceEqual(DefaultUuid);

    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "DEFAULT STRING", "NONE", "N/A", "NA",
        "NOT APPLICABLE", "NOT SPECIFIED", "NOT AVAILABLE", "SYSTEM SERIAL NUMBER", "BASE BOARD SERIAL NUMBER",
        "BASEBOARD SERIAL NUMBER", "SERIAL", "SERIALNUMBER", "SN", "OEM", "O.E.M.", "INVALID", "UNKNOWN",
        "123456789", "1234567890", "0123456789", "XXXXXXXXXX", "CHASSIS SERIAL NUMBER",
    };

    internal static string? CleanSerial(string? serial)
    {
        var value = serial?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length < 4) return null;
        if (PlaceholderSerials.Contains(value)) return null;
        if (value.All(c => c == value[0] || c == ' ' || c == '-' || c == '.')) return null;   // "0000000", "........"
        return value.ToUpperInvariant();
    }

    /// <summary>SMBIOS type 1 (system) and type 2 (baseboard) through GetSystemFirmwareTable.</summary>
    private static class SmbiosReader
    {
        public sealed record Result(byte[]? SystemUuid, string? SystemSerial, string? BoardSerial);

        private const uint Rsmb = 0x52534D42;   // 'RSMB'

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);

        public static Result Read()
        {
            try
            {
                var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
                if (size == 0 || size > 1024 * 1024) return new Result(null, null, null);
                var buffer = new byte[size];
                if (GetSystemFirmwareTable(Rsmb, 0, buffer, size) != size) return new Result(null, null, null);
                return Parse(buffer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SMBIOS unavailable: {ex.Message}");
                return new Result(null, null, null);
            }
        }

        // RawSMBIOSData: 8-byte header (method, major, minor, revision, UInt32
        // length), then the structures. Each: type, formatted length, handle,
        // formatted fields, then a string set ended by a double NUL. String
        // fields hold a 1-based index into that set.
        private static Result Parse(byte[] raw)
        {
            if (raw.Length < 8) return new Result(null, null, null);
            var tableLength = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
            int offset = 8, end = 8 + tableLength;

            byte[]? uuid = null;
            string? systemSerial = null, boardSerial = null;

            while (offset + 4 <= end)
            {
                var type = raw[offset];
                var length = raw[offset + 1];
                if (length < 4 || offset + length > end) break;

                var strings = offset + length;
                var next = strings;
                while (next + 1 < end && !(raw[next] == 0 && raw[next + 1] == 0)) next++;
                next += 2;

                if (type == 1 && length >= 0x19 && uuid == null)
                {
                    uuid = raw.AsSpan(offset + 8, 16).ToArray();
                    systemSerial = ReadString(raw, strings, end, raw[offset + 7]);
                }
                else if (type == 2 && length >= 0x08 && boardSerial == null)
                {
                    boardSerial = ReadString(raw, strings, end, raw[offset + 7]);
                }
                else if (type == 127)
                {
                    break;   // end-of-table marker
                }

                offset = next;
            }

            return new Result(uuid, systemSerial, boardSerial);
        }

        private static string? ReadString(byte[] raw, int start, int end, byte index)
        {
            if (index == 0) return null;
            var position = start;
            for (var i = 1; position < end; i++)
            {
                var stop = position;
                while (stop < end && raw[stop] != 0) stop++;
                if (stop == position) return null;   // reached the terminating NUL
                if (i == index) return Encoding.ASCII.GetString(raw, position, stop - position);
                position = stop + 1;
            }
            return null;
        }
    }
}
