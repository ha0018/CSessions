using System.Runtime.InteropServices;

namespace WindowsChildSession;

internal sealed class RdpWindow : Form
{
    private sealed class RdpControl() : AxHost("A0C63C30-F08D-4AB4-907C-34905D770C7D")
    {
        public object ActiveX => GetOcx() ?? throw new InvalidOperationException("The installed RDP ActiveX control could not be created.");
    }

    // This interface uses an IUnknown vtable. Property writes precede reads.
    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpExtendedSettings
    {
        void SetProperty([MarshalAs(UnmanagedType.BStr)] string name,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    private readonly RdpControl control = new() { Dock = DockStyle.Fill };
    private readonly Label message = new() { Dock = DockStyle.Bottom, Height = 26, Text = "Ready" };

    public RdpWindow()
    {
        Text = "Windows Child Session";
        ClientSize = new Size(1000, 690);
        Controls.Add(control);
        Controls.Add(message);
    }

    public short ConnectionState => (short)((dynamic)control.ActiveX).Connected;

    public void StartConnection()
    {
        if (ConnectionState != 0)
            return;
        if (!WindowsSessions.Enabled())
            throw new InvalidOperationException("Windows child sessions are disabled. See the installation guide.");

        dynamic rdp = control.ActiveX;
        rdp.Server = "localhost";
        rdp.DesktopWidth = 1000;
        rdp.DesktopHeight = 650;
        rdp.ColorDepth = 32;
        dynamic advanced = rdp.AdvancedSettings7;
        advanced.EnableCredSspSupport = true;
        advanced.SmartSizing = true;
        advanced.RedirectDrives = false;
        advanced.RedirectPrinters = false;
        advanced.RedirectClipboard = false;
        object enabled = true;
        ((IRdpExtendedSettings)control.ActiveX).SetProperty("ConnectToChildSession", ref enabled);
        rdp.Connect();
        message.Text = "Connecting to the local child session";
    }

    public void SetMessage(string text) => message.Text = text;

    public void Disconnect()
    {
        if (ConnectionState != 0)
            ((dynamic)control.ActiveX).Disconnect();
    }

    public string DisconnectReason()
    {
        dynamic rdp = control.ActiveX;
        return $"The RDP control disconnected (extended reason {(int)rdp.ExtendedDisconnectReason}). " +
            "Check the child-session window for a Windows sign-in or connection error.";
    }
}
