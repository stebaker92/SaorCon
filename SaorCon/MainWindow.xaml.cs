using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using System.Windows.Markup;
using Hardcodet.Wpf.TaskbarNotification;
using System.Drawing;

namespace SaorCon
{
    public partial class MainWindow : Window
    {
        private static string Qc35Query =
            "System.Devices.DevObjectType:=5 AND " +
            "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\" AND " +
            "(System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True OR System.Devices.Aep.Bluetooth.IssueInquiry:=System.StructuredQueryType.Boolean#False) AND " +
            "System.DeviceInterface.Bluetooth.VendorId:=158 AND " +
            "(System.DeviceInterface.Bluetooth.ProductId:=16396 OR System.DeviceInterface.Bluetooth.ProductId:=16416)";

        public MainWindow()
        {
            InitializeComponent();
            
            m_deviceWatcher = DeviceInformation.CreateWatcher( Qc35Query );
            m_deviceWatcher.Added += OnBluetoothDeviceAdded;
            m_deviceWatcher.Removed += OnBluetoothDeviceRemoved;
            m_deviceWatcher.Start();
        }

        private void OnBluetoothDeviceAdded( DeviceWatcher sender, DeviceInformation device )
        {
            if ( m_devices.Find( x => x.DeviceId == device.Id ) == null )
            {
                Task.Factory.StartNew( async () =>
                    {
                        var boseDevice = new BoseDeviceDefault( await BluetoothDevice.FromIdAsync( device.Id ) );
                        boseDevice.BatteryUpdated += HandleBatteryLevelUpdate;
                        m_devices.Add( boseDevice );

                        if ( m_quickMenu != null )
                        {
                            m_quickMenu.OnDeviceAdded( boseDevice );
                        }
                    } );
            }
        }

        private void HandleBatteryLevelUpdate(BoseDeviceDefault device, int batteryLevel)
        {
            const int lowBatteryErrorThreshold = 20;
            const int lowBatteryWarnThreshold = 30;

            // TODO - this code assumes we only have 1 Bose device connected

            var iconToUse = SelectIcon(batteryLevel);
            // See https://stackoverflow.com/questions/74466/how-do-i-use-an-icon-that-is-a-resource-in-wpf
            MyNotifyIcon.Icon = System.Drawing.Icon.FromHandle(iconToUse.Handle);

            if (batteryLevel <= lowBatteryErrorThreshold && device.LastLowBatteryNotificationPercentage > lowBatteryErrorThreshold)
            {
                MyNotifyIcon.ShowBalloonTip("Low Battery Warning", $"{device.DeviceName} has under {lowBatteryErrorThreshold}% battery remaining", BalloonIcon.Error);
                device.LastLowBatteryNotificationPercentage = lowBatteryErrorThreshold;
            }
            else if (batteryLevel <= lowBatteryWarnThreshold && device.LastLowBatteryNotificationPercentage > lowBatteryWarnThreshold)
            {
                MyNotifyIcon.ShowBalloonTip("Low Battery Warning", $"{device.DeviceName} has under {lowBatteryWarnThreshold}% battery remaining", BalloonIcon.Warning);
                device.LastLowBatteryNotificationPercentage = lowBatteryWarnThreshold;
            }
        }

        private Icon SelectIcon(int batteryLevel)
        {
            switch (batteryLevel)
            {
                case int n when n >= 70:
                default:
                    return Properties.Resources.BatteryIcon_Hi;
                case int n when n < 70 && n >= 30:
                    return Properties.Resources.BatteryIcon_Mid;
                case int n when n < 30:
                    return Properties.Resources.BatteryIcon_Low;
            }
        }

        private void OnBluetoothDeviceRemoved( DeviceWatcher sender, DeviceInformationUpdate devInfo )
        {
            m_devices.RemoveAll( x => x.DeviceId == devInfo.Id );
            
            if ( m_quickMenu != null )
            {
                m_quickMenu.OnDeviceRemoved( devInfo.Id );
            }
        }

        /*
         * Show SaorCon quick-menu device list, if not already visible
         */
        private void NotifyIcon_OnMouseUp( object sender, RoutedEventArgs e )
        {
            if ( m_quickMenu != null )
            {
                m_quickMenu.Focus();
                m_quickMenu.Activate();
                m_quickMenu.Show();
                return;
            }

            m_quickMenu = new SaorConMenu( m_devices );
            m_quickMenu.Closed += onQuickMenuClosed;
            m_quickMenu.Show();
            m_quickMenu.Focus();
            m_quickMenu.Activate();
        }

        protected override void OnClosing( System.ComponentModel.CancelEventArgs e )
        {
            MyNotifyIcon.Dispose();
            base.OnClosing( e );
        }

        private void onQuickMenuClosed( object sender, EventArgs e )
        {
            m_quickMenu = null;
        }

        private SaorConMenu             m_quickMenu;
        private List<IBoseDevice>       m_devices = new List<IBoseDevice>();
        private DeviceWatcher           m_deviceWatcher;
    }

    // TODO - move commands out to own files
    public abstract class CommandBase<T> : MarkupExtension, ICommand
        where T : class, ICommand, new()
    {
        private static T s_command { get; } = new T();

        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public virtual bool CanExecute( object parameter )
        {
            return true;
        }

        public override object ProvideValue( IServiceProvider serviceProvider )
        {
            return s_command;
        }

        public abstract void Execute( object parameter );
    }

    public class ExitCommand : CommandBase<ExitCommand>
    {
        public override void Execute( object parameter )
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    public class ShowSettingsCommand : CommandBase<ShowSettingsCommand>
    {
        public override void Execute( object parameter )
        {
            new SettingsWindow().Show();
        }
    }


}
