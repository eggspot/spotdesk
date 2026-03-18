using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SpotDesk.Core.Crypto;

public interface IDeviceIdService
{
    string GetDeviceId();
}

public class DeviceIdService : IDeviceIdService
{
    private string? _cached;

    public string GetDeviceId() => _cached ??= Compute();

    private static string Compute()
    {
        string raw;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            raw = File.ReadAllText("/etc/machine-id").Trim();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            raw = GetMacOsSerialNumber();
        }
        else if (OperatingSystem.IsWindows())
        {
            raw = GetWindowsMachineGuid();
        }
        else
        {
            raw = Environment.MachineName;
        }

        // Hash so we never store raw machine identifiers
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw + "spotdesk-v1"));
        return Convert.ToHexString(hash);
    }

    private static string GetMacOsSerialNumber()
    {
        // IOKit IOPlatformSerialNumber via P/Invoke
        var entry = IOKit.IOServiceGetMatchingService(0, IOKit.IOServiceMatching("IOPlatformExpertDevice"));
        if (entry == 0) return Guid.NewGuid().ToString();

        try
        {
            var key = CFString.Create("IOPlatformSerialNumber");
            var value = IOKit.IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0);
            return CFString.GetValue(value) ?? Guid.NewGuid().ToString();
        }
        finally
        {
            IOKit.IOObjectRelease(entry);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetWindowsMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString() ?? Guid.NewGuid().ToString();
    }
}

// Minimal IOKit P/Invoke stubs — only called at runtime on macOS
internal static partial class IOKit
{
    [LibraryImport("libIOKit.dylib", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint IOServiceGetMatchingService(uint masterPort, IntPtr matching);

    [LibraryImport("libIOKit.dylib", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr IOServiceMatching(string name);

    [LibraryImport("libIOKit.dylib")]
    internal static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport("libIOKit.dylib")]
    internal static partial int IOObjectRelease(uint obj);
}

internal static partial class CFString
{
    [LibraryImport("CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CFStringCreateWithCharacters(IntPtr alloc, string chars, nint numChars);

    internal static IntPtr Create(string value) =>
        CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length);

    [LibraryImport("CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringGetCStringPtr(IntPtr theString, uint encoding);

    internal static string? GetValue(IntPtr cfString)
    {
        var ptr = CFStringGetCStringPtr(cfString, 0x08000100 /* kCFStringEncodingUTF8 */);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }
}
