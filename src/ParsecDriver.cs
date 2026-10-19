/*
 * Copyright (c) 2023, Nguyen Duy <wuuyi123@gmail.com> All rights reserved.
 * GitHub repo: https://github.com/nomi-san/parsec-vdd/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *   * Redistributions of source code must retain the above copyright notice,
 *     this list of conditions and the following disclaimer.
 *
 *   * Redistributions in binary form must reproduce the above copyright
 *     notice, this list of conditions and the following disclaimer in the
 *     documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ScreenQuiet
{
    /// <summary>
    /// Owns Parsec VDD displays created through this instance and keeps them alive.
    /// </summary>
    public sealed class ParsecDriver : IDisposable
    {
        private const uint IoctlAdd = 0x0022e004;
        private const uint IoctlRemove = 0x0022a008;
        private const uint IoctlUpdate = 0x0022a00c;

        private const int InputBufferSize = 32;
        private const int DisplayIndexLimit = 16;
        private const int AddTimeoutMilliseconds = 5000;
        private const int IoctlTimeoutMilliseconds = 1000;
        private const int KeepAlivePeriodMilliseconds = 50;

        private static readonly Guid DeviceInterfaceGuid = new Guid(
            "00b41627-04c4-429e-a26e-0265cf50c8fa");

        private readonly object _sync = new object();
        private readonly SafeFileHandle _device;
        private readonly Timer _keepAliveTimer;
        private readonly HashSet<int> _ownedDisplays = new HashSet<int>();

        private Exception _keepAliveFailure;
        private bool _disposing;
        private bool _disposed;
        private int _keepAliveCallbackRunning;

        public ParsecDriver()
        {
            string devicePath = FindDevicePath();
            if (devicePath == null)
                throw new InvalidOperationException("The Parsec virtual display device interface was not found.");

            _device = Native.CreateFile(
                devicePath,
                Native.GenericRead | Native.GenericWrite,
                Native.FileShareRead | Native.FileShareWrite,
                IntPtr.Zero,
                Native.OpenExisting,
                Native.FileAttributeNormal | Native.FileFlagNoBuffering |
                    Native.FileFlagOverlapped | Native.FileFlagWriteThrough,
                IntPtr.Zero);

            if (_device == null || _device.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (_device != null)
                    _device.Dispose();
                throw new Win32Exception(error, "Could not open the Parsec virtual display device.");
            }

            try
            {
                _keepAliveTimer = new Timer(KeepAliveTick, null, Timeout.Infinite, Timeout.Infinite);
            }
            catch
            {
                _device.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Checks whether a present Parsec device interface can be enumerated.
        /// This method does not send IOCTLs or alter display state.
        /// </summary>
        public static bool IsInstalled()
        {
            return FindDevicePath() != null;
        }

        /// <summary>
        /// Adds a virtual display and records its returned driver index as owned.
        /// </summary>
        public int AddDisplay()
        {
            lock (_sync)
            {
                ThrowIfUnavailable();
                ThrowIfKeepAliveFailed();

                int index = ExecuteIoctl(IoctlAdd, null, 4, AddTimeoutMilliseconds);
                if (index < 0 || index >= DisplayIndexLimit)
                    throw new InvalidDataException("The Parsec driver returned an invalid display index.");

                if (!_ownedDisplays.Add(index))
                    throw new InvalidOperationException("The Parsec driver returned an index already owned by this instance.");

                _keepAliveTimer.Change(KeepAlivePeriodMilliseconds, KeepAlivePeriodMilliseconds);
                ExecuteIoctl(IoctlUpdate, null, 0, IoctlTimeoutMilliseconds);
                return index;
            }
        }

        /// <summary>
        /// Removes a display only when its index was returned by this instance.
        /// </summary>
        public void RemoveDisplay(int index)
        {
            lock (_sync)
            {
                ThrowIfUnavailable();
                if (!_ownedDisplays.Contains(index))
                    throw new InvalidOperationException("This ParsecDriver instance does not own display index " + index + ".");

                Exception keepAliveFailure = _keepAliveFailure;
                byte[] input = new byte[InputBufferSize];
                input[1] = (byte)(index & 0xff);
                ExecuteIoctl(IoctlRemove, input, 0, IoctlTimeoutMilliseconds);
                _ownedDisplays.Remove(index);

                if (_ownedDisplays.Count == 0)
                    _keepAliveTimer.Change(Timeout.Infinite, Timeout.Infinite);

                ExecuteIoctl(IoctlUpdate, null, 0, IoctlTimeoutMilliseconds);
                if (keepAliveFailure != null)
                    throw new InvalidOperationException("The Parsec keep-alive timer failed.", keepAliveFailure);
            }
        }

        public void Dispose()
        {
            Timer timer;
            Exception keepAliveFailure;

            lock (_sync)
            {
                while (_disposing && !_disposed)
                    Monitor.Wait(_sync);

                if (_disposed)
                    return;

                _disposing = true;
                timer = _keepAliveTimer;
                keepAliveFailure = _keepAliveFailure;
                timer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            List<Exception> failures = new List<Exception>();
            if (keepAliveFailure != null)
                failures.Add(new InvalidOperationException("The Parsec keep-alive timer failed.", keepAliveFailure));

            try
            {
                using (ManualResetEvent callbacksDone = new ManualResetEvent(false))
                {
                    timer.Dispose(callbacksDone);
                    callbacksDone.WaitOne();
                }

                lock (_sync)
                {
                    int[] owned = new int[_ownedDisplays.Count];
                    _ownedDisplays.CopyTo(owned);

                    for (int i = 0; i < owned.Length; ++i)
                    {
                        try
                        {
                            byte[] input = new byte[InputBufferSize];
                            input[1] = (byte)(owned[i] & 0xff);
                            ExecuteIoctl(IoctlRemove, input, 0, IoctlTimeoutMilliseconds);
                            _ownedDisplays.Remove(owned[i]);

                            try
                            {
                                ExecuteIoctl(IoctlUpdate, null, 0, IoctlTimeoutMilliseconds);
                            }
                            catch (Exception exception)
                            {
                                failures.Add(exception);
                            }
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                lock (_sync)
                {
                    _ownedDisplays.Clear();
                    try
                    {
                        _device.Dispose();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                    finally
                    {
                        _disposed = true;
                        Monitor.PulseAll(_sync);
                    }
                }
            }

            if (failures.Count == 1)
                throw failures[0];
            if (failures.Count > 1)
                throw new AggregateException("One or more Parsec display cleanup operations failed.", failures);
        }

        private void KeepAliveTick(object state)
        {
            if (Interlocked.Exchange(ref _keepAliveCallbackRunning, 1) != 0)
                return;

            try
            {
                lock (_sync)
                {
                    if (_disposing || _disposed || _ownedDisplays.Count == 0 || _keepAliveFailure != null)
                        return;

                    try
                    {
                        ExecuteIoctl(IoctlUpdate, null, 0, IoctlTimeoutMilliseconds);
                    }
                    catch (Exception exception)
                    {
                        _keepAliveFailure = exception;
                        _keepAliveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _keepAliveCallbackRunning, 0);
            }
        }

        private void ThrowIfUnavailable()
        {
            if (_disposing || _disposed)
                throw new ObjectDisposedException("ParsecDriver");
        }

        private void ThrowIfKeepAliveFailed()
        {
            if (_keepAliveFailure != null)
                throw new InvalidOperationException("The Parsec keep-alive timer failed.", _keepAliveFailure);
        }

        private int ExecuteIoctl(uint ioctl, byte[] payload, int outputSize, int timeoutMilliseconds)
        {
            IntPtr inputBuffer = IntPtr.Zero;
            IntPtr outputBuffer = IntPtr.Zero;
            IntPtr overlappedBuffer = IntPtr.Zero;
            IntPtr eventHandle = IntPtr.Zero;

            try
            {
                inputBuffer = Marshal.AllocHGlobal(InputBufferSize);
                for (int i = 0; i < InputBufferSize; ++i)
                    Marshal.WriteByte(inputBuffer, i, 0);

                if (payload != null)
                    Marshal.Copy(payload, 0, inputBuffer, Math.Min(payload.Length, InputBufferSize));

                if (outputSize > 0)
                {
                    outputBuffer = Marshal.AllocHGlobal(outputSize);
                    for (int i = 0; i < outputSize; ++i)
                        Marshal.WriteByte(outputBuffer, i, 0);
                }

                eventHandle = Native.CreateEvent(IntPtr.Zero, true, false, null);
                if (eventHandle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create an IOCTL completion event.");

                overlappedBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OverlappedData)));
                Native.OverlappedData overlapped = new Native.OverlappedData();
                overlapped.EventHandle = eventHandle;
                Marshal.StructureToPtr(overlapped, overlappedBuffer, false);

                // Always collect the overlapped completion status: this driver
                // can report a synchronous DeviceIoControl failure while still
                // queuing the request.
                Native.DeviceIoControl(
                    _device,
                    ioctl,
                    inputBuffer,
                    InputBufferSize,
                    outputBuffer,
                    outputSize,
                    IntPtr.Zero,
                    overlappedBuffer);

                uint bytesTransferred;
                bool completed = Native.GetOverlappedResultEx(
                    _device,
                    overlappedBuffer,
                    out bytesTransferred,
                    (uint)timeoutMilliseconds,
                    false);

                if (!completed)
                {
                    int waitError = Marshal.GetLastWin32Error();
                    Native.CancelIoEx(_device, overlappedBuffer);

                    bool finalResult = Native.GetOverlappedResult(_device, overlappedBuffer, out bytesTransferred, true);
                    int finalError = finalResult ? 0 : Marshal.GetLastWin32Error();
                    if (!finalResult)
                    {
                        if (waitError == Native.ErrorWaitTimeout)
                            throw new TimeoutException("The Parsec display IOCTL exceeded its " + timeoutMilliseconds + " ms timeout.", new Win32Exception(finalError));

                        throw new Win32Exception(finalError, "The Parsec display IOCTL failed.");
                    }
                }

                if (outputSize > 0 && bytesTransferred != (uint)outputSize)
                    throw new IOException("The Parsec driver returned an unexpected IOCTL output size.");

                return outputSize >= sizeof(int) ? Marshal.ReadInt32(outputBuffer) : 0;
            }
            finally
            {
                if (eventHandle != IntPtr.Zero)
                    Native.CloseHandle(eventHandle);
                if (overlappedBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(overlappedBuffer);
                if (outputBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(outputBuffer);
                if (inputBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputBuffer);
            }
        }

        private static string FindDevicePath()
        {
            Guid interfaceGuid = DeviceInterfaceGuid;
            IntPtr deviceInfoSet = Native.SetupDiGetClassDevs(
                ref interfaceGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.DigcfPresent | Native.DigcfDeviceInterface);

            if (deviceInfoSet == Native.InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Parsec device interfaces.");

            try
            {
                for (uint index = 0; ; ++index)
                {
                    Native.DeviceInterfaceData interfaceData = new Native.DeviceInterfaceData();
                    interfaceData.Size = (uint)Marshal.SizeOf(typeof(Native.DeviceInterfaceData));

                    if (!Native.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref interfaceGuid,
                        index,
                        ref interfaceData))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == Native.ErrorNoMoreItems)
                            return null;
                        throw new Win32Exception(error, "Could not enumerate a Parsec device interface.");
                    }

                    int requiredSize;
                    bool probeSucceeded = Native.SetupDiGetDeviceInterfaceDetail(
                        deviceInfoSet,
                        ref interfaceData,
                        IntPtr.Zero,
                        0,
                        out requiredSize,
                        IntPtr.Zero);
                    int probeError = Marshal.GetLastWin32Error();

                    if (requiredSize <= 0 || (!probeSucceeded && probeError != Native.ErrorInsufficientBuffer))
                        throw new Win32Exception(probeError, "Could not query the Parsec device interface path size.");

                    IntPtr detail = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            detail,
                            requiredSize,
                            out requiredSize,
                            IntPtr.Zero))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the Parsec device interface path.");
                        }

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(int)));
                        if (!String.IsNullOrEmpty(path))
                            return path;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static class Native
        {
            internal const uint GenericRead = 0x80000000;
            internal const uint GenericWrite = 0x40000000;
            internal const uint FileShareRead = 0x00000001;
            internal const uint FileShareWrite = 0x00000002;
            internal const uint OpenExisting = 3;
            internal const uint FileAttributeNormal = 0x00000080;
            internal const uint FileFlagNoBuffering = 0x20000000;
            internal const uint FileFlagOverlapped = 0x40000000;
            internal const uint FileFlagWriteThrough = 0x80000000;
            internal const uint DigcfPresent = 0x00000002;
            internal const uint DigcfDeviceInterface = 0x00000010;
            internal const int ErrorInsufficientBuffer = 122;
            internal const int ErrorNoMoreItems = 259;
            internal const int ErrorWaitTimeout = 258;

            internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential)]
            internal struct DeviceInterfaceData
            {
                internal uint Size;
                internal Guid InterfaceClassGuid;
                internal uint Flags;
                internal IntPtr Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct OverlappedData
            {
                internal IntPtr Internal;
                internal IntPtr InternalHigh;
                internal uint Offset;
                internal uint OffsetHigh;
                internal IntPtr EventHandle;
            }

            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr SetupDiGetClassDevs(
                ref Guid classGuid,
                IntPtr enumerator,
                IntPtr parentWindow,
                uint flags);

            [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiEnumDeviceInterfaces(
                IntPtr deviceInfoSet,
                IntPtr deviceInfoData,
                ref Guid interfaceClassGuid,
                uint memberIndex,
                ref DeviceInterfaceData deviceInterfaceData);

            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceInterfaceDetail(
                IntPtr deviceInfoSet,
                ref DeviceInterfaceData deviceInterfaceData,
                IntPtr deviceInterfaceDetailData,
                int deviceInterfaceDetailDataSize,
                out int requiredSize,
                IntPtr deviceInfoData);

            [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr CreateEvent(
                IntPtr eventAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool manualReset,
                [MarshalAs(UnmanagedType.Bool)] bool initialState,
                string name);

            [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeviceIoControl(
                SafeFileHandle device,
                uint controlCode,
                IntPtr inputBuffer,
                int inputBufferSize,
                IntPtr outputBuffer,
                int outputBufferSize,
                IntPtr bytesReturned,
                IntPtr overlapped);

            [DllImport("kernel32.dll", EntryPoint = "GetOverlappedResultEx", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetOverlappedResultEx(
                SafeFileHandle device,
                IntPtr overlapped,
                out uint bytesTransferred,
                uint milliseconds,
                [MarshalAs(UnmanagedType.Bool)] bool alertable);

            [DllImport("kernel32.dll", EntryPoint = "CancelIoEx", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CancelIoEx(SafeFileHandle device, IntPtr overlapped);

            [DllImport("kernel32.dll", EntryPoint = "GetOverlappedResult", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetOverlappedResult(
                SafeFileHandle device,
                IntPtr overlapped,
                out uint bytesTransferred,
                [MarshalAs(UnmanagedType.Bool)] bool wait);
        }
    }
}
