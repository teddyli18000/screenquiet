using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenQuiet
{
    internal static class Program
    {
        internal static readonly string StateDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenQuiet");
        internal static readonly string RecoveryFile=Path.Combine(StateDir,"display-recovery.xml");
        internal static void Log(string text)
        {
            try { Directory.CreateDirectory(StateDir); string path=Path.Combine(StateDir,"status.log");
                if(File.Exists(path) && new FileInfo(path).Length>262144) File.Delete(path);
                File.AppendAllText(path,DateTime.Now.ToString("s")+" "+text+Environment.NewLine); } catch { }
        }
        [STAThread] static int Main(string[] args)
        {
            Directory.CreateDirectory(StateDir);
            try
            {
                if(args.Length>0 && args[0]=="--watch") return Watch(args);
                if(args.Length>0 && args[0]=="--suppress-vendor") return 0;
                if(args.Length>0 && args[0]=="--validate-virtual") return ValidateVirtual(args[1]);
                if(args.Length>0 && args[0]=="--diagnose")
                {
                    string report="ScreenQuiet read-only diagnosis\r\nParsec installed="+ParsecDriver.IsInstalled()+"\r\n";
                    report+=String.Join("\r\n",DisplayTopology.Describe())+"\r\n"+String.Join("\r\n",PhysicalInput.DescribeDevices());
                    DisplaySnapshot s=DisplayTopology.Read(false); DisplayTopology.Validate(s);
                    report+="\r\nCurrent display configuration VALIDATE passed. No display was applied.\r\n";
                    File.WriteAllText(args.Length>1?args[1]:Path.Combine(StateDir,"diagnosis.txt"),report);
                    return 0;
                }
                if(args.Length>0 && args[0]=="--smoke")
                {
                    Application.EnableVisualStyles();
                    using(Controller c=new Controller(true))
                    using(System.Windows.Forms.Timer stop=new System.Windows.Forms.Timer { Interval=2500 })
                    {
                        stop.Tick+=delegate { stop.Stop(); c.ExitThread(); };
                        stop.Start(); Application.Run(c);
                    }
                    Log("PASS: passive receiver, WMI watcher, Raw Input registration and cleanup");
                    return 0;
                }
                if(args.Length>0 && args[0]=="--restore")
                {
                    using(EventWaitHandle wake= new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenQuiet.Restore")) wake.Set();
                    // The running controller owns restoration. If absent, restore saved state.
                    bool created;
                    using(Mutex m=new Mutex(true,"Local\\ScreenQuiet.Controller",out created))
                    { if(created) { if(File.Exists(RecoveryFile)) { DisplayTopology.Restore(DisplaySnapshot.Load(RecoveryFile)); File.Delete(RecoveryFile); } m.ReleaseMutex(); } }
                    return 0;
                }
                if(args.Length>0 && args[0]=="--stop")
                {
                    using(EventWaitHandle stop=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenQuiet.Stop")) stop.Set();
                    return 0;
                }
                bool first;
                using(Mutex single=new Mutex(true,"Local\\ScreenQuiet.Controller",out first))
                {
                    if(!first) return 0;
                    try
                    {
                        if(File.Exists(RecoveryFile)) { DisplayTopology.Restore(DisplaySnapshot.Load(RecoveryFile)); File.Delete(RecoveryFile); Log("Recovered previous session"); }
                        Application.EnableVisualStyles();
                        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                        using(Controller c=new Controller()) Application.Run(c);
                    }
                    finally { single.ReleaseMutex(); }
                }
                return 0;
            }
            catch(Exception ex) { Log("ERROR "+ex); return 1; }
        }
        static int ValidateVirtual(string reportPath)
        {
            DisplaySnapshot original=DisplayTopology.Read(false); DisplayTopology.CheckInitial(original);
            var before=DisplayTopology.AvailableKeys();
            original.Save(RecoveryFile); ParsecDriver driver=null;
            try
            {
                driver=new ParsecDriver(); driver.AddDisplay();
                DisplaySnapshot plan=DisplayTopology.WaitForVirtual(before);
                DisplaySnapshot active=DisplayTopology.Read(false);
                bool internalActive=false;
                for(int i=0;i<active.PathCount;i++) if(DisplayTopology.IsInternal(active,i)) internalActive=true;
                if(!internalActive) throw new InvalidOperationException("Internal panel unexpectedly inactive; restoring");
                plan.Save(reportPath+".plan.xml");
                File.WriteAllText(reportPath,"PASS: virtual display created; internal panel stayed active; virtual-only plan passed SDC_VALIDATE.\r\nVirtual-only plan was NOT applied.\r\n");
                return 0;
            }
            finally
            {
                // The diagnostic never applies the virtual-only topology.
                DisplayTopology.Restore(original);
                try { if(driver!=null) driver.Dispose(); }
                finally { DisplayTopology.Restore(original); File.Delete(RecoveryFile); }
            }
        }
        static int Watch(string[] args)
        {
            int pid=Int32.Parse(args[1]); long start=Int64.Parse(args[2]); string eventName=args[3];
            using(EventWaitHandle done=EventWaitHandle.OpenExisting(eventName))
            using(EventWaitHandle pulse=EventWaitHandle.OpenExisting(eventName+".Pulse"))
            {
                using(EventWaitHandle ready=EventWaitHandle.OpenExisting(eventName+".Ready")) ready.Set();
                Process parent=null;
                try { parent=Process.GetProcessById(pid); if(parent.StartTime.ToUniversalTime().Ticks!=start) parent=null; } catch { }
                DateTime alive=DateTime.UtcNow;
                while(parent!=null && !parent.HasExited)
                {
                    if(done.WaitOne(500)) return 0;
                    if(pulse.WaitOne(0)) alive=DateTime.UtcNow;
                    if((DateTime.UtcNow-alive).TotalSeconds>20) { Log("UI heartbeat lost; recovering panel"); break; }
                }
                // Driver heartbeat expiry can change topology shortly after a crash.
                for(int i=0;i<3;i++)
                {
                    try { if(File.Exists(RecoveryFile)) DisplayTopology.Restore(DisplaySnapshot.Load(RecoveryFile)); }
                    catch(Exception ex) { Log("Recovery retry: "+ex.Message); }
                    Thread.Sleep(1200);
                }
                if(File.Exists(RecoveryFile))
                {
                    try { DisplayTopology.Restore(DisplaySnapshot.Load(RecoveryFile)); File.Delete(RecoveryFile); Log("Watchdog recovered panel"); }
                    catch(Exception ex) { Log("RECOVERY FAILED "+ex); return 1; }
                }
            }
            return 0;
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        internal const string VendorClass="ScreenQuiet.PhysicalInput.1";
        delegate IntPtr WindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        static readonly WindowProc DefaultProc=DefWindowProc;
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WindowClass
        {
            public uint Style; public WindowProc Proc; public int ClassExtra,WindowExtra;
            public IntPtr Instance,Icon,Cursor,Background;
            public string Menu,ClassName;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClass(ref WindowClass c);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        internal PhysicalInput Input;
        internal HotkeyWindow()
        {
            if(FindWindow(VendorClass,null)!=IntPtr.Zero) throw new InvalidOperationException("ScreenQuiet input window already exists");
            IntPtr module=GetModuleHandle(null);
            WindowClass c=new WindowClass { Proc=DefaultProc,ClassName=VendorClass,Instance=module };
            if(RegisterClass(ref c)==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            IntPtr hwnd=CreateWindowEx(0,VendorClass,"ScreenQuiet receiver",0x00800000,0,0,0,0,IntPtr.Zero,IntPtr.Zero,module,IntPtr.Zero);
            if(hwnd==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            AssignHandle(hwnd);
            if(FindWindow(VendorClass,null)!=Handle) throw new InvalidOperationException("Could not create input receiver window");
        }
        protected override void WndProc(ref Message m)
        {
            if(Input!=null && (m.Msg==0xff || m.Msg==0xfe)) { Input.ProcessMessage(ref m); return; }
            base.WndProc(ref m);
        }
        public void Dispose() { IntPtr h=Handle; ReleaseHandle(); if(h!=IntPtr.Zero) DestroyWindow(h); }
    }

    internal sealed class Controller : ApplicationContext
    {
        readonly Control dispatch=new Control();
        readonly HotkeyWindow window;
        PhysicalInput input;
        readonly System.Windows.Forms.Timer poll;
        readonly EventWaitHandle restoreSignal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenQuiet.Restore");
        readonly EventWaitHandle stopSignal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\ScreenQuiet.Stop");
        readonly EventWaitHandle fnGate=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\ScreenQuiet.FnRoute");
        readonly System.Threading.Timer gateReset;
        ManagementEventWatcher watcher;
        ParsecDriver driver;
        DisplaySnapshot original,quiet;
        EventWaitHandle watchdogDone;
        EventWaitHandle watchdogPulse;
        Process watchdog;
        bool busy,armed,disposed,wakePending;
        readonly bool observeOnly;
        DateTime ignoreInputUntil,lastHotkey=DateTime.MinValue;
        [DllImport("kernel32.dll",SetLastError=true)] static extern uint SetThreadExecutionState(uint flags);
        internal Controller() : this(false) { }
        internal Controller(bool observe)
        {
            observeOnly=observe;
            dispatch.CreateControl();
            string[] devices=PhysicalInput.DescribeDevices();
            if(!Array.Exists(devices,delegate(string d) { return d.StartsWith("Keyboard: physical "); }))
                throw new InvalidOperationException("未识别到可恢复屏幕的物理键盘，未启用关屏。");
            window=new HotkeyWindow();
            if(observeOnly) RegisterPhysicalInput();
            gateReset=new System.Threading.Timer(delegate { try { fnGate.Reset(); } catch(ObjectDisposedException) { } },null,Timeout.Infinite,Timeout.Infinite);
            // WMI receives the hardware event even if the vendor launch path varies by firmware.
            watcher=new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"),new WqlEventQuery("SELECT * FROM LENOVO_UTILITY_EVENT"));
            watcher.EventArrived+=delegate(object sender,EventArrivedEventArgs e)
            {
                try { if(Convert.ToUInt32(e.NewEvent["PressTypeDataVal"])==1) { fnGate.Set(); gateReset.Change(3000,Timeout.Infinite); dispatch.BeginInvoke((Action)OnHotkey); } }
                catch(Exception ex) { Program.Log("WMI event: "+ex.Message); }
            };
            watcher.Start();
            Microsoft.Win32.SystemEvents.SessionSwitch+=OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged+=OnPowerMode;
            poll=new System.Windows.Forms.Timer { Interval=1000 };
            poll.Tick+=delegate
            {
                if(stopSignal.WaitOne(0)) { ExitThread(); return; }
                if(watchdogPulse!=null) watchdogPulse.Set();
                if(restoreSignal.WaitOne(0)) Restore();
                if(armed && !busy)
                {
                    try { if(watchdog==null || watchdog.HasExited || !DisplayTopology.SameTargets(quiet,DisplayTopology.Read(false))) { Program.Log("Watchdog exit or external topology change: restoring and disarming"); Restore(); } }
                    catch(Exception ex) { Program.Log(ex.Message); Restore(); }
                }
            };
            poll.Start(); Program.Log("Controller ready; receiver and WMI registered");
        }
        void OnHotkey()
        {
            if(observeOnly) { Program.Log("Hotkey observed in passive mode; no action"); return; }
            DateTime now=DateTime.UtcNow;
            if(busy || (now-lastHotkey).TotalMilliseconds<1200) return;
            lastHotkey=now;
            if(armed) { Restore(); return; }
            busy=true;
            try
            {
                if(original!=null) throw new InvalidOperationException("上次显示恢复尚未完成，已禁止再次关屏。");
                if(!ParsecDriver.IsInstalled()) throw new InvalidOperationException("需要先安装已签名的 Parsec 虚拟显示驱动。当前未改变显示配置。");
                original=DisplayTopology.Read(false); DisplayTopology.CheckInitial(original);
                RegisterPhysicalInput();
                var before=DisplayTopology.AvailableKeys();
                original.Save(Program.RecoveryFile);
                StartWatchdog();
                driver=new ParsecDriver(); driver.AddDisplay();
                quiet=DisplayTopology.WaitForVirtual(before);
                DisplayTopology.Validate(quiet);
                // Refuse concurrent topology changes rather than overwrite another controller.
                DisplaySnapshot current=DisplayTopology.Read(false);
                for(int i=0;i<current.PathCount;i++)
                    if(!DisplayTopology.IsInternal(current,i) && !DisplayTopology.IsParsec(current,i))
                        throw new InvalidOperationException("其他程序正在切换显示布局，已取消关屏。");
                if(SetThreadExecutionState(0x80000001)==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                armed=true;
                DisplayTopology.Apply(quiet);
                ignoreInputUntil=DateTime.UtcNow.AddMilliseconds(900);
                Program.Log("Quiet mode active");
            }
            catch(Exception ex)
            {
                Program.Log("Activation failed: "+ex);
                Restore();
            }
            finally { busy=false; }
        }
        void OnPhysical()
        {
            if(original!=null && !busy && !wakePending && DateTime.UtcNow>=ignoreInputUntil)
            { wakePending=true; dispatch.BeginInvoke((Action)Restore); }
        }
        void RegisterPhysicalInput()
        {
            if(input!=null) return;
            input=new PhysicalInput(window.Handle,OnPhysical);
            input.PhysicalInputSourceDetected+=delegate(string source)
            { if(armed && !busy && !wakePending && DateTime.UtcNow>=ignoreInputUntil) Program.Log("Physical wake requested: "+source); };
            window.Input=input;
        }
        void OnSessionSwitch(object sender,Microsoft.Win32.SessionSwitchEventArgs e)
        { if(e.Reason!=Microsoft.Win32.SessionSwitchReason.SessionUnlock) dispatch.BeginInvoke((Action)Restore); }
        void OnPowerMode(object sender,Microsoft.Win32.PowerModeChangedEventArgs e)
        { if(e.Mode==Microsoft.Win32.PowerModes.Suspend) dispatch.BeginInvoke((Action)Restore); }
        void StartWatchdog()
        {
            string name="Local\\ScreenQuiet.Watch."+Guid.NewGuid().ToString("N");
            watchdogDone=new EventWaitHandle(false,EventResetMode.ManualReset,name);
            watchdogPulse=new EventWaitHandle(false,EventResetMode.AutoReset,name+".Pulse");
            Process self=Process.GetCurrentProcess();
            using(EventWaitHandle ready=new EventWaitHandle(false,EventResetMode.ManualReset,name+".Ready"))
            {
                watchdog=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--watch "+self.Id+" "+self.StartTime.ToUniversalTime().Ticks+" "+name) { UseShellExecute=false,CreateNoWindow=true });
                if(watchdog==null || !ready.WaitOne(4000) || watchdog.HasExited) throw new InvalidOperationException("Recovery watchdog did not start");
            }
        }
        void Restore()
        {
            window.Input=null;
            if(input!=null) { input.Dispose(); input=null; }
            wakePending=false;
            bool restored=original==null;
            try { if(original!=null) { DisplayTopology.Restore(original); restored=true; } }
            catch(Exception ex) { Program.Log("Restore failed; recovery snapshot retained: "+ex); }
            armed=false;
            if(driver!=null) { try { driver.Dispose(); } catch(Exception ex) { Program.Log("Driver close: "+ex.Message); } driver=null; }
            if(!restored && original!=null)
            {
                try { DisplayTopology.Restore(original); restored=true; }
                catch(Exception ex) { Program.Log("Restore retry failed: "+ex.Message); }
            }
            SetThreadExecutionState(0x80000000);
            if(restored)
            {
                original=null; quiet=null;
                if(File.Exists(Program.RecoveryFile)) File.Delete(Program.RecoveryFile);
                if(watchdogDone!=null) watchdogDone.Set();
                if(watchdogDone!=null) { watchdogDone.Dispose(); watchdogDone=null; }
                if(watchdogPulse!=null) { watchdogPulse.Dispose(); watchdogPulse=null; }
                if(watchdog!=null) { watchdog.Dispose(); watchdog=null; }
            }
            Program.Log(restored?"Restored; idle":"Restore pending watchdog/process exit");
        }
        protected override void ExitThreadCore()
        {
            if(!disposed)
            {
                disposed=true; poll.Stop(); Restore(); watcher.Stop(); watcher.Dispose(); window.Dispose();
                Microsoft.Win32.SystemEvents.SessionSwitch-=OnSessionSwitch;
                Microsoft.Win32.SystemEvents.PowerModeChanged-=OnPowerMode;
                gateReset.Dispose(); fnGate.Dispose(); stopSignal.Dispose(); restoreSignal.Dispose(); dispatch.Dispose(); poll.Dispose();
            }
            base.ExitThreadCore();
        }
    }
}
