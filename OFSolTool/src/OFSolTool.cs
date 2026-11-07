// OFSolTool - writes OpenFIRE solenoid timing settings straight to a gun over serial,
// bypassing the OpenFIRE App's 99 ms limit on "Solenoid Autofire Wait Time".
//
// Uses the same docked-mode commit protocol as the OpenFIRE App (OpenFIRE-Boards/OpenFIREshared.h):
//   dock (0x01 0x02) -> commit start (0xAA 0x01) -> sCommitSettings record(s) -> save (0xFC) -> undock (0xFE x3)
// Only the settings you enter are sent; every other setting on the gun is left untouched.
//
// Usage:
//   OFSolTool.exe                         interactive
//   OFSolTool.exe COM5 SolOff=150         non-interactive (any of SolOn, SolOff, SolHold)

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OFSolTool
{
    // Byte transport: real COM port on Windows; "tcp:host:port" is only used for bench-testing the protocol.
    internal interface IPort : IDisposable
    {
        int BytesToRead { get; }
        void Write(byte[] b, int off, int len);
        int Read(byte[] b, int off, int len);
        int ReadByte();
        void DiscardInBuffer();
    }

    internal sealed class ComPort : IPort
    {
        readonly SerialPort sp;
        public ComPort(string name)
        {
            sp = new SerialPort(name, 9600);
            sp.DtrEnable = true;   // Windows needs DTR for the gun to reply (same as the OpenFIRE App)
            sp.ReadTimeout = 2000;
            sp.WriteTimeout = 2000;
            sp.Open();
        }
        public int BytesToRead { get { return sp.BytesToRead; } }
        public void Write(byte[] b, int off, int len) { sp.Write(b, off, len); }
        public int Read(byte[] b, int off, int len) { return sp.Read(b, off, len); }
        public int ReadByte() { return sp.ReadByte(); }
        public void DiscardInBuffer() { sp.DiscardInBuffer(); }
        public void Dispose() { try { sp.Close(); } catch { } }
    }

    internal sealed class TcpPort : IPort
    {
        readonly System.Net.Sockets.TcpClient c;
        readonly System.Net.Sockets.NetworkStream ns;
        public TcpPort(string host, int port) { c = new System.Net.Sockets.TcpClient(host, port); ns = c.GetStream(); }
        public int BytesToRead { get { return c.Available; } }
        public void Write(byte[] b, int off, int len) { ns.Write(b, off, len); }
        public int Read(byte[] b, int off, int len) { return ns.Read(b, off, len); }
        public int ReadByte() { return ns.ReadByte(); }
        public void DiscardInBuffer() { while (c.Available > 0) { var t = new byte[c.Available]; ns.Read(t, 0, t.Length); } }
        public void Dispose() { try { c.Close(); } catch { } }
    }

    internal static class Program
    {
        const byte sDock1 = 1, sDock2 = 2;
        const byte sCommitStart = 0xAA;
        const byte sCommitSettings = 0xAD;
        const byte sSave = 0xFC;
        const byte serialTerminator = 0xFE;

        // Setting keys exactly as the firmware names them, with friendly labels.
        static readonly string[][] Settings =
        {
            new[] { "SolOn",   "Solenoid On length" },
            new[] { "SolOff",  "Solenoid Autofire wait time" },
            new[] { "SolHold", "Solenoid Hold-to-autofire length" },
        };

        static int Main(string[] args)
        {
            Console.Title = "OFSolTool";
            Console.WriteLine("OFSolTool - write OpenFIRE solenoid timings over serial");
            Console.WriteLine("Close the OpenFIRE App first (it holds the COM port).");
            Console.WriteLine();

            bool interactive = args.Length == 0;
            try
            {
                string port;
                var values = new List<KeyValuePair<string, uint>>();

                if (interactive)
                {
                    port = PickPort();
                    if (port == null) return Done(interactive, 1);
                    Console.WriteLine();
                    Console.WriteLine("Enter new values in ms (1-9999). Press Enter to leave a setting unchanged.");
                    foreach (var s in Settings)
                    {
                        uint v;
                        if (AskValue(s[1] + " (" + s[0] + "): ", out v))
                            values.Add(new KeyValuePair<string, uint>(s[0], v));
                    }
                }
                else
                {
                    port = args[0].StartsWith("tcp:") ? args[0] : args[0].ToUpperInvariant();
                    foreach (var a in args.Skip(1))
                    {
                        var m = Regex.Match(a, @"^(\w+)=(\d+)$");
                        var key = m.Success ? Settings.FirstOrDefault(s => s[0].Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) : null;
                        uint v;
                        if (key == null || !uint.TryParse(m.Groups[2].Value, out v) || v < 1 || v > 9999)
                        {
                            Console.WriteLine("Bad argument: " + a + "  (use SolOn=, SolOff= or SolHold= with 1-9999)");
                            return 1;
                        }
                        values.Add(new KeyValuePair<string, uint>(key[0], v));
                    }
                }

                if (values.Count == 0)
                {
                    Console.WriteLine("Nothing to change.");
                    return Done(interactive, 0);
                }

                bool ok = WriteSettings(port, values);
                return Done(interactive, ok ? 0 : 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return Done(interactive, 1);
            }
        }

        static int Done(bool interactive, int code)
        {
            if (interactive)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close.");
                Console.ReadKey(true);
            }
            return code;
        }

        static bool AskValue(string prompt, out uint value)
        {
            while (true)
            {
                Console.Write(prompt);
                string s = (Console.ReadLine() ?? "").Trim();
                value = 0;
                if (s.Length == 0) return false;
                if (uint.TryParse(s, out value) && value >= 1 && value <= 9999) return true;
                Console.WriteLine("  Enter a whole number from 1 to 9999, or just press Enter to skip.");
            }
        }

        // ------------------------------------------------------------ port discovery

        sealed class GunPort
        {
            public string Com;
            public string Pid;
        }

        static List<GunPort> FindOpenFirePorts()
        {
            var list = new List<GunPort>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_F143%'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string name = (mo["Name"] as string) ?? "";
                        string id = (mo["DeviceID"] as string) ?? "";
                        var com = Regex.Match(name, @"\((COM\d+)\)");
                        if (!com.Success) continue;
                        var pid = Regex.Match(id, @"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                        list.Add(new GunPort { Com = com.Groups[1].Value, Pid = pid.Success ? pid.Groups[1].Value : "????" });
                    }
                }
            }
            catch { }
            return list.OrderBy(p => p.Pid).ThenBy(p => p.Com).ToList();
        }

        static string PickPort()
        {
            var guns = FindOpenFirePorts();
            if (guns.Count == 0)
            {
                Console.WriteLine("No OpenFIRE guns found (looking for USB VID F143).");
                Console.WriteLine("Check the gun is plugged in, then run this again.");
                return null;
            }
            Console.WriteLine("OpenFIRE guns found:");
            for (int i = 0; i < guns.Count; i++)
                Console.WriteLine("  " + (i + 1) + ")  " + guns[i].Com + "   (USB PID " + guns[i].Pid + ")");
            if (guns.Count == 1)
            {
                Console.WriteLine("Using " + guns[0].Com + ".");
                return guns[0].Com;
            }
            while (true)
            {
                Console.Write("Which gun? (1-" + guns.Count + "): ");
                int n;
                if (int.TryParse((Console.ReadLine() ?? "").Trim(), out n) && n >= 1 && n <= guns.Count)
                    return guns[n - 1].Com;
            }
        }

        // ------------------------------------------------------------ protocol

        static bool WriteSettings(string com, List<KeyValuePair<string, uint>> values)
        {
            IPort sp;
            try
            {
                if (com.StartsWith("tcp:"))
                {
                    var hp = com.Split(':');
                    sp = new TcpPort(hp[1], int.Parse(hp[2]));
                }
                else sp = new ComPort(com);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine(com + " is in use. Close the OpenFIRE App (and anything else using the gun's COM port) and try again.");
                return false;
            }
            using (sp)
            {
                bool docked = false;
                try
                {
                    // 1. Dock
                    Console.WriteLine();
                    Console.WriteLine("Connecting to " + com + "...");
                    sp.DiscardInBuffer();
                    sp.Write(new byte[] { sDock1, sDock2 }, 0, 2);
                    docked = true;
                    byte[] hello = ReadFor(sp, 1500);
                    string info = DescribeBoard(hello);
                    Console.WriteLine(info ?? "Gun did not identify itself - is this an OpenFIRE gun?");
                    if (info == null) return false;

                    // 2. Commit start
                    Thread.Sleep(150);
                    sp.DiscardInBuffer();
                    sp.Write(new byte[] { sCommitStart, 1 }, 0, 2);
                    if (!WaitForByte(sp, sCommitStart, 2000))
                    {
                        Console.WriteLine("Gun did not accept the settings session.");
                        return false;
                    }

                    // 3. One record per setting: type, name\0, size, value (little-endian uint32)
                    foreach (var kv in values)
                    {
                        byte[] name = Encoding.ASCII.GetBytes(kv.Key);
                        var rec = new List<byte> { sCommitSettings };
                        rec.AddRange(name);
                        rec.Add(0);
                        rec.Add(4);
                        rec.AddRange(BitConverter.GetBytes(kv.Value));
                        sp.Write(rec.ToArray(), 0, rec.Count);

                        // Gun echoes name\0, size, value
                        int expect = rec.Count - 1;
                        byte[] echo = ReadExactly(sp, expect, 2000);
                        bool match = echo != null && echo.SequenceEqual(rec.Skip(1));
                        if (!match)
                        {
                            Console.WriteLine("Gun did not confirm " + kv.Key + " - cancelling, nothing saved.");
                            sp.Write(new byte[] { serialTerminator }, 0, 1);   // abort: gun reloads its saved settings
                            Thread.Sleep(300);
                            return false;
                        }
                        Console.WriteLine("  " + kv.Key + " = " + kv.Value + " ms  (confirmed)");
                    }

                    // 4. Save to flash
                    sp.DiscardInBuffer();
                    sp.Write(new byte[] { sSave }, 0, 1);
                    byte[] reply = ReadFor(sp, 3000, stopAfter: 2);
                    bool saved = reply.Length >= 2 && reply[0] == sSave && reply[1] == 1;
                    Console.WriteLine(saved ? "Saved to the gun." : "Gun did not confirm the save - check the values in the OpenFIRE App.");
                    return saved;
                }
                finally
                {
                    if (docked)
                    {
                        try
                        {
                            Thread.Sleep(200);
                            sp.Write(new byte[] { serialTerminator, serialTerminator, serialTerminator }, 0, 3);  // undock -> back to normal play
                            Thread.Sleep(200);
                        }
                        catch { }
                    }
                }
            }
        }

        // Board hello: version 0xFE board-type 0xFE usb-ident(18 bytes: PID LE16 + 16-char name) ...
        static string DescribeBoard(byte[] hello)
        {
            if (hello.Length < 4) return null;
            var parts = new List<byte[]>();
            int start = 0;
            for (int i = 0; i < hello.Length && parts.Count < 3; i++)
            {
                if (hello[i] == serialTerminator)
                {
                    parts.Add(hello.Skip(start).Take(i - start).ToArray());
                    start = i + 1;
                }
            }
            if (parts.Count < 2) return null;
            string version = Clean(parts[0]);
            string board = Clean(parts[1]);
            string name = null;
            byte[] usb = parts.Count >= 3 ? parts[2] : hello.Skip(start).Take(18).ToArray();
            if (usb.Length >= 18) name = Clean(usb.Skip(2).Take(16).ToArray());
            return "Found: " + (string.IsNullOrEmpty(name) ? "OpenFIRE gun" : name) +
                   "   (firmware " + version + ", board " + board + ")";
        }

        static string Clean(byte[] b)
        {
            var s = Encoding.ASCII.GetString(b);
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            return new string(s.Where(c => c >= 32 && c < 127).ToArray()).Trim();
        }

        static byte[] ReadFor(IPort sp, int ms, int stopAfter = int.MaxValue)
        {
            var buf = new List<byte>();
            var end = DateTime.Now.AddMilliseconds(ms);
            DateTime lastByte = DateTime.MinValue;
            while (DateTime.Now < end && buf.Count < stopAfter)
            {
                if (sp.BytesToRead > 0)
                {
                    var tmp = new byte[sp.BytesToRead];
                    int n = sp.Read(tmp, 0, tmp.Length);
                    buf.AddRange(tmp.Take(n));
                    lastByte = DateTime.Now;
                }
                else
                {
                    // stop once data has arrived and the line has gone quiet
                    if (lastByte != DateTime.MinValue && (DateTime.Now - lastByte).TotalMilliseconds > 250) break;
                    Thread.Sleep(10);
                }
            }
            return buf.ToArray();
        }

        static byte[] ReadExactly(IPort sp, int count, int ms)
        {
            var buf = new List<byte>();
            var end = DateTime.Now.AddMilliseconds(ms);
            while (buf.Count < count && DateTime.Now < end)
            {
                if (sp.BytesToRead > 0)
                {
                    var tmp = new byte[Math.Min(sp.BytesToRead, count - buf.Count)];
                    int n = sp.Read(tmp, 0, tmp.Length);
                    buf.AddRange(tmp.Take(n));
                }
                else Thread.Sleep(5);
            }
            return buf.Count == count ? buf.ToArray() : null;
        }

        static bool WaitForByte(IPort sp, byte want, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                if (sp.BytesToRead > 0)
                {
                    int b = sp.ReadByte();
                    if (b == want) return true;
                }
                else Thread.Sleep(5);
            }
            return false;
        }
    }
}
