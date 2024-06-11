using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace LidStatusService
{
    public partial class LidStatusService : ServiceBase
    {
        public enum ServiceState
        {
            ServiceStopped = 0x00000001,
            ServiceStartPending = 0x00000002,
            ServiceStopPending = 0x00000003,
            ServiceRunning = 0x00000004,
            ServiceContinuePending = 0x00000005,
            ServicePausePending = 0x00000006,
            ServicePaused = 0x00000007
        }

        private Lid _lid;

        public LidStatusService()
        {
            InitializeComponent();
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus serviceStatus);

        protected override void OnStart(string[] args)
        {
            var serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.ServiceStartPending,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);

            Log("{0}: service starting", DateTime.Now);

            _lid = new Lid(ServiceHandle, ServiceName, LidEventHandler);
            // XXX _lid.onOpen(callback)
            // XXX _lid.onClose(callback)

            serviceStatus.dwCurrentState = ServiceState.ServiceRunning;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        private static void LidEventHandler(bool status)
        {
            Log("{0}: lid status: {1}", DateTime.Now, status ? "opened" : "closed");
        }

        protected override void OnStop()
        {
            var serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.ServiceStopPending,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);

            Log("{0}: service stopping", DateTime.Now);

            serviceStatus.dwCurrentState = ServiceState.ServiceStopped;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        private static void Log(string format, params object[] arg)
        {
            using (var sw = new StreamWriter(@"C:\powerstatus.txt", true))
            {
                sw.WriteLine(format, arg);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public int dwServiceType;
            public ServiceState dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }
    }
}