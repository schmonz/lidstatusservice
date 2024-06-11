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
            Log("{0}: Service Running", DateTime.Now);

            _lid = new Lid();

            Action<bool> lidEventHandler = status => Log("{0}: Lid status: {1}", DateTime.Now, status);
                
            var registeredNotificationsSuccess = _lid.RegisterLidEventNotifications(ServiceHandle, ServiceName, lidEventHandler);

            Log("{0}: Notifications registered? {1}", DateTime.Now, registeredNotificationsSuccess);
        }

        protected override void OnStop()
        {
            _lid.UnregisterLidEventNotifications();
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