using System;
using System.Runtime.InteropServices;

namespace LidStatusService
{
    public class Lid
    {
        private const int DeviceNotifyServiceHandle = 0x00000001;
        private const int ServiceControlPowerEvent = 0x0000000D;
        private const int PbtPowerSettingChange = 0x8013;

        private static Guid _guidLidSwitchStateChange =
            new Guid(0xBA3E0F4D, 0xB817, 0x4094, 0xA2, 0xD1, 0xD5, 0x63, 0x79, 0xE6, 0xA0, 0xF3);

        private IntPtr _powerSettingsNotificationHandle;

        public Lid(IntPtr serviceHandle, string serviceName, Action<bool> lidEventHandler)
        {
            LidEventHandler = lidEventHandler;
            RegisterEventNotifications(serviceHandle, serviceName);
        }

        ~Lid()
        {
            UnregisterEventNotifications();
        }

        private event Action<bool> LidEventHandler;

        [DllImport(@"User32", SetLastError = true, EntryPoint = "RegisterPowerSettingNotification",
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid,
            int flags);

        [DllImport("User32", EntryPoint = "UnregisterPowerSettingNotification",
            CallingConvention = CallingConvention.StdCall)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr RegisterServiceCtrlHandlerEx(string lpServiceName,
            ServiceControlHandlerEx serviceControlHandler, IntPtr context);

        private void RegisterEventNotifications(IntPtr serviceHandle, string serviceName)
        {
            _powerSettingsNotificationHandle = RegisterPowerSettingNotification(serviceHandle,
                ref _guidLidSwitchStateChange, DeviceNotifyServiceHandle);

            RegisterServiceCtrlHandlerEx(serviceName, MessageHandler, IntPtr.Zero);
        }

        private void UnregisterEventNotifications()
        {
            if (_powerSettingsNotificationHandle != IntPtr.Zero)
                UnregisterPowerSettingNotification(_powerSettingsNotificationHandle);
        }

        private static bool IsEvent(int dwControl, int dwEventType, IntPtr lpEventData)
        {
            // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nc-winsvc-lphandler_function_ex

            if (dwControl != ServiceControlPowerEvent || dwEventType != PbtPowerSettingChange)
                return false;

            return ExtractPowerBroadcastSetting(lpEventData).PowerSetting == _guidLidSwitchStateChange;
        }

        private static bool GetState(IntPtr lpEventData)
        {
            return 0 != ExtractPowerBroadcastSetting(lpEventData).Data;
        }

        private static PowerBroadcastSetting ExtractPowerBroadcastSetting(IntPtr lpEventData)
        {
            return (PowerBroadcastSetting)Marshal.PtrToStructure(lpEventData, typeof(PowerBroadcastSetting));
        }

        private IntPtr MessageHandler(int dwControl, int dwEventType, IntPtr lpEventData, IntPtr lpContext)
        {
            if (IsEvent(dwControl, dwEventType, lpEventData)) LidEventHandler?.Invoke(GetState(lpEventData));
            return IntPtr.Zero;
        }

        private delegate IntPtr ServiceControlHandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }
    }
}