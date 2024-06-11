using System;
using System.IO;
using System.ServiceProcess;

namespace LidStatusService
{
    public partial class LidStatusService : ServiceBase
    {
        private Lid _lid;

        public LidStatusService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            Log("{0}: service running", DateTime.Now);
            _lid = new Lid();
            Log("{0}: notifications registered? {1}", DateTime.Now, _lid.RegisterLidEventNotifications(ServiceHandle, ServiceName, LidEventHandler));
        }

        private void LidEventHandler(bool status)
        {
            Log("{0}: lid status: {1}", DateTime.Now, status ? "lid opened" : "lid closed");
        }

        protected override void OnStop()
        {
            Log("{0}: notifications unregistered? {1}", DateTime.Now, _lid.UnregisterLidEventNotifications());
        }

        private static void Log(string format, params object[] arg)
        {
            using (var sw = new StreamWriter(@"C:\powerstatus.txt", true))
            {
                sw.WriteLine(format, arg);
            }
        }
    }
}