using System.ServiceProcess;

namespace LidStatusService
{
    internal static class Program
    {
        private static void Main()
        {
            var servicesToRun = new ServiceBase[]
            {
                new LidStatusService()
            };
            ServiceBase.Run(servicesToRun);
        }
    }
}