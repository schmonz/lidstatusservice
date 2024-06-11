using System.ComponentModel;

namespace LidStatusService
{
    [RunInstaller(true)]
    public abstract partial class ProjectInstaller : System.Configuration.Install.Installer
    {
        protected ProjectInstaller()
        {
            InitializeComponent();
        }
    }
}
