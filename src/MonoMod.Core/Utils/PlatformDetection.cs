﻿using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonoMod.Core.Utils {
    public static class PlatformDetection {
        #region OS/Arch
        private static int platInitState;
        private static OSKind os;
        private static ArchitectureKind arch;

        private static void EnsurePlatformInfoInitialized() {
            if (platInitState != 0) {
                return;
            }

            // we're actually OK with invoking this multiple times on different threads, because it
            // *should* give the same results each time.
            var detected = DetectPlatformInfo();
            os = detected.OS;
            arch = detected.Arch;
            Thread.MemoryBarrier();
            _ = Interlocked.Exchange(ref platInitState, 1);
        }

        public static OSKind OS {
            get {
                EnsurePlatformInfoInitialized();
                return os;
            }
        }

        public static ArchitectureKind Architecture {
            get {
                EnsurePlatformInfoInitialized();
                return arch;
            }
        }

        private static (OSKind OS, ArchitectureKind Arch) DetectPlatformInfo() {
            OSKind os = OSKind.Unknown;
            ArchitectureKind arch = ArchitectureKind.Unknown;

            {
                // For old Mono, get from a private property to accurately get the platform.
                // static extern PlatformID Platform
                PropertyInfo? p_Platform = typeof(Environment).GetProperty("Platform", BindingFlags.NonPublic | BindingFlags.Static);
                string? platID;
                if (p_Platform != null) {
                    platID = p_Platform.GetValue(null, null)?.ToString();
                } else {
                    // For .NET and newer Mono, use the usual value.
                    platID = Environment.OSVersion.Platform.ToString();
                }
                platID = platID?.ToUpperInvariant() ?? "";

                if (platID.Contains("WIN", StringComparison.Ordinal)) {
                    os = OSKind.Windows;
                } else if (platID.Contains("MAC", StringComparison.Ordinal) || platID.Contains("OSX", StringComparison.Ordinal)) {
                    os = OSKind.OSX;
                } else if (platID.Contains("LIN", StringComparison.Ordinal)) {
                    os = OSKind.Linux;
                } else if (platID.Contains("BSD", StringComparison.Ordinal)) {
                    os = OSKind.BSD;
                } else if (platID.Contains("UNIX", StringComparison.Ordinal)) {
                    os = OSKind.Posix;
                }
            }

            // Try to use OS-specific methods of determining OS/Arch info
            if (os == OSKind.Windows) {
                DetectInfoWindows(ref os, ref arch);
            } else if ((os & OSKind.Posix) != 0) {
                DetectInfoPosix(ref os, ref arch);
            }

            if (os == OSKind.Unknown) {
                // Welp.

            } else if (os == OSKind.Linux &&
                Directory.Exists("/data") && File.Exists("/system/build.prop")
            ) {
                os = OSKind.Android;
            } else if (os == OSKind.Posix &&
                Directory.Exists("/Applications") && Directory.Exists("/System") &&
                Directory.Exists("/User") && !Directory.Exists("/Users")
            ) {
                os = OSKind.IOS;
            } else if (os == OSKind.Windows &&
                CheckWine()
            ) {
                // Sorry, Wine devs, but you might want to look at DetourRuntimeNETPlatform.
                os = OSKind.Wine;
            }

            MMDbgLog.Log($"Platform info: {os} {arch}");
            return (os, arch);
        }


        #region OS-specific arch detection


        private static unsafe int PosixUname(OSKind os, byte* buf) {
            static int Libc(byte* buf) => Interop.Unix.Uname(buf);
            static int Osx(byte* buf) => Interop.OSX.Uname(buf);
            return os == OSKind.OSX ? Osx(buf) : Libc(buf);
        }

        private static unsafe string GetCString(ReadOnlySpan<byte> buffer, out int nullByte) {
            fixed (byte* buf = buffer) {
                return Marshal.PtrToStringAnsi((IntPtr)buf, nullByte = buffer.IndexOf((byte)0));
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "This method failing to detect information should not be a hard error. Exceptions thrown because of " +
            "issues with P/Invoke and the like should not prevent the OS and arch info from being populated.")]
        private static void DetectInfoPosix(ref OSKind os, ref ArchitectureKind arch) {
            try {
                // we want to call libc's uname() function
                // the fields we're interested in are sysname and machine, which are field 0 and 4 respectively.

                // Unfortunately for us, the size of the utsname struct depends heavily ont he platform. Fortunately for us,
                // the returned data is all null-terminated strings. Hopefully, the unused data in the fields are filled with
                // zeroes or untouched, which will allow us to easily scan for the strings.

                // Because the amount of space required for this syscall is unknown, we'll just allocate 6*513 bytes for it, and scan.

                Span<byte> buffer = new byte[6 * 513];
                unsafe {
                    fixed (byte* bufPtr = buffer) {
                        if (PosixUname(os, bufPtr) < 0) {
                            // uh-oh, uname failed. Log the error if we can  get it and return normally.
                            string msg = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                            MMDbgLog.Log($"uname() syscall failed! {msg}");
                            return;
                        }
                    }
                }

                // buffer now contains a bunch of null-terminated strings
                // the first of these is the kernel name

                var kernelName = GetCString(buffer, out var nullByteOffs).ToUpperInvariant();
                buffer = buffer.Slice(nullByteOffs);

                for (int i = 0; i < 4; i++) { // we want to jump to string 4, but we've already skipped the text of the first
                    if (i != 0) {
                        // skip a string
                        nullByteOffs = buffer.IndexOf((byte)0);
                        buffer = buffer.Slice(nullByteOffs);
                    }
                    // then advance to the next one
                    int j = 0;
                    for (; j < buffer.Length && buffer[j] == 0; j++) { }
                    buffer = buffer.Slice(j);
                }

                // and here we find the machine field
                var machineName = GetCString(buffer, out _).ToUpperInvariant();

                MMDbgLog.Log($"uname() call returned {kernelName} {machineName}");

                // now we want to inspect the fields and select something useful from them
                if (kernelName.Contains("LINUX", StringComparison.Ordinal)) { // A Linux kernel
                    os = OSKind.Linux;
                } else if (kernelName.Contains("DARWIN", StringComparison.Ordinal)) { // the MacOS kernel
                    os = OSKind.OSX;
                } else if (kernelName.Contains("BSD", StringComparison.Ordinal)) { // a BSD kernel
                    // Note: I'm fairly sure that the different BSDs vary quite a lot, so it may be worth checking with more specificity here
                    os = OSKind.BSD;
                }
                // TODO: fill in other known kernel names

                if (machineName.Contains("X86_64", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.x86_64;
                } else if (machineName.Contains("AMD64", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.x86_64;
                } else if (machineName.Contains("X86", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.x86;
                } else if (machineName.Contains("AARCH64", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.Arm64;
                } else if (machineName.Contains("ARM64", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.Arm64;
                } else if (machineName.Contains("ARM", StringComparison.Ordinal)) {
                    arch = ArchitectureKind.Arm;
                }
                // TODO: fill in other values for machine

                MMDbgLog.Log($"uname() detected architecture info: {os} {arch}");
            } catch (Exception e) {
                MMDbgLog.Log($"Error trying to detect info on POSIX-like system");
                MMDbgLog.Log(e.ToString());
                return;
            }
        }

        private static void DetectInfoWindows(ref OSKind os, ref ArchitectureKind arch) {
            Interop.Windows.GetSystemInfo(out var sysInfo);

            // we don't update OS here, because Windows

            // https://docs.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-system_info
            arch = sysInfo.wProcessorArchitecture switch {
                9 => ArchitectureKind.x86_64,
                5 => ArchitectureKind.Arm,
                12 => ArchitectureKind.Arm64,
                6 => arch, // Itanium. Fuck Itanium.
                0 => ArchitectureKind.x86,
                _ => ArchitectureKind.Unknown
            };
        }
        #endregion

        // Separated method so that this P/Invoke mess doesn't error out on non-Windows.
        private static bool CheckWine() {
            // wine_get_version can be missing because of course it can.
            // General purpose env var.
            string? env = Environment.GetEnvironmentVariable("MONOMOD_WINE");
            if (env == "1")
                return true;
            if (env == "0")
                return false;

            // The "Dalamud" plugin loader for FFXIV uses Harmony, coreclr and wine. What a nice combo!
            // At least they went ahead and provide an environment variable for everyone to check.
            // See https://github.com/goatcorp/FFXIVQuickLauncher/blob/8685db4a0e8ec53235fb08cd88aded7c7061d9fb/src/XIVLauncher/Settings/EnvironmentSettings.cs
            env = Environment.GetEnvironmentVariable("XL_WINEONLINUX")?.ToUpperInvariant();
            if (env == "TRUE")
                return true;
            if (env == "FALSE")
                return false;

            IntPtr ntdll = Interop.Windows.GetModuleHandle("ntdll.dll");
            if (ntdll != IntPtr.Zero && Interop.Windows.GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero)
                return true;

            return false;
        }
        #endregion

        #region Runtime
        private static int runtimeInitState;
        private static RuntimeKind runtime;
        private static Version? runtimeVersion;

        [MemberNotNull(nameof(runtimeVersion))]
        private static void EnsureRuntimeInitialized() {
            if (runtimeInitState != 0) {
                if (runtimeVersion is null) {
                    throw new InvalidOperationException("Despite runtimeInitState being set, runtimeVersion was somehow null");
                }
                return;
            }

            var runtimeInfo = DetermineRuntimeInfo();
            runtime = runtimeInfo.Rt;
            runtimeVersion = runtimeInfo.Ver;

            Thread.MemoryBarrier();
            _ = Interlocked.Exchange(ref runtimeInitState, 1);
        }

        public static RuntimeKind Runtime {
            get {
                EnsureRuntimeInitialized();
                return runtime;
            }
        }

        public static Version RuntimeVersion {
            get {
                EnsureRuntimeInitialized();
                return runtimeVersion;
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "In old versions of Framework, there is no Version.TryParse, and so we must call the constructor " +
            "and catch any exception that may ocurr.")]
        private static (RuntimeKind Rt, Version Ver) DetermineRuntimeInfo() {
            var runtime = RuntimeKind.Unknown;
            Version? version = null; // an unknown version

            bool isMono =
                // This is what everyone expects.
                Type.GetType("Mono.Runtime") != null ||
                // .NET Core BCL running on Mono, see https://github.com/dotnet/runtime/blob/main/src/libraries/Common/tests/TestUtilities/System/PlatformDetection.cs
                Type.GetType("Mono.RuntimeStructs") != null;

            bool isCoreBcl = typeof(object).Assembly.GetName().Name == "System.Private.CoreLib";

            if (isMono) {
                runtime = RuntimeKind.Mono;
            } else if (isCoreBcl && !isMono) {
                runtime = RuntimeKind.CoreCLR;
            } else {
                runtime = RuntimeKind.Framework;
            }

            MMDbgLog.Log($"IsMono: {isMono}, IsCoreBcl: {isCoreBcl}");

            var sysVer = Environment.Version;
            MMDbgLog.Log($"Returned system version: {sysVer}");

            // RuntimeInformation is present in FX 4.7.1+ and all netstandard and Core releases, however its location varies
            // In FX, it is in mscorlib
            Type? rti = Type.GetType("System.Runtime.InteropServices.RuntimeInformation");
            // however, in Core, its in System.Runtime.InteropServices.RuntimeInformation
            rti ??= Type.GetType("System.Runtime.InteropServices.RuntimeInformation, System.Runtime.InteropServices.RuntimeInformation");

            // FrameworkDescription is a string which (is supposed to) describe the runtime
            var fxDesc = (string?) rti?.GetProperty("FrameworkDescription")?.GetValue(null, null);
            MMDbgLog.Log($"FrameworkDescription: {fxDesc??"(null)"}");

            if (fxDesc is not null) {
                // If we could get FrameworkDescription, we want to check the start of it for each known runtime
                const string MonoPrefix = "Mono ";
                const string NetCore = ".NET Core ";
                const string NetFramework = ".NET Framework ";
                const string Net5Plus = ".NET ";

                int prefixLength;
                if (fxDesc.StartsWith(MonoPrefix, StringComparison.Ordinal)) {
                    runtime = RuntimeKind.Mono;
                    prefixLength = MonoPrefix.Length;
                } else if (fxDesc.StartsWith(NetCore, StringComparison.Ordinal)) {
                    runtime = RuntimeKind.CoreCLR;
                    prefixLength = NetCore.Length;
                } else if (fxDesc.StartsWith(NetFramework, StringComparison.Ordinal)) {
                    runtime = RuntimeKind.Framework;
                    prefixLength = NetFramework.Length;
                } else if (fxDesc.StartsWith(Net5Plus, StringComparison.Ordinal)) {
                    runtime = RuntimeKind.CoreCLR;
                    prefixLength = Net5Plus.Length;
                } else {
                    runtime = RuntimeKind.Unknown; // even if we think we already know, if we get to this point, explicitly set to unknown
                    // this *likely* means that this is some new/obscure runtime
                    prefixLength = fxDesc.Length;
                }

                // find the next space, if any, because everything up to that should be the version
                var space = fxDesc.IndexOf(' ', prefixLength);
                if (space < 0)
                    space = fxDesc.Length;

                var versionString = fxDesc.Substring(prefixLength, space - prefixLength);

                try {
                    version = new Version(versionString);
                } catch (Exception e) {
                    MMDbgLog.Log("Invalid version string pulled from FrameworkDescription");
                    MMDbgLog.Log(e.ToString());
                }

                // TODO: map .NET Core 2.1 version to something saner
            }

            // only on old Framework is this anything *close* to reliable
            if (runtime == RuntimeKind.Framework)
                version ??= sysVer;

            // TODO: map strange (read: Framework) versions correctly

            MMDbgLog.Log($"Detected runtime: {runtime} {version?.ToString()??"(null)"}");

            return (runtime, version ?? new Version(0, 0));
        }

        #endregion
    }
}