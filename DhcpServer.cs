// Portabler Mini-DHCP-Server fuer Windows (WinForms, .NET Framework 4.x, C# 5)
//
// Bauen (kein Visual Studio noetig, csc.exe ist Teil von Windows):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /optimize+ ^
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:DhcpServer.exe DhcpServer.cs

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        if (!IsAdmin())
        {
            // Selbst-Elevation: Port 67 und IP-Konfiguration brauchen Adminrechte
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Verb = "runas",
                    UseShellExecute = true
                });
            }
            catch (Win32Exception)
            {
                MessageBox.Show("Administratorrechte werden benötigt (Port 67, IP-Konfiguration).", "DHCP-Server");
            }
            return;
        }
        bool createdNew;
        using (Mutex m = new Mutex(true, "Global\\PortableDhcpServer", out createdNew))
        {
            if (!createdNew) { MessageBox.Show("Das Programm läuft bereits.", "DHCP-Server"); return; }
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }

    static bool IsAdmin()
    {
        using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

class Lease
{
    public uint Ip;
    public string Mac;
    public DateTime Exp;
}

// Ursprüngliche Netzwerkkonfiguration einer Schnittstelle (im Speicher + als Sicherungsdatei)
class OrigConfig
{
    public string NicId = "", NicName = "";
    public bool Dhcp, DnsStatic;
    public List<string> Addrs = new List<string>();    // "ip/maske"
    public List<string> Gateways = new List<string>();
    public List<string> Dns = new List<string>();      // nur statische DNS-Server

    public void Save(string path)
    {
        List<string> l = new List<string>();
        l.Add("id=" + NicId);
        l.Add("name=" + NicName);
        l.Add("dhcp=" + (Dhcp ? "1" : "0"));
        l.Add("dnsstatic=" + (DnsStatic ? "1" : "0"));
        foreach (string a in Addrs) l.Add("addr=" + a);
        foreach (string g in Gateways) l.Add("gw=" + g);
        foreach (string d in Dns) l.Add("dns=" + d);
        File.WriteAllLines(path, l.ToArray(), Encoding.UTF8);
    }

    public static OrigConfig Load(string path)
    {
        try
        {
            OrigConfig c = new OrigConfig();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int k = line.IndexOf('=');
                if (k < 0) continue;
                string key = line.Substring(0, k), val = line.Substring(k + 1);
                if (key == "id") c.NicId = val;
                else if (key == "name") c.NicName = val;
                else if (key == "dhcp") c.Dhcp = val == "1";
                else if (key == "dnsstatic") c.DnsStatic = val == "1";
                else if (key == "addr") c.Addrs.Add(val);
                else if (key == "gw") c.Gateways.Add(val);
                else if (key == "dns") c.Dns.Add(val);
            }
            return c.NicId.Length > 0 ? c : null;
        }
        catch (Exception) { return null; }
    }
}

class MainForm : Form
{
    const int LeaseSeconds = 3600;
    const int OfferSeconds = 60;

    // GUI
    ComboBox cbNic = new ComboBox();
    TextBox tbNet = new TextBox(), tbMask = new TextBox(), tbGw = new TextBox(), tbDns = new TextBox();
    Button btn = new Button();
    TextBox logBox = new TextBox();
    List<NetworkInterface> nics = new List<NetworkInterface>();

    // Konfiguration (beim Start berechnet)
    NetworkInterface nic;
    int nicIndex;
    uint netAddr, mask, serverIp, gwIp, first, last;
    List<uint> dns = new List<uint>();

    // Sicherung der ursprünglichen Konfiguration
    OrigConfig orig;
    string restoreFile = Application.ExecutablePath + ".restore";

    // Laufzeit
    volatile bool running;
    Socket recvSock, sendSock;
    readonly object sync = new object();
    Dictionary<string, Lease> byMac = new Dictionary<string, Lease>();
    Dictionary<uint, Lease> byIp = new Dictionary<uint, Lease>();

