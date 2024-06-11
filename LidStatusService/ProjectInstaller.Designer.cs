﻿namespace LidStatusService
{
    partial class ProjectInstaller
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.LidStatusServiceProcessInstaller1 = new System.ServiceProcess.ServiceProcessInstaller();
            this.LidStatusServiceInstaller = new System.ServiceProcess.ServiceInstaller();
            // 
            // LidStatusServiceProcessInstaller1
            // 
            this.LidStatusServiceProcessInstaller1.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.LidStatusServiceProcessInstaller1.Password = null;
            this.LidStatusServiceProcessInstaller1.Username = null;
            // 
            // LidStatusServiceInstaller
            // 
            this.LidStatusServiceInstaller.Description = "Lid Status";
            this.LidStatusServiceInstaller.DisplayName = "Lid Status Service";
            this.LidStatusServiceInstaller.ServiceName = "LidStatusService";
            // 
            // ProjectInstaller
            // 
            this.Installers.AddRange(new System.Configuration.Install.Installer[] {
            this.LidStatusServiceProcessInstaller1,
            this.LidStatusServiceInstaller});

        }

        #endregion

        private System.ServiceProcess.ServiceProcessInstaller LidStatusServiceProcessInstaller1;
        private System.ServiceProcess.ServiceInstaller LidStatusServiceInstaller;
    }
}