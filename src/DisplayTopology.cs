using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Serialization;
using System.Threading;

namespace ScreenQuiet
{
    public class DisplaySnapshot
    {
        public byte[] Paths;
        public byte[] Modes;
        public uint PathCount;
        public uint ModeCount;
        public void Save(string path)
        {
            using (FileStream f = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(DisplaySnapshot)).Serialize(f, this);
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
        }
        public static DisplaySnapshot Load(string path)
        {
            using (FileStream f = File.OpenRead(path))
                return (DisplaySnapshot)new XmlSerializer(typeof(DisplaySnapshot)).Deserialize(f);
        }
    }

    internal static class DisplayTopology
    {
        const int PathSize = 72, ModeSize = 64;
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] byte[] pathArray, ref uint modes, [Out] byte[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, byte[] pathArray, uint modes, byte[] modeArray, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(byte[] request);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string name, uint number, ref DisplayDevice device, uint flags);
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct DisplayDevice
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Key;
        }
        static uint U(byte[] b,int p) { return BitConverter.ToUInt32(b,p); }
        static void Put(byte[] b,int p,uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v),0,b,p,4); }
        public static DisplaySnapshot Read(bool all)
        {
            uint flag = all ? 1u : 2u;
            for(int attempt=0;attempt<6;attempt++)
            {
                uint pc,mc; int e=GetDisplayConfigBufferSizes(flag,out pc,out mc);
                if(e!=0) throw new Win32Exception(e,"Display buffer query failed");
                byte[] p=new byte[checked((int)pc*PathSize)],m=new byte[checked((int)mc*ModeSize)];
                e=QueryDisplayConfig(flag,ref pc,p,ref mc,m,IntPtr.Zero);
                if(e==122) continue;
                if(e!=0) throw new Win32Exception(e,"Display query failed");
                Array.Resize(ref p,(int)pc*PathSize); Array.Resize(ref m,(int)mc*ModeSize);
                return new DisplaySnapshot { Paths=p,Modes=m,PathCount=pc,ModeCount=mc };
            }
            throw new InvalidOperationException("Display topology is changing; try again later.");
        }
        static byte[] Request(DisplaySnapshot s,int i,uint type,int length,bool target)
        {
            byte[] b=new byte[length]; Put(b,0,type); Put(b,4,(uint)length);
            Buffer.BlockCopy(s.Paths,i*PathSize+(target?20:0),b,8,12);
            int e=DisplayConfigGetDeviceInfo(b);
            if(e!=0) throw new Win32Exception(e,"Display identity query failed");
            return b;
        }
        static string W(byte[] b,int offset,int count) { return Encoding.Unicode.GetString(b,offset,count*2).TrimEnd('\0'); }
        public static string Key(DisplaySnapshot s,int i)
        { return Convert.ToBase64String(s.Paths,i*PathSize+20,12); }
        public static string TargetName(DisplaySnapshot s,int i)
        { return W(Request(s,i,2,420,true),164,128); }
        public static string AdapterName(DisplaySnapshot s,int i)
        {
            string source=W(Request(s,i,1,84,false),20,32);
            for(uint n=0;n<64;n++)
            {
                DisplayDevice d=new DisplayDevice(); d.Size=(uint)Marshal.SizeOf(typeof(DisplayDevice));
                if(!EnumDisplayDevices(null,n,ref d,0)) break;
                if(d.Name==source) return d.Description;
            }
            return "";
        }
        public static string[] Describe()
        {
            DisplaySnapshot s=Read(false); List<string> lines=new List<string>();
            for(int i=0;i<s.PathCount;i++) lines.Add(AdapterName(s,i)+" | "+TargetName(s,i)+" | technology="+U(s.Paths,i*PathSize+36));
            return lines.ToArray();
        }
        public static bool IsInternal(DisplaySnapshot s,int i)
        { return U(s.Paths,i*PathSize+36)==0x80000000u; }
        public static bool IsParsec(DisplaySnapshot s,int i)
        { return AdapterName(s,i)=="Parsec Virtual Display Adapter" && TargetName(s,i).IndexOf("PSCCDD0",StringComparison.OrdinalIgnoreCase)>=0; }
        public static void CheckInitial(DisplaySnapshot s)
        {
            // The initial supported layout is deliberately narrow: one built-in panel.
            // Do not rewrite a user's dock, multi-monitor layout, or UU privacy topology.
            if(s.PathCount!=1 || !IsInternal(s,0))
                throw new InvalidOperationException("当前显示布局不是单个内置屏幕，已保留现有布局。请先退出其他虚拟屏/扩展屏模式。");
        }
        public static DisplaySnapshot VirtualOnly(DisplaySnapshot all,HashSet<string> before)
        {
            // Prefer a path Windows has already activated. ALL_PATHS contains several
            // possible source/target pairs, including unconfigured paths with no mode.
            DisplaySnapshot active=Read(false);
            for(int i=0;i<active.PathCount;i++)
            {
                if(before.Contains(Key(active,i)) || !IsParsec(active,i)) continue;
                byte[] p=new byte[PathSize]; Buffer.BlockCopy(active.Paths,i*PathSize,p,0,PathSize);
                byte[] modes=(byte[])active.Modes.Clone();
                uint sourceIndex=U(p,12);
                if(sourceIndex>=active.ModeCount) continue;
                int modeOffset=checked((int)sourceIndex*ModeSize);
                if(U(modes,modeOffset)!=1) continue;
                Put(modes,modeOffset+28,0); Put(modes,modeOffset+32,0);
                DisplaySnapshot plan=new DisplaySnapshot { Paths=p,Modes=modes,PathCount=1,ModeCount=active.ModeCount };
                try { Validate(plan); return plan; }
                catch(Win32Exception) { }
            }
            List<string> errors=new List<string>();
            for(int i=0;i<all.PathCount;i++)
            {
                int p=i*PathSize;
                if(U(all.Paths,p+60)==0 || before.Contains(Key(all,i)) || !IsParsec(all,i)) continue;
                byte[] path=new byte[PathSize]; Buffer.BlockCopy(all.Paths,p,path,0,PathSize);
                Put(path,12,uint.MaxValue); Put(path,32,uint.MaxValue); Put(path,68,1);
                // Unconfigured paths may contain zero rotation/scaling. Those are not
                // valid DISPLAYCONFIG_ROTATION/SCALING values for supplied paths.
                if(U(path,40)<1 || U(path,40)>4) Put(path,40,1);
                if(U(path,44)==0) Put(path,44,128);
                DisplaySnapshot plan=new DisplaySnapshot { Paths=path,Modes=null,PathCount=1,ModeCount=0 };
                try { Validate(plan); return plan; }
                catch(Win32Exception ex) { errors.Add("source="+U(path,8)+" Win32="+ex.NativeErrorCode); }
            }
            if(errors.Count>0) throw new InvalidOperationException("Windows rejected virtual display candidates: "+String.Join("; ",errors));
            return null;
        }
        public static DisplaySnapshot WaitForVirtual(HashSet<string> before)
        {
            DateTime deadline=DateTime.UtcNow.AddSeconds(6); Exception last=null;
            do
            {
                try { DisplaySnapshot plan=VirtualOnly(Read(true),before); if(plan!=null) return plan; }
                catch(Win32Exception ex) { last=ex; }
                catch(InvalidOperationException ex) { last=ex; }
                Thread.Sleep(200);
            } while(DateTime.UtcNow<deadline);
            throw new InvalidOperationException("Virtual display did not produce a valid configuration.",last);
        }
        public static HashSet<string> AvailableKeys()
        {
            DisplaySnapshot s=Read(true); HashSet<string> keys=new HashSet<string>();
            for(int i=0;i<s.PathCount;i++) if(U(s.Paths,i*PathSize+60)!=0) keys.Add(Key(s,i));
            return keys;
        }
        public static bool SameTargets(DisplaySnapshot a,DisplaySnapshot b)
        {
            if(a.PathCount!=b.PathCount) return false;
            HashSet<string> keys=new HashSet<string>();
            for(int i=0;i<a.PathCount;i++) keys.Add(Key(a,i));
            for(int i=0;i<b.PathCount;i++) if(!keys.Contains(Key(b,i))) return false;
            return true;
        }
        public static void Validate(DisplaySnapshot s)
        { CheckResult(SetDisplayConfig(s.PathCount,s.Paths,s.ModeCount,s.ModeCount==0?null:s.Modes,0x20|0x40|0x400)); }
        public static void Apply(DisplaySnapshot s)
        { CheckResult(SetDisplayConfig(s.PathCount,s.Paths,s.ModeCount,s.ModeCount==0?null:s.Modes,0x20|0x80|0x400)); }
        public static void Restore(DisplaySnapshot s)
        {
            int e=SetDisplayConfig(s.PathCount,s.Paths,s.ModeCount,s.Modes,0x20|0x80);
            if(e!=0) e=SetDisplayConfig(s.PathCount,s.Paths,s.ModeCount,s.Modes,0x20|0x80|0x400);
            if(e!=0) throw new Win32Exception(e,"Restoring original display configuration failed");
        }
        static void CheckResult(int e) { if(e!=0) throw new Win32Exception(e,"Display configuration rejected (Win32 "+e+")"); }
    }
}