    public MainForm()
    {
        Text = "Portabler DHCP-Server";
        ClientSize = new Size(624, 485);
        MinimumSize = new Size(500, 400);
        StartPosition = FormStartPosition.CenterScreen;

        AddRow("Netzwerkschnittstelle", cbNic, 12, 440);
        cbNic.DropDownStyle = ComboBoxStyle.DropDownList;
        AddRow("Netzadresse", tbNet, 42, 200);
        AddRow("Subnetzmaske", tbMask, 72, 200);
        AddRow("Gateway (optional)", tbGw, 102, 200);
        AddRow("DNS-Server (optional)", tbDns, 132, 200);
        tbNet.Text = "192.168.100.0";
        tbMask.Text = "255.255.255.0";

        Label hint = new Label();
        hint.Text = "mehrere DNS-Server mit Komma";
        hint.ForeColor = Color.Gray;
        hint.Location = new Point(380, 135);
        hint.AutoSize = true;
        Controls.Add(hint);

        btn.Text = "Starten";
        btn.Location = new Point(400, 55);
        btn.Size = new Size(210, 50);
        btn.Click += OnButton;
        Controls.Add(btn);

        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 9f);
        logBox.Location = new Point(12, 170);
        logBox.Size = new Size(600, 303);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(logBox);

