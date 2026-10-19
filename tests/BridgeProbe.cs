using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
class ScreenQuietBridgeProbe
{
    [DllImport("kernel32.dll")] static extern bool IsDebuggerPresent();
    static void Main(string[] args)
    {
        StringBuilder b=new StringBuilder("Debugger="+IsDebuggerPresent()+"\r\n");
        for(int i=1;i<args.Length;i++) b.AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])));
        File.WriteAllText(args[0],b.ToString());
    }
}
