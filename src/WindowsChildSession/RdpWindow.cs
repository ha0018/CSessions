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
    private readonly System.Windows.Forms.Timer resizeTimer = new() { Interval = 200 };
    private bool connectionStarted;

    public RdpWindow()
    {
        Text = "Windows Child Session";
        ClientSize = new Size(1000, 690);
        Controls.Add(control);
        Controls.Add(message);
        control.ClientSizeChanged += (_, _) => ScheduleDisplayUpdate();
        resizeTimer.Tick += (_, _) => UpdateDisplay();
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
        var desktop = DesktopSize();
        rdp.DesktopWidth = desktop.Width;
        rdp.DesktopHeight = desktop.Height;
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
        connectionStarted = true;
        message.Text = "Connecting to the local child session";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || !connectionStarted || e.CloseReason == CloseReason.WindowsShutDown)
            return;

        try { WindowsSessions.LogOffChild(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            message.Text = $"Could not log off the child session: {ex.Message}";
        }
    }

    public void ShowConnected(uint child)
    {
        message.Text = $"Windows child session {child}";
        ScheduleDisplayUpdate();
    }

    private Size DesktopSize()
    {
        // The RDP display channel accepts 200–8192 pixels per axis and an even width.
        var size = control.ClientSize;
        return new Size(Math.Clamp(size.Width, 200, 8192) & ~1, Math.Clamp(size.Height, 200, 8192));
    }

    private void ScheduleDisplayUpdate()
    {
        resizeTimer.Stop();
        if (connectionStarted && WindowState != FormWindowState.Minimized)
            resizeTimer.Start();
    }

    private void UpdateDisplay()
    {
        resizeTimer.Stop();
        if (WindowState == FormWindowState.Minimized || ConnectionState != 1)
            return;

        var size = DesktopSize();
        var dpi = control.DeviceDpi;
        try
        {
            ((dynamic)control.ActiveX).UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height,
                (uint)Math.Round(size.Width * 25.4 / dpi), (uint)Math.Round(size.Height * 25.4 / dpi),
                0u, (uint)Math.Clamp(dpi * 100 / 96, 100, 500), 100u);
        }
        catch (COMException ex)
        {
            message.Text = $"Could not resize the child desktop: {ex.Message}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            resizeTimer.Dispose();
        base.Dispose(disposing);
    }

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