        LoadNics();
        FormClosing += delegate { if (running || orig != null) StopServer(); };
        Shown += delegate { CheckStaleBackup(); };
    }

    void AddRow(string label, Control c, int y, int width)
    {
        Label l = new Label();
        l.Text = label;
        l.Location = new Point(12, y + 3);
        l.Size = new Size(155, 20);
        Controls.Add(l);
        c.Location = new Point(170, y);
        c.Width = width;
        Controls.Add(c);
    }

    void LoadNics()
    {
        foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (!n.Supports(NetworkInterfaceComponent.IPv4)) continue;
            string ip = "-";
            foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) { ip = u.Address.ToString(); break; }
            nics.Add(n);
            cbNic.Items.Add(n.Name + "  |  " + n.Description + "  |  " + n.OperationalStatus + "  |  " + ip);
        }
        if (cbNic.Items.Count > 0) cbNic.SelectedIndex = 0;
    }

    // ---------- Logging ----------
    void Log(string s)
    {
        try
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }
        catch (InvalidOperationException) { } // schließt ObjectDisposedException ein
    }

    // ---------- Hilfsfunktionen ----------
    static uint U(byte[] b, int o)
    {
        return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    }
    static void PutU(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
    static uint ToU(IPAddress a) { return U(a.GetAddressBytes(), 0); }
    static string Str(uint v)
    {
        return string.Format("{0}.{1}.{2}.{3}", v >> 24, (v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
    static bool TryIp(string s, out uint v)
    {
        v = 0;
        IPAddress a;
        s = s.Trim();
        if (s.Split('.').Length != 4 || !IPAddress.TryParse(s, out a) || a.AddressFamily != AddressFamily.InterNetwork)
            return false;
        v = ToU(a);
        return true;
    }

    // ---------- Start / Stop ----------
    void OnButton(object sender, EventArgs e)
    {
        if (running) StopServer(); else BeginStart();
    }

    void SetUi(bool isRunning, bool busy)
    {
        if (InvokeRequired) { BeginInvoke(new Action<bool, bool>(SetUi), isRunning, busy); return; }
        cbNic.Enabled = tbNet.Enabled = tbMask.Enabled = tbGw.Enabled = tbDns.Enabled = !isRunning && !busy;
        btn.Enabled = !busy;
        btn.Text = isRunning ? "Stoppen" : (busy ? "Bitte warten …" : "Starten");
    }

    void BeginStart()
    {
        if (cbNic.SelectedIndex < 0) { MessageBox.Show("Keine Netzwerkschnittstelle ausgewählt."); return; }
        nic = nics[cbNic.SelectedIndex];
        IPv4InterfaceProperties v4 = nic.GetIPProperties().GetIPv4Properties();
        if (v4 == null) { MessageBox.Show("Auf dieser Schnittstelle ist IPv4 nicht aktiv."); return; }
        nicIndex = v4.Index;

        uint ip;
        if (!TryIp(tbNet.Text, out ip)) { MessageBox.Show("Netzadresse ungültig."); return; }
        if (!TryIp(tbMask.Text, out mask)) { MessageBox.Show("Subnetzmaske ungültig."); return; }
        uint inv = ~mask;
        if (mask == 0 || (inv & (inv + 1)) != 0 || inv < 7) { MessageBox.Show("Subnetzmaske ungültig oder Netz zu klein (mindestens /29)."); return; }
        netAddr = ip & mask;
        uint bcast = netAddr | inv;
        first = netAddr + 1;
        last = bcast - 1;

        gwIp = 0;
        if (tbGw.Text.Trim().Length > 0)
        {
            if (!TryIp(tbGw.Text, out gwIp) || gwIp < first || gwIp > last) { MessageBox.Show("Gateway ungültig oder nicht im Netz."); return; }
        }

        dns.Clear();
        foreach (string part in tbDns.Text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            uint d;
            if (!TryIp(part, out d)) { MessageBox.Show("DNS-Server ungültig: " + part); return; }
            dns.Add(d);
        }

        // Server-IP = erste Host-Adresse, außer sie ist das Gateway
        serverIp = first;
        if (serverIp == gwIp) serverIp = first + 1;
        if (last - first < 2) { MessageBox.Show("Netz zu klein."); return; }

        if (ip != netAddr) Log("Hinweis: Netzadresse wurde auf " + Str(netAddr) + " korrigiert.");
        Log(string.Format("Konfiguration: Server-IP {0}, Maske {1}, Pool {2} - {3}{4}{5}",
            Str(serverIp), Str(mask), Str(first), Str(last),
            gwIp != 0 ? ", Gateway " + Str(gwIp) + " (aus Pool ausgenommen)" : "",
            dns.Count > 0 ? ", DNS " + tbDns.Text.Trim() : ""));

        SetUi(false, true);
        Thread t = new Thread(DoStart);
        t.IsBackground = true;
        t.Start();
    }

    void DoStart()
    {
        try
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                Log("Link ist down - der Server startet trotzdem und wartet auf das Kabel.");

            orig = Capture(nic);
            try { orig.Save(restoreFile); }
            catch (Exception ex) { Log("Warnung: Sicherungsdatei konnte nicht geschrieben werden: " + ex.Message); }
            Log("Ursprüngliche Konfiguration gesichert: " + (orig.Dhcp ? "DHCP" : string.Join(", ", orig.Addrs.ToArray())) +
                (orig.DnsStatic ? " | DNS statisch: " + string.Join(", ", orig.Dns.ToArray()) : ""));

            Log("Setze statische IP " + Str(serverIp) + " auf '" + nic.Name + "' …");
            if (!Netsh(string.Format("interface ipv4 set address name=\"{0}\" source=static address={1} mask={2} gateway=none",
                    nic.Name, Str(serverIp), Str(mask))))
            {
                Log("FEHLER: IP konnte nicht gesetzt werden.");
                Revert();
                SetUi(false, false);
                return;
            }

            byMac.Clear();
            byIp.Clear();

            recvSock = NewSock();
            recvSock.Bind(new IPEndPoint(IPAddress.Any, 67));

            running = true;
            StartLoop(recvSock);
            Thread mon = new Thread(MonitorLoop);
            mon.IsBackground = true;
            mon.Start();
            Log("DHCP-Server gestartet.");
            SetUi(true, false);
        }
        catch (SocketException ex)
        {
            Log("FEHLER beim Öffnen von Port 67: " + ex.Message + " (belegt? z. B. durch Internetverbindungsfreigabe oder anderen DHCP-Server)");
            CloseSocks();
            Revert();
            SetUi(false, false);
        }
        catch (Exception ex)
        {
            Log("FEHLER: " + ex.Message);
            CloseSocks();
            Revert();
            SetUi(false, false);
        }
    }

    static Socket NewSock()
    {
        Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        return s;
    }

    // 0 = Link down, 1 = Link up aber Adresse noch nicht nutzbar, 2 = bereit
    int LinkState()
    {
        try
        {
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.Id != nic.Id) continue;
                if (n.OperationalStatus != OperationalStatus.Up) return 0;
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && ToU(u.Address) == serverIp &&
                        u.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                        return 2;
                return 1;
            }
        }
        catch (Exception) { }
        return 0;
    }

    // Beobachtet den Link: Sende-Socket (an die Server-IP gebunden) gibt es nur, solange Link und Adresse bereit sind
    void MonitorLoop()
    {
        int lastState = -1;
        bool failLogged = false;
        while (running)
        {
            int st = LinkState();
            if (st != lastState)
            {
                lock (sync)
                {
                    if (!running) break;
                    if (st == 2)
                    {
                        try
                        {
                            Socket sk = NewSock();
                            sk.Bind(new IPEndPoint(new IPAddress(Four(serverIp)), 67));
                            sendSock = sk;
                            StartLoop(sk);
                            Log("Link up, Adresse bereit - Server aktiv, warte auf Anfragen …");
                            failLogged = false;
                            lastState = st;
                        }
                        catch (SocketException ex)
                        {
                            if (!failLogged) Log("Socket an " + Str(serverIp) + ":67 fehlgeschlagen (wird wiederholt): " + ex.Message);
                            failLogged = true;
                        }
                    }
                    else
                    {
                        if (sendSock != null) { try { sendSock.Close(); } catch (Exception) { } sendSock = null; }
                        Log(st == 0 ? "Link down - warte auf Kabel …" : "Link up, warte auf nutzbare Adresse (Adresskonflikt?) …");
                        lastState = st;
                    }
                }
            }
            Thread.Sleep(1000);
        }
    }

    void StartLoop(Socket s)
    {
        Thread t = new Thread(delegate() { Loop(s); });
        t.IsBackground = true;
        t.Start();
    }

    void CloseSocks()
    {
        lock (sync)
        {
            try { if (recvSock != null) recvSock.Close(); } catch (Exception) { }
            try { if (sendSock != null) sendSock.Close(); } catch (Exception) { }
            recvSock = sendSock = null;
        }
    }

    void StopServer()
    {
        running = false;
        CloseSocks();
        lock (sync) { byMac.Clear(); byIp.Clear(); }
        Log("Server gestoppt.");
        Cursor = Cursors.WaitCursor;
        Revert();
        Cursor = Cursors.Default;
        SetUi(false, false);
    }

    // ---------- Sichern / Wiederherstellen der Netzwerkkonfiguration ----------
    static OrigConfig Capture(NetworkInterface n)
    {
        OrigConfig c = new OrigConfig();
        c.NicId = n.Id;
        c.NicName = n.Name;
        IPInterfaceProperties props = n.GetIPProperties();
        c.Dhcp = props.GetIPv4Properties().IsDhcpEnabled;
        foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
            if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                c.Addrs.Add(u.Address.ToString() + "/" + u.IPv4Mask.ToString());
        foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
            if (g.Address.AddressFamily == AddressFamily.InterNetwork)
                c.Gateways.Add(g.Address.ToString());
        try
        {
            // 'NameServer' enthält nur manuell eingetragene DNS-Server (leer = per DHCP)
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + n.Id))
            {
                string ns = k == null ? null : k.GetValue("NameServer") as string;
                if (!string.IsNullOrEmpty(ns))
                    foreach (string d in ns.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        c.Dns.Add(d);
            }
        }
        catch (Exception) { }
        c.DnsStatic = c.Dns.Count > 0;
        return c;
    }

    bool Netsh(string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (o.Length > 0) Log("netsh: " + o);
            return p.ExitCode == 0;
        }
    }

    bool Restore(OrigConfig c)
    {
        string name = c.NicName;
        int idx = -1;
        foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            if (n.Id == c.NicId)
            {
                name = n.Name; // aktueller Name, falls umbenannt
                try { idx = n.GetIPProperties().GetIPv4Properties().Index; } catch (Exception) { }
            }
        Log("Stelle ursprüngliche Konfiguration von '" + name + "' wieder her …");

        bool ok = false;
        if (c.Dhcp && idx >= 0)
        {
            // netsh meldet bei Link down teils "DHCP bereits aktiviert", obwohl die statische IP bleibt -> PowerShell zuerst
            ok = RestoreDhcpPs(idx, c);
            if (!ok) Log("PowerShell-Weg fehlgeschlagen, versuche netsh …");
        }
        if (!ok) ok = RestoreNetsh(c, name);

        Log("Kontrolle: " + DescribeState(c.NicId));
        Log(ok ? "Ursprüngliche Konfiguration wiederhergestellt." : "WARNUNG: Wiederherstellung war nicht vollständig erfolgreich.");
        return ok;
    }

    static string DescribeState(string nicId)
    {
        try
        {
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.Id != nicId) continue;
                List<string> ips = new List<string>();
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) ips.Add(u.Address.ToString());
                return "DHCP " + (n.GetIPProperties().GetIPv4Properties().IsDhcpEnabled ? "aktiv" : "AUS") +
                       ", Adressen: " + (ips.Count > 0 ? string.Join(", ", ips.ToArray()) : "keine") + ", Status: " + n.OperationalStatus;
            }
        }
        catch (Exception) { }
        return "unbekannt";
    }

    bool RestoreDhcpPs(int idx, OrigConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("try {");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-NetIPInterface -InterfaceIndex " + idx + " -AddressFamily IPv4 -Dhcp Enabled");
        sb.AppendLine("Get-NetIPAddress -InterfaceIndex " + idx + " -AddressFamily IPv4 -PrefixOrigin Manual -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false");
        if (c.DnsStatic)
            sb.AppendLine("Set-DnsClientServerAddress -InterfaceIndex " + idx + " -ServerAddresses (" +
                string.Join(",", c.Dns.ConvertAll(x => "'" + x + "'").ToArray()) + ")");
        else
            sb.AppendLine("Set-DnsClientServerAddress -InterfaceIndex " + idx + " -ResetServerAddresses");
        sb.AppendLine("} catch { Write-Output ('PowerShell: ' + $_.Exception.Message); exit 1 }");
        return Ps(sb.ToString());
    }

    bool Ps(string script)
    {
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (o.Length > 0) Log(o);
            return p.ExitCode == 0;
        }
    }

    bool RestoreNetsh(OrigConfig c, string name)
    {
        bool ok = true;
        if (c.Dhcp)
        {
            ok &= Netsh(string.Format("interface ipv4 set address name=\"{0}\" source=dhcp", name));
        }
        else if (c.Addrs.Count > 0)
        {
            string[] a = c.Addrs[0].Split('/');
            ok &= Netsh(string.Format("interface ipv4 set address name=\"{0}\" source=static address={1} mask={2} gateway={3}",
                name, a[0], a[1], c.Gateways.Count > 0 ? c.Gateways[0] : "none"));
            for (int i = 1; i < c.Addrs.Count; i++)
            {
                string[] x = c.Addrs[i].Split('/');
                ok &= Netsh(string.Format("interface ipv4 add address name=\"{0}\" address={1} mask={2}", name, x[0], x[1]));
            }
        }
        else Log("Hinweis: Ursprünglich keine IPv4-Adresse konfiguriert, Adresse bleibt unverändert.");

        // DNS explizit zurücksetzen (set address static kann die DNS-Einstellung verändern)
        if (c.DnsStatic)
        {
            ok &= Netsh(string.Format("interface ipv4 set dnsservers name=\"{0}\" source=static address={1} validate=no", name, c.Dns[0]));
            for (int i = 1; i < c.Dns.Count; i++)
                ok &= Netsh(string.Format("interface ipv4 add dnsservers name=\"{0}\" address={1} index={2} validate=no", name, c.Dns[i], i + 1));
        }
        else
            ok &= Netsh(string.Format("interface ipv4 set dnsservers name=\"{0}\" source=dhcp", name));
        return ok;
    }

    void Revert()
    {
        if (orig == null) return;
        if (Restore(orig)) { DeleteBackup(); orig = null; }
        else Log("Die Sicherung bleibt erhalten und wird beim nächsten Start angeboten: " + restoreFile);
    }

    void DeleteBackup()
    {
        try { if (File.Exists(restoreFile)) File.Delete(restoreFile); } catch (Exception) { }
    }

    // Programm wurde zuvor hart beendet (Absturz, Task-Manager, Stromausfall) -> Wiederherstellung anbieten
    void CheckStaleBackup()
    {
        if (!File.Exists(restoreFile)) return;
        OrigConfig c = OrigConfig.Load(restoreFile);
        if (c == null) { DeleteBackup(); return; }
        DialogResult r = MessageBox.Show(this,
            "Die letzte Sitzung wurde nicht sauber beendet.\r\nUrsprüngliche Konfiguration von '" + c.NicName + "' wiederherstellen?",
            "DHCP-Server", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes)
        {
            Cursor = Cursors.WaitCursor;
            bool ok = Restore(c);
            Cursor = Cursors.Default;
            if (!ok) { Log("Die Sicherung bleibt erhalten: " + restoreFile); return; }
        }
        DeleteBackup();
    }

    // ---------- Empfang ----------
    void Loop(Socket s)
    {
        byte[] buf = new byte[1500];
        while (running)
        {
            try
            {
                EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                SocketFlags fl = SocketFlags.None;
                IPPacketInformation pi;
                int n = s.ReceiveMessageFrom(buf, 0, buf.Length, ref fl, ref ep, out pi);
                if (pi.Interface != nicIndex) continue; // nur Anfragen von der gewählten Schnittstelle
                lock (sync) { HandlePacket(buf, n); }
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!running) break; }
            catch (Exception ex) { Log("Fehler: " + ex.Message); }
        }
    }

    void HandlePacket(byte[] b, int n)
    {
        if (n < 241 || b[0] != 1) return;
        if (b[236] != 99 || b[237] != 130 || b[238] != 83 || b[239] != 99) return;

        int hlen = b[2] == 0 || b[2] > 16 ? 6 : b[2];
        string mac = BitConverter.ToString(b, 28, hlen).Replace('-', ':');

        int type = 0;
        uint reqIp = 0, srvId = 0;
        string host = null;
        int i = 240;
        while (i < n && b[i] != 255)
        {
            if (b[i] == 0) { i++; continue; }
            if (i + 1 >= n) break;
            byte c = b[i];
            int len = b[i + 1];
            if (i + 2 + len > n) break;
            if (c == 53 && len >= 1) type = b[i + 2];
            else if (c == 50 && len == 4) reqIp = U(b, i + 2);
            else if (c == 54 && len == 4) srvId = U(b, i + 2);
            else if (c == 12) host = Encoding.ASCII.GetString(b, i + 2, len);
            i += 2 + len;
        }
        uint ciaddr = U(b, 12);
        string who = mac + (string.IsNullOrEmpty(host) ? "" : " (" + host + ")");

        switch (type)
        {
            case 1: // DISCOVER
                Log("DISCOVER von " + who);
                uint offer = Allocate(mac, reqIp);
                if (offer == 0) { Log("  -> Pool erschöpft, keine Antwort"); break; }
                Send(Build(b, 2, offer));
                Log("OFFER   an " + mac + ": " + Str(offer));
                break;

            case 3: // REQUEST
                Log("REQUEST von " + who + (reqIp != 0 ? ", angefordert " + Str(reqIp) : ""));
                if (srvId != 0 && srvId != serverIp)
                {
                    Lease old;
                    if (byMac.TryGetValue(mac, out old)) Remove(old);
                    Log("  -> Client hat anderen Server gewählt (" + Str(srvId) + "), ignoriert");
                    break;
                }
                uint want = reqIp != 0 ? reqIp : ciaddr;
                if (want != 0 && CanLease(mac, want))
                {
                    Reserve(mac, want, LeaseSeconds);
                    Send(Build(b, 5, want));
                    Log("ACK     an " + mac + ": " + Str(want));
                }
                else
                {
                    Send(Build(b, 6, 0));
                    Log("NAK     an " + mac + (want != 0 ? " (" + Str(want) + " nicht vergebbar)" : ""));
                }
                break;

            case 4: // DECLINE
            case 7: // RELEASE
                Lease l;
                if (byMac.TryGetValue(mac, out l)) { Remove(l); Log((type == 7 ? "RELEASE" : "DECLINE") + " von " + who + ": " + Str(l.Ip) + " freigegeben"); }
                else Log((type == 7 ? "RELEASE" : "DECLINE") + " von " + who + " (kein Lease bekannt)");
                break;

            case 8:
                Log("INFORM von " + who + " (wird nicht beantwortet)");
                break;

            default:
                Log("Unbekannter DHCP-Typ " + type + " von " + who);
                break;
        }
    }

    // ---------- Lease-Verwaltung (immer unter lock(sync)) ----------
    bool InPool(uint u) { return u >= first && u <= last && u != serverIp && u != gwIp; }

    bool CanLease(string mac, uint u)
    {
        if (!InPool(u)) return false;
        Lease l;
        return !byIp.TryGetValue(u, out l) || l.Mac == mac || l.Exp < DateTime.Now;
    }

    void Remove(Lease l)
    {
        Lease x;
        if (byIp.TryGetValue(l.Ip, out x) && x == l) byIp.Remove(l.Ip);
        if (byMac.TryGetValue(l.Mac, out x) && x == l) byMac.Remove(l.Mac);
    }

    void Reserve(string mac, uint ip, int seconds)
    {
        Lease old;
        if (byMac.TryGetValue(mac, out old)) Remove(old);
        if (byIp.TryGetValue(ip, out old)) Remove(old);
        Lease l = new Lease();
        l.Ip = ip; l.Mac = mac; l.Exp = DateTime.Now.AddSeconds(seconds);
        byMac[mac] = l;
        byIp[ip] = l;
    }

    uint Allocate(string mac, uint requested)
    {
        Lease l;
        if (byMac.TryGetValue(mac, out l) && InPool(l.Ip))
        {
            DateTime min = DateTime.Now.AddSeconds(OfferSeconds);
            if (l.Exp < min) l.Exp = min;
            return l.Ip;
        }
        if (requested != 0 && CanLease(mac, requested)) { Reserve(mac, requested, OfferSeconds); return requested; }
        for (uint u = first; u <= last; u++)
        {
            if (CanLease(mac, u) && InPool(u)) { Reserve(mac, u, OfferSeconds); return u; }
            if (u == uint.MaxValue) break;
        }
        return 0;
    }

    // ---------- Antwort ----------
    byte[] Build(byte[] req, byte msgType, uint yiaddr)
    {
        List<byte> o = new List<byte>();
        Opt(o, 53, msgType);
        Opt(o, 54, Four(serverIp));
        if (msgType != 6)
        {
            Opt(o, 51, Four((uint)LeaseSeconds));
            Opt(o, 1, Four(mask));
            if (gwIp != 0) Opt(o, 3, Four(gwIp));
            if (dns.Count > 0)
            {
                List<byte> d = new List<byte>();
                foreach (uint x in dns) d.AddRange(Four(x));
                Opt(o, 6, d.ToArray());
            }
        }
        o.Add(255);

        byte[] p = new byte[Math.Max(300, 240 + o.Count)];
        p[0] = 2;
        p[1] = req[1];
        p[2] = req[2];
        Array.Copy(req, 4, p, 4, 4);    // xid
        Array.Copy(req, 10, p, 10, 2);  // flags
        PutU(p, 16, yiaddr);
        if (msgType != 6) PutU(p, 20, serverIp);
        Array.Copy(req, 24, p, 24, 4);  // giaddr
        Array.Copy(req, 28, p, 28, 16); // chaddr
        p[236] = 99; p[237] = 130; p[238] = 83; p[239] = 99;
        o.CopyTo(p, 240);
        return p;
    }

    static byte[] Four(uint v) { byte[] b = new byte[4]; PutU(b, 0, v); return b; }

    static void Opt(List<byte> l, byte code, params byte[] data)
    {
        l.Add(code);
        l.Add((byte)data.Length);
        l.AddRange(data);
    }

    void Send(byte[] pkt)
    {
        Socket sk = sendSock;
        if (sk == null) { Log("  -> Antwort nicht möglich (Link/Adresse nicht bereit)"); return; }
        try { sk.SendTo(pkt, new IPEndPoint(IPAddress.Broadcast, 68)); }
        catch (Exception ex) { Log("Senden fehlgeschlagen: " + ex.Message); }
    }
}
