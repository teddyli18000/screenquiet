using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ScreenQuiet
{
    /// <summary>
    /// Reports physical mouse, keyboard, and touchpad activity through Raw Input.
    /// The class never reads or exposes keyboard or mouse input values.
    /// </summary>
    public sealed class PhysicalInput : IDisposable
    {
        private const int WmInput = 0x00FF;
        private const int WmInputDeviceChange = 0x00FE;

        private const uint RidInput = 0x10000003;
        private const uint RidiDeviceName = 0x20000007;
        private const uint RidiDeviceInfo = 0x2000000B;
        private const uint RidiPreparsedData = 0x20000005;

        private const uint RimTypeMouse = 0;
        private const uint RimTypeKeyboard = 1;
        private const uint RimTypeHid = 2;
        private const uint RimInput = 0;
        private const uint GidcArrival = 1;
        private const uint GidcRemoval = 2;

        private const uint RidevRemove = 0x00000001;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevDevNotify = 0x00002000;

        private const ushort GenericDesktopPage = 0x01;
        private const ushort MouseUsage = 0x02;
        private const ushort KeyboardUsage = 0x06;
        private const ushort DigitizersPage = 0x0D;
        private const ushort TouchpadUsage = 0x05;
        private const ushort ContactCountUsage = 0x54;

        private const uint HidpInput = 0;
        private const uint HidpStatusSuccess = 0x00110000;
        private const uint InvalidApiResult = 0xFFFFFFFF;

        private readonly IntPtr _windowHandle;
        private readonly Action _onPhysicalInput;
        private readonly Dictionary<IntPtr, DeviceDescriptor> _devices;
        private readonly Dictionary<IntPtr, bool> _touchActive;
        private bool _registered;
        private bool _disposed;

        /// <summary>
        /// Raised with only the coarse source name: "Mouse", "Keyboard", or "Touchpad".
        /// No key, button, coordinate, or report values are included.
        /// </summary>
        public event Action<string> PhysicalInputSourceDetected;

        public PhysicalInput(IntPtr windowHandle, Action onPhysicalInput)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("A valid window handle is required.", "windowHandle");
            if (onPhysicalInput == null)
                throw new ArgumentNullException("onPhysicalInput");

            _windowHandle = windowHandle;
            _onPhysicalInput = onPhysicalInput;
            _devices = new Dictionary<IntPtr, DeviceDescriptor>();
            _touchActive = new Dictionary<IntPtr, bool>();

            RawInputDevice[] devices = CreateRegistrations(windowHandle, RidevInputSink | RidevDevNotify);
            if (!NativeMethods.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf(typeof(RawInputDevice))))
            {
                int error = Marshal.GetLastWin32Error();
                TryUnregister(windowHandle);
                throw new Win32Exception(error, "Could not register Raw Input devices.");
            }

            _registered = true;
        }

        /// <summary>
        /// Returns a privacy-safe summary of currently enumerated Raw Input devices.
        /// Device paths and unique instance identifiers are intentionally omitted.
        /// </summary>
        public static string[] DescribeDevices()
        {
            List<RawInputDeviceListEntry> entries = EnumerateRawInputDevices();
            List<string> descriptions = new List<string>();

            for (int i = 0; i < entries.Count; i++)
            {
                DeviceDescriptor descriptor;
                if (!TryCreateDeviceDescriptor(entries[i].Device, out descriptor))
                {
                    descriptions.Add("Unclassified Raw Input device");
                    continue;
                }

                string kind = GetDeviceKind(descriptor);
                string usage = descriptor.Type == RimTypeHid
                    ? " usage " + Hex(descriptor.UsagePage) + "/" + Hex(descriptor.Usage)
                    : String.Empty;
                string classification = descriptor.IsPhysical
                    ? "physical " + descriptor.Bus
                    : "ignored: " + descriptor.RejectionReason;

                if (kind == "Touchpad" && descriptor.IsPhysical)
                {
                    bool hasContactCount = SupportsContactCount(descriptor);
                    classification += hasContactCount
                        ? "; ContactCount supported"
                        : "; ContactCount unavailable";
                    FreePreparsedData(descriptor);
                }

                descriptions.Add(kind + usage + ": " + classification);
            }

            return descriptions.ToArray();
        }

        /// <summary>
        /// Call from the window procedure for messages delivered to the registered window.
        /// For WM_INPUT foreground messages, this method also performs the required default
        /// processing so Windows can release the Raw Input buffer.
        /// </summary>
        public void ProcessMessage(ref Message message)
        {
            if (_disposed)
                return;

            if (message.Msg == WmInput)
            {
                ProcessRawInputMessage(ref message);
            }
            else if (message.Msg == WmInputDeviceChange)
            {
                ProcessDeviceChange(message.WParam, message.LParam);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_registered)
            {
                TryUnregister(_windowHandle);
                _registered = false;
            }

            foreach (KeyValuePair<IntPtr, DeviceDescriptor> item in _devices)
                FreePreparsedData(item.Value);
            _devices.Clear();
            _touchActive.Clear();
        }

        private void ProcessRawInputMessage(ref Message message)
        {
            try
            {
                ProcessRawInput(message.LParam);
            }
            finally
            {
                long inputCode = message.WParam.ToInt64() & 0xFF;
                if (inputCode == RimInput)
                {
                    message.Result = NativeMethods.DefWindowProc(
                        message.HWnd,
                        (uint)message.Msg,
                        message.WParam,
                        message.LParam);
                }
            }
        }

        private void ProcessRawInput(IntPtr rawInputHandle)
        {
            if (rawInputHandle == IntPtr.Zero)
                return;

            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            uint queryResult = NativeMethods.GetRawInputData(
                rawInputHandle,
                RidInput,
                IntPtr.Zero,
                ref size,
                headerSize);

            if (queryResult == InvalidApiResult || size < headerSize || size > 1048576)
                return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint bufferSize = size;
                uint read = NativeMethods.GetRawInputData(
                    rawInputHandle,
                    RidInput,
                    buffer,
                    ref bufferSize,
                    headerSize);

                if (read == InvalidApiResult || read < headerSize)
                    return;

                RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(
                    buffer,
                    typeof(RawInputHeader));
                if (header.Device == IntPtr.Zero || header.Size < headerSize || header.Size > read)
                    return;

                DeviceDescriptor descriptor = GetDeviceDescriptor(header.Device);
                if (descriptor == null || !descriptor.IsPhysical || descriptor.Type != header.Type)
                    return;

                if (header.Type == RimTypeMouse && descriptor.Type == RimTypeMouse)
                {
                    if (HasMouseActivity(buffer, (uint)read, headerSize))
                        NotifyPhysicalInput("Mouse");
                }
                else if (header.Type == RimTypeKeyboard && descriptor.Type == RimTypeKeyboard)
                {
                    // Releasing the Fn/F9 trigger must not wake the panel again.
                    // Only a new physical key-down is a wake action; no key value
                    // is read or retained.
                    if (read >= headerSize + 16 && (Marshal.ReadInt16(buffer, (int)headerSize + 2) & 1) == 0)
                        NotifyPhysicalInput("Keyboard");
                }
                else if (header.Type == RimTypeHid &&
                    descriptor.Type == RimTypeHid &&
                    descriptor.UsagePage == DigitizersPage &&
                    descriptor.Usage == TouchpadUsage)
                {
                    ProcessTouchpadInput(buffer, (uint)read, headerSize, descriptor);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool HasMouseActivity(IntPtr rawInputBuffer, uint rawInputSize, uint headerSize)
        {
            const int RawMouseSize = 24;
            long mouseOffset = headerSize;
            if (rawInputSize < mouseOffset + RawMouseSize)
                return false;

            ushort buttonFlags = unchecked((ushort)Marshal.ReadInt16(
                rawInputBuffer,
                checked((int)mouseOffset + 4)));
            int deltaX = Marshal.ReadInt32(rawInputBuffer, checked((int)mouseOffset + 12));
            int deltaY = Marshal.ReadInt32(rawInputBuffer, checked((int)mouseOffset + 16));

            return buttonFlags != 0 || deltaX != 0 || deltaY != 0;
        }

        private void ProcessTouchpadInput(
            IntPtr rawInputBuffer,
            uint rawInputSize,
            uint headerSize,
            DeviceDescriptor descriptor)
        {
            long hidOffset = headerSize;
            if (rawInputSize < hidOffset + 8)
                return;

            int reportSizeValue = Marshal.ReadInt32(rawInputBuffer, (int)hidOffset);
            int reportCountValue = Marshal.ReadInt32(rawInputBuffer, (int)hidOffset + 4);
            if (reportSizeValue <= 0 || reportCountValue <= 0 || reportCountValue > 1024)
                return;

            long reportBytes = (long)reportSizeValue * (long)reportCountValue;
            if (reportBytes > rawInputSize - hidOffset - 8)
                return;

            if (!SupportsContactCount(descriptor))
                return;

            long reportsOffset = hidOffset + 8;
            for (int i = 0; i < reportCountValue; i++)
            {
                IntPtr report = IntPtr.Add(
                    rawInputBuffer,
                    checked((int)(reportsOffset + ((long)i * reportSizeValue))));
                uint contactCount;
                uint status = NativeMethods.HidPGetUsageValue(
                    HidpInput,
                    DigitizersPage,
                    0,
                    ContactCountUsage,
                    out contactCount,
                    descriptor.PreparsedData,
                    report,
                    (uint)reportSizeValue);

                if (status != HidpStatusSuccess)
                    continue;

                bool wasActive;
                _touchActive.TryGetValue(descriptor.Handle, out wasActive);
                if (contactCount > 0)
                {
                    _touchActive[descriptor.Handle] = true;
                    if (!wasActive)
                        NotifyPhysicalInput("Touchpad");
                }
                else
                {
                    _touchActive[descriptor.Handle] = false;
                }
            }
        }

        private void ProcessDeviceChange(IntPtr changeCodeValue, IntPtr deviceHandle)
        {
            if (deviceHandle == IntPtr.Zero)
                return;

            long changeCode = changeCodeValue.ToInt64() & 0xFFFFFFFF;
            if (changeCode == GidcArrival)
            {
                RemoveDevice(deviceHandle);
                GetDeviceDescriptor(deviceHandle);
            }
            else if (changeCode == GidcRemoval)
            {
                RemoveDevice(deviceHandle);
            }
        }

        private DeviceDescriptor GetDeviceDescriptor(IntPtr deviceHandle)
        {
            if (deviceHandle == IntPtr.Zero)
                return null;

            DeviceDescriptor descriptor;
            if (_devices.TryGetValue(deviceHandle, out descriptor))
                return descriptor;

            if (!TryCreateDeviceDescriptor(deviceHandle, out descriptor))
                return null;

            descriptor.Handle = deviceHandle;
            _devices[deviceHandle] = descriptor;
            return descriptor;
        }

        private void RemoveDevice(IntPtr deviceHandle)
        {
            DeviceDescriptor descriptor;
            if (_devices.TryGetValue(deviceHandle, out descriptor))
            {
                FreePreparsedData(descriptor);
                _devices.Remove(deviceHandle);
            }
            _touchActive.Remove(deviceHandle);
        }

        private void NotifyPhysicalInput(string source)
        {
            Action<string> sourceHandler = PhysicalInputSourceDetected;
            if (sourceHandler != null)
            {
                try
                {
                    sourceHandler(source);
                }
                catch
                {
                    // Diagnostic handlers must not prevent the physical-input callback.
                }
            }

            _onPhysicalInput();
        }

        private static RawInputDevice[] CreateRegistrations(IntPtr windowHandle, uint flags)
        {
            RawInputDevice[] devices = new RawInputDevice[3];
            devices[0] = new RawInputDevice();
            devices[0].UsagePage = GenericDesktopPage;
            devices[0].Usage = MouseUsage;
            devices[0].Flags = flags;
            devices[0].Target = windowHandle;

            devices[1] = new RawInputDevice();
            devices[1].UsagePage = GenericDesktopPage;
            devices[1].Usage = KeyboardUsage;
            devices[1].Flags = flags;
            devices[1].Target = windowHandle;

            devices[2] = new RawInputDevice();
            devices[2].UsagePage = DigitizersPage;
            devices[2].Usage = TouchpadUsage;
            devices[2].Flags = flags;
            devices[2].Target = windowHandle;
            return devices;
        }

        private static void TryUnregister(IntPtr windowHandle)
        {
            RawInputDevice[] devices = CreateRegistrations(IntPtr.Zero, RidevRemove);
            NativeMethods.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf(typeof(RawInputDevice)));
        }

        private static bool TryCreateDeviceDescriptor(
            IntPtr deviceHandle,
            out DeviceDescriptor descriptor)
        {
            descriptor = null;
            if (deviceHandle == IntPtr.Zero)
                return false;

            RawInputDeviceInfo deviceInfo;
            if (!TryGetRawInputDeviceInfo(deviceHandle, out deviceInfo))
                return false;

            string path = TryGetDevicePath(deviceHandle);
            string bus;
            string rejectionReason;
            bool physical = ClassifyPhysicalDevice(path, out bus, out rejectionReason);

            descriptor = new DeviceDescriptor();
            descriptor.Handle = deviceHandle;
            descriptor.Type = deviceInfo.Type;
            if (deviceInfo.Type == RimTypeHid)
            {
                descriptor.UsagePage = deviceInfo.Data.Hid.UsagePage;
                descriptor.Usage = deviceInfo.Data.Hid.Usage;
            }
            descriptor.DevicePath = path;
            descriptor.IsPhysical = physical;
            descriptor.Bus = bus;
            descriptor.RejectionReason = rejectionReason;
            return true;
        }

        private static bool TryGetRawInputDeviceInfo(
            IntPtr deviceHandle,
            out RawInputDeviceInfo deviceInfo)
        {
            deviceInfo = new RawInputDeviceInfo();
            int infoSize = Marshal.SizeOf(typeof(RawInputDeviceInfo));
            IntPtr memory = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.WriteInt32(memory, infoSize);
                uint size = (uint)infoSize;
                uint result = NativeMethods.GetRawInputDeviceInfo(
                    deviceHandle,
                    RidiDeviceInfo,
                    memory,
                    ref size);
                if (result == InvalidApiResult)
                    return false;

                deviceInfo = (RawInputDeviceInfo)Marshal.PtrToStructure(
                    memory,
                    typeof(RawInputDeviceInfo));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private static string TryGetDevicePath(IntPtr deviceHandle)
        {
            uint characters = 0;
            uint result = NativeMethods.GetRawInputDeviceInfo(
                deviceHandle,
                RidiDeviceName,
                IntPtr.Zero,
                ref characters);
            if (result == InvalidApiResult || characters == 0 || characters > 32768)
                return null;

            int bytes = checked((int)characters * 2);
            IntPtr memory = Marshal.AllocHGlobal(bytes);
            try
            {
                uint capacity = characters;
                result = NativeMethods.GetRawInputDeviceInfo(
                    deviceHandle,
                    RidiDeviceName,
                    memory,
                    ref capacity);
                if (result == InvalidApiResult)
                    return null;

                return Marshal.PtrToStringUni(memory);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        private static bool ClassifyPhysicalDevice(
            string devicePath,
            out string bus,
            out string rejectionReason)
        {
            bus = null;
            rejectionReason = "unknown PnP ancestry";
            if (String.IsNullOrEmpty(devicePath))
                return false;

            if (devicePath.IndexOf("UUYC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rejectionReason = "UUYC source";
                return false;
            }

            string instanceId = GetInstanceIdFromPath(devicePath);
            if (String.IsNullOrEmpty(instanceId))
            {
                rejectionReason = "unrecognized device path";
                return false;
            }

            if (IsVirtualTreeId(instanceId))
            {
                rejectionReason = "ROOT/SWD virtual tree";
                return false;
            }

            uint devInst;
            uint result = NativeMethods.CM_Locate_DevNodeW(
                out devInst,
                new StringBuilder(instanceId),
                0);
            if (result != 0)
            {
                rejectionReason = "PnP devnode unavailable";
                return false;
            }

            string acceptedBus = null;
            uint current = devInst;
            for (int depth = 0; depth < 64; depth++)
            {
                string currentId = GetDevNodeId(current);
                if (String.IsNullOrEmpty(currentId))
                    break;

                if (currentId.IndexOf("UUYC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rejectionReason = "UUYC source";
                    return false;
                }
                if (acceptedBus == null)
                    acceptedBus = GetAllowedPhysicalBus(currentId);

                if (IsVirtualTreeId(currentId))
                {
                    if (acceptedBus == null)
                    {
                        rejectionReason = "ROOT/SWD virtual tree";
                        return false;
                    }

                    // A physical bus ancestor already identifies this device. A later
                    // machine-tree root is not the device's enumerating source.
                    break;
                }

                uint parent;
                if (NativeMethods.CM_Get_Parent(out parent, current, 0) != 0)
                    break;
                current = parent;
            }

            if (acceptedBus == null)
            {
                rejectionReason = "unrecognized PnP ancestry";
                return false;
            }

            bus = acceptedBus;
            rejectionReason = null;
            return true;
        }

        private static string GetInstanceIdFromPath(string devicePath)
        {
            string path = devicePath;
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
                path = path.Substring(4);

            int interfaceGuid = path.IndexOf("#{", StringComparison.OrdinalIgnoreCase);
            if (interfaceGuid >= 0)
                path = path.Substring(0, interfaceGuid);

            path = path.Replace('#', '\\');
            if (path.IndexOf('\\') < 0)
                return null;
            return path;
        }

        private static bool IsVirtualTreeId(string deviceInstanceId)
        {
            return deviceInstanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                deviceInstanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAllowedPhysicalBus(string deviceInstanceId)
        {
            if (deviceInstanceId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
                return "ACPI";
            if (deviceInstanceId.StartsWith("I2C\\", StringComparison.OrdinalIgnoreCase))
                return "I2C";
            if (deviceInstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                return "USB";
            if (deviceInstanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                deviceInstanceId.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase) ||
                deviceInstanceId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) ||
                deviceInstanceId.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase))
                return "Bluetooth";
            return null;
        }

        private static string GetDevNodeId(uint devInst)
        {
            StringBuilder buffer = new StringBuilder(512);
            if (NativeMethods.CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0) != 0)
                return null;
            return buffer.ToString();
        }

        private static List<RawInputDeviceListEntry> EnumerateRawInputDevices()
        {
            uint entrySize = (uint)Marshal.SizeOf(typeof(RawInputDeviceListEntry));
            uint count = 0;
            uint result = NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
            if (result == InvalidApiResult)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Raw Input devices.");
            if (count == 0)
                return new List<RawInputDeviceListEntry>();

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (count > 65536)
                    throw new InvalidOperationException("The Raw Input device count is outside the expected range.");

                int bytes = checked((int)(count * entrySize));
                IntPtr memory = Marshal.AllocHGlobal(bytes);
                try
                {
                    uint actualCount = count;
                    result = NativeMethods.GetRawInputDeviceList(memory, ref actualCount, entrySize);
                    if (result != InvalidApiResult)
                    {
                        List<RawInputDeviceListEntry> entries = new List<RawInputDeviceListEntry>();
                        int structSize = Marshal.SizeOf(typeof(RawInputDeviceListEntry));
                        for (int i = 0; i < actualCount; i++)
                        {
                            IntPtr item = IntPtr.Add(memory, checked(i * structSize));
                            entries.Add((RawInputDeviceListEntry)Marshal.PtrToStructure(
                                item,
                                typeof(RawInputDeviceListEntry)));
                        }
                        return entries;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }

                count = 0;
                result = NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
                if (result == InvalidApiResult)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Raw Input devices.");
                if (count == 0)
                    return new List<RawInputDeviceListEntry>();
            }

            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raw Input devices changed during enumeration.");
        }

        private static bool SupportsContactCount(DeviceDescriptor descriptor)
        {
            if (descriptor.ContactCountChecked)
                return descriptor.HasContactCount;

            if (!EnsurePreparsedData(descriptor))
                return false;

            HidpCaps caps = new HidpCaps();
            uint status = NativeMethods.HidPGetCaps(descriptor.PreparsedData, ref caps);
            if (status != HidpStatusSuccess)
                return false;

            ushort capCount = caps.NumberInputValueCaps;
            if (capCount == 0)
            {
                descriptor.ContactCountChecked = true;
                return false;
            }

            if (capCount > 4096)
                return false;

            HidpValueCaps[] valueCaps = new HidpValueCaps[capCount];
            ushort returnedCount = capCount;
            status = NativeMethods.HidPGetValueCaps(
                HidpInput,
                valueCaps,
                ref returnedCount,
                descriptor.PreparsedData);
            if (status != HidpStatusSuccess)
                return false;

            int filled = Math.Min((int)returnedCount, valueCaps.Length);
            for (int i = 0; i < filled; i++)
            {
                HidpValueCaps valueCap = valueCaps[i];
                if (valueCap.UsagePage != DigitizersPage)
                    continue;

                if (valueCap.IsRange != 0)
                {
                    if (valueCap.Data.Range.UsageMin <= ContactCountUsage &&
                        valueCap.Data.Range.UsageMax >= ContactCountUsage)
                    {
                        descriptor.HasContactCount = true;
                        break;
                    }
                }
                else if (valueCap.Data.NotRange.Usage == ContactCountUsage)
                {
                    descriptor.HasContactCount = true;
                    break;
                }
            }

            descriptor.ContactCountChecked = true;
            return descriptor.HasContactCount;
        }

        private static bool EnsurePreparsedData(DeviceDescriptor descriptor)
        {
            if (descriptor.PreparsedData != IntPtr.Zero)
                return true;

            uint size = 0;
            uint result = NativeMethods.GetRawInputDeviceInfo(
                descriptor.Handle,
                RidiPreparsedData,
                IntPtr.Zero,
                ref size);
            if (result == InvalidApiResult || size == 0 || size > 1048576)
                return false;

            IntPtr memory = Marshal.AllocHGlobal((int)size);
            uint capacity = size;
            result = NativeMethods.GetRawInputDeviceInfo(
                descriptor.Handle,
                RidiPreparsedData,
                memory,
                ref capacity);
            if (result == InvalidApiResult)
            {
                Marshal.FreeHGlobal(memory);
                return false;
            }

            descriptor.PreparsedData = memory;
            return true;
        }

        private static void FreePreparsedData(DeviceDescriptor descriptor)
        {
            if (descriptor != null && descriptor.PreparsedData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descriptor.PreparsedData);
                descriptor.PreparsedData = IntPtr.Zero;
            }
        }

        private static string GetDeviceKind(DeviceDescriptor descriptor)
        {
            if (descriptor.Type == RimTypeMouse)
                return "Mouse";
            if (descriptor.Type == RimTypeKeyboard)
                return "Keyboard";
            if (descriptor.Type == RimTypeHid &&
                descriptor.UsagePage == DigitizersPage &&
                descriptor.Usage == TouchpadUsage)
                return "Touchpad";
            return "Other Raw Input device";
        }

        private static string Hex(ushort value)
        {
            return value.ToString("X2");
        }

        private sealed class DeviceDescriptor
        {
            public IntPtr Handle;
            public uint Type;
            public ushort UsagePage;
            public ushort Usage;
            public string DevicePath;
            public bool IsPhysical;
            public string Bus;
            public string RejectionReason;
            public IntPtr PreparsedData;
            public bool ContactCountChecked;
            public bool HasContactCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDeviceListEntry
        {
            public IntPtr Device;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDeviceInfo
        {
            public uint Size;
            public uint Type;
            public RawInputDeviceInfoUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct RawInputDeviceInfoUnion
        {
            [FieldOffset(0)]
            public RawInputMouseInfo Mouse;
            [FieldOffset(0)]
            public RawInputKeyboardInfo Keyboard;
            [FieldOffset(0)]
            public RawInputHidInfo Hid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputMouseInfo
        {
            public uint Id;
            public uint NumberOfButtons;
            public uint SampleRate;
            public int HasHorizontalWheel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputKeyboardInfo
        {
            public uint Type;
            public uint SubType;
            public uint KeyboardMode;
            public uint NumberOfFunctionKeys;
            public uint NumberOfIndicators;
            public uint NumberOfKeysTotal;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHidInfo
        {
            public uint VendorId;
            public uint ProductId;
            public uint VersionNumber;
            public ushort UsagePage;
            public ushort Usage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            public ushort Reserved0;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public ushort Reserved4;
            public ushort Reserved5;
            public ushort Reserved6;
            public ushort Reserved7;
            public ushort Reserved8;
            public ushort Reserved9;
            public ushort Reserved10;
            public ushort Reserved11;
            public ushort Reserved12;
            public ushort Reserved13;
            public ushort Reserved14;
            public ushort Reserved15;
            public ushort Reserved16;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpValueCaps
        {
            public ushort UsagePage;
            public byte ReportId;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            public ushort Reserved20;
            public ushort Reserved21;
            public ushort Reserved22;
            public ushort Reserved23;
            public ushort Reserved24;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public HidpValueCapsUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct HidpValueCapsUnion
        {
            [FieldOffset(0)]
            public HidpValueCapsRange Range;
            [FieldOffset(0)]
            public HidpValueCapsNotRange NotRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpValueCapsRange
        {
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpValueCapsNotRange
        {
            public ushort Usage;
            public ushort Reserved1;
            public ushort StringIndex;
            public ushort Reserved2;
            public ushort DesignatorIndex;
            public ushort Reserved3;
            public ushort DataIndex;
            public ushort Reserved4;
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool RegisterRawInputDevices(
                RawInputDevice[] devices,
                uint deviceCount,
                uint deviceSize);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetRawInputData(
                IntPtr rawInput,
                uint command,
                IntPtr data,
                ref uint size,
                uint headerSize);

            [DllImport(
                "user32.dll",
                EntryPoint = "GetRawInputDeviceInfoW",
                CharSet = CharSet.Unicode,
                ExactSpelling = true,
                SetLastError = true)]
            public static extern uint GetRawInputDeviceInfo(
                IntPtr device,
                uint command,
                IntPtr data,
                ref uint size);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetRawInputDeviceList(
                IntPtr devices,
                ref uint deviceCount,
                uint deviceSize);

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProc(
                IntPtr window,
                uint message,
                IntPtr wParam,
                IntPtr lParam);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            public static extern uint CM_Locate_DevNodeW(
                out uint deviceInstance,
                StringBuilder deviceId,
                uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            public static extern uint CM_Get_Parent(
                out uint parent,
                uint deviceInstance,
                uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            public static extern uint CM_Get_Device_IDW(
                uint deviceInstance,
                StringBuilder buffer,
                uint bufferLength,
                uint flags);

            [DllImport("hid.dll", EntryPoint = "HidP_GetUsageValue")]
            public static extern uint HidPGetUsageValue(
                uint reportType,
                ushort usagePage,
                ushort linkCollection,
                ushort usage,
                out uint usageValue,
                IntPtr preparsedData,
                IntPtr report,
                uint reportLength);

            [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
            public static extern uint HidPGetCaps(
                IntPtr preparsedData,
                ref HidpCaps caps);

            [DllImport("hid.dll", EntryPoint = "HidP_GetValueCaps")]
            public static extern uint HidPGetValueCaps(
                uint reportType,
                [Out] HidpValueCaps[] valueCaps,
                ref ushort valueCapsLength,
                IntPtr preparsedData);
        }
    }
}
