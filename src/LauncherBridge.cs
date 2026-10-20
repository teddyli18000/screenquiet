using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenQuiet
{
    // Short-lived IFEO bridge. The vendor's dedicated customer-service launch
    // command is suppressed before its launcher can notify or start Baiying.
    internal static class LauncherBridge
    {
        [STAThread] static int Main(string[] args)
        {
            if(args.Length==0) return 0;
            try
            {
                if(IsFnLaunch(args)) { LogRoute("Suppressed vendor customer-service command"); return 0; }
                LogRoute("Forwarding ordinary vendor launch");
                return Forward(args);
            }
            catch(Exception ex)
            {
                try
                {
                    string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenQuiet");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir,"bridge-errors.log"),DateTime.Now.ToString("s")+" "+ex.GetType().Name+": "+ex.Message+Environment.NewLine);
                } catch { }
                return 1;
            }
        }
        static bool IsFnLaunch(string[] args)
        {
            // FnHotkeyUtility calls ShellExecuteW with exactly this command.
            // Do not depend on a separate WMI callback arriving within a deadline.
            return args.Length==2 &&
                String.Equals(Path.GetFileName(args[0]),"LenovoBaiyingLauncher.exe",StringComparison.OrdinalIgnoreCase) &&
                String.Equals(args[1],"-customerService",StringComparison.OrdinalIgnoreCase);
        }
        static void LogRoute(string action)
        {
            try
            {
                string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenQuiet");
                Directory.CreateDirectory(dir);
                string path=Path.Combine(dir,"bridge-routing.log");
                if(File.Exists(path) && new FileInfo(path).Length>65536) File.Delete(path);
                File.AppendAllText(path,DateTime.Now.ToString("s")+" "+action+Environment.NewLine);
            } catch { }
        }
        static string Quote(string s)
        {
            StringBuilder b=new StringBuilder("\""); int slashes=0;
            foreach(char c in s)
            {
                if(c=='\\') { slashes++; continue; }
                if(c=='\"') b.Append('\\',slashes*2+1);
                else b.Append('\\',slashes);
                b.Append(c); slashes=0;
            }
            b.Append('\\',slashes*2); return b.Append('"').ToString();
        }
        static int Forward(string[] args)
        {
            StringBuilder command=new StringBuilder();
            for(int i=0;i<args.Length;i++) { if(i>0) command.Append(' '); command.Append(Quote(args[i])); }
            StartupInfo si=new StartupInfo(); GetStartupInfo(ref si); si.Size=(uint)Marshal.SizeOf(typeof(StartupInfo)); si.Flags&=~0x100u;
            ProcessInfo pi;
            // DEBUG_ONLY_THIS_PROCESS bypasses this process's IFEO Debugger entry.
            // Windows stops the target at CREATE_PROCESS_DEBUG_EVENT, before user code.
            if(!CreateProcess(args[0],command,IntPtr.Zero,IntPtr.Zero,false,0x2,IntPtr.Zero,null,ref si,out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not forward application launch");
            HashSet<IntPtr> handles=new HashSet<IntPtr>(); handles.Add(pi.Process); handles.Add(pi.Thread);
            bool detached=false,suspended=false;
            try
            {
                if(!DebugSetProcessKillOnExit(false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                DebugEvent e;
                if(!WaitForDebugEvent(out e,5000)) throw new Win32Exception(Marshal.GetLastWin32Error(),"No process creation event");
                if(e.Code==3)
                {
                    if(e.File!=IntPtr.Zero) CloseHandle(e.File);
                    handles.Add(e.ProcessHandle); handles.Add(e.ThreadHandle);
                }
                else throw new InvalidOperationException("Unexpected initial debug event");
                // Preserve an explicit suspend while releasing the debug event, so
                // target code cannot run until the debugger has been detached.
                if(SuspendThread(pi.Thread)==uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                suspended=true;
                if(!ContinueDebugEvent(e.Process,e.Thread,0x10002)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!DebugActiveProcessStop(pi.Id)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not detach launch bridge");
                detached=true;
                if(ResumeThread(pi.Thread)==uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                suspended=false;
                return 0;
            }
            finally
            {
                // Never intentionally leave a manually opened application suspended.
                if(!detached) DebugActiveProcessStop(pi.Id);
                if(suspended) ResumeThread(pi.Thread);
                foreach(IntPtr h in handles) if(h!=IntPtr.Zero && h!=new IntPtr(-1)) CloseHandle(h);
            }
        }
        [StructLayout(LayoutKind.Sequential)] struct StartupInfo
        {
            public uint Size; public IntPtr Reserved,Desktop,Title;
            public uint X,Y,Width,Height,XChars,YChars,Fill,Flags;
            public ushort Show,ReservedCount; public IntPtr ReservedData,StdInput,StdOutput,StdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
        [StructLayout(LayoutKind.Explicit,Size=176)] struct DebugEvent
        {
            [FieldOffset(0)] public uint Code; [FieldOffset(4)] public uint Process; [FieldOffset(8)] public uint Thread;
            [FieldOffset(16)] public IntPtr File; [FieldOffset(24)] public IntPtr ProcessHandle; [FieldOffset(32)] public IntPtr ThreadHandle;
        }
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern void GetStartupInfo(ref StartupInfo si);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref StartupInfo si,out ProcessInfo pi);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool DebugSetProcessKillOnExit(bool kill);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool WaitForDebugEvent(out DebugEvent e,uint ms);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool ContinueDebugEvent(uint process,uint thread,uint status);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool DebugActiveProcessStop(uint process);
        [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll",SetLastError=true)] static extern uint SuspendThread(IntPtr thread);
    }
}
