using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

[assembly: AssemblyTitle("OmniClean")]
[assembly: AssemblyProduct("OmniClean Universal Windows Performance & Storage Suite")]
[assembly: AssemblyCompany("Junaed Khandakar")]
[assembly: AssemblyCopyright("Copyright 2026 Junaed Khandakar")]
[assembly: AssemblyVersion("1.0.5.0")]
[assembly: AssemblyFileVersion("1.0.5.0")]

namespace OmniClean {
    public class CategoryItem {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconText { get; set; }
        public string IconBgHex { get; set; }
        public double SizeMB { get; set; }
        public bool IsInstalled { get; set; }
        public CheckBox CheckBoxRef { get; set; }
        public TextBlock BadgeRef { get; set; }
        public Border CardRef { get; set; }
    }

    public class LargeFileInfo {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Extension { get; set; }
        public long Bytes { get; set; }
        public double SizeMB { get; set; }
        public Border CardRef { get; set; }
    }

    public class DuplicateFileGroup {
        public string Hash { get; set; }
        public long Bytes { get; set; }
        public double SizeMB { get; set; }
        public List<string> FilePaths { get; set; }
    }

    public class StartupAppItem {
        public string Name { get; set; }
        public string Command { get; set; }
        public bool IsEnabled { get; set; }
        // True when the entry lives in HKLM and therefore needs
        // Administrator rights to toggle.
        public bool IsMachine { get; set; }
        public Border CardRef { get; set; }
        public Button ToggleBtnRef { get; set; }
        public TextBlock StatusTextRef { get; set; }
    }

    partial class Program {
        const string CURRENT_VERSION = "v1.1.0";
        const string REPO_URL = "https://github.com/Junaed93/OmniClean";
        const string RELEASES_API = "https://api.github.com/repos/Junaed93/OmniClean/releases/latest";
        const string TASK_NAME = "OmniCleanUniversalAutoClean";
        // The web edition schedules the same maintenance job under a different
        // name. Both are queried so the UI never shows "no schedule" while a
        // job created by the other edition is actually running.
        const string WEB_TASK_NAME = "StorageOptimizerAutoClean";

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
        const uint SHERB_NOCONFIRMATION = 0x00000001;
        const uint SHERB_NOPROGRESSUI   = 0x00000002;
        const uint SHERB_NOSOUND        = 0x00000004;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct SHQUERYRBINFO {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("psapi.dll")]
        static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWM_WINDOW_CORNER_ROUND = 2;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_MAINWINDOW = 2; // Windows 11 Mica

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        static Window mainWindow;
        static Border titleBar;
        static Button btnMinimize;
        static Button btnMaximize;
        static Button btnClose;

        // Navigation Controls
        static Button btnNavCleaner;
        static Button btnNavLargeFiles;
        static Button btnNavDuplicates;
        static Button btnNavStartup;
        static Button btnNavSettings;
        static Border barNavCleaner;
        static Border barNavLargeFiles;
        static Border barNavDuplicates;
        static Border barNavStartup;
        static Border barNavSettings;
        static TabControl mainTabControl;

        // Sidebar Telemetry
        static TextBlock txtSidebarCpu;
        static TextBlock txtSidebarRam;
        static ProgressBar barSidebarRam;
        static Button btnQuickFlushRam;
        static TextBlock txtSidebarUptime;

        // Update banner
        static Border bannerUpdate;
        static TextBlock txtUpdateNotice;
        static Button btnDownloadUpdate;

        // Tab 1: Storage Cleaner
        static ComboBox cmbDrive;
        static ProgressBar diskProgress;
        static TextBlock txtDiskPercent;
        static TextBlock txtDiskRatio;
        static TextBlock txtStatFree;
        static TextBlock txtStatUsed;
        static TextBlock txtStatTotal;
        static TextBlock txtSelectedSize;
        static TextBlock txtHeaderUptimePill;
        static Button btnClean;
        static Button btnRescan;
        static Button btnOptimizeRam;
        static Button btnSelectAll;
        static Button btnSelectSafe;
        static Button btnClearAll;
        static CheckBox chkDryRun;
        static TextBox txtConsole;
        static Button btnExportLog;
        static Button btnClearLog;
        static StackPanel pnlCategories;
        static Border bannerSuccess;
        static TextBlock txtSuccessMsg;

        // Tab 2: Large Files
        static Button btnScanLargeFiles;
        static Button btnFilterAll100;
        static Button btnFilter500MB;
        static Button btnFilter1GB;
        static StackPanel pnlLargeFiles;
        static TextBlock txtLargeFilesSummary;

        // Tab 3: Duplicate Finder
        static Button btnScanDuplicates;
        static Button btnCleanAllDuplicates;
        static StackPanel pnlDuplicates;
        static TextBlock txtDuplicatesSummary;

        // Tab 4: Startup Apps
        static Button btnScanStartup;
        static StackPanel pnlStartupApps;
        static TextBlock txtStartupSummary;

        // Tab 5: Scheduler & Settings
        static CheckBox chkAutoClean;
        static TextBlock txtScheduleStatus;
        static Button btnCreateShortcut;
        static Button btnTestSilentClean;
        static Button btnCheckUpdates;
        static Button btnOpenGitHub;
        static TextBlock txtSysSpecOs;
        static TextBlock txtSysSpecCpu;
        static TextBlock txtSysSpecRam;
        static TextBlock txtSysSpecGpu;

        static List<CategoryItem> categories = new List<CategoryItem>();
        static List<LargeFileInfo> allLargeFiles = new List<LargeFileInfo>();
        static List<DuplicateFileGroup> duplicateGroups = new List<DuplicateFileGroup>();
        static List<StartupAppItem> startupApps = new List<StartupAppItem>();

        static bool isScanning = false;
        static bool isCleaning = false;
        static string selectedDriveLetter = "C";
        static long currentMinLargeFileBytes = 100 * 1024 * 1024;

        // v1.0.4: cancellation, single instance, and clean audit trail
        static Button btnStopLargeFiles;
        static Button btnStopDuplicates;
        static CancellationTokenSource largeFilesCts;
        static CancellationTokenSource duplicatesCts;
        static Mutex singleInstanceMutex;
        static StreamWriter cleanManifest;
        static string lastCleanLogPath;
        static long cleanDeletedCount;
        static long cleanFailedCount;
        static double cleanFreedMB;

        [STAThread]
        static void Main(string[] args) {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, @"Local\OmniClean_SingleInstance", out createdNew);
            if (!createdNew) {
                if (args == null || args.Length == 0) {
                    MessageBox.Show("OmniClean is already running.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            if (args != null && args.Length > 0 && args[0].Equals("--silent-clean", StringComparison.OrdinalIgnoreCase)) {
                PerformSilentCleanup();
                return;
            }

            try {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application app = new Application();
                app.DispatcherUnhandledException += OnDispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

                string xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='OmniClean - Windows 11 Storage &amp; Performance Suite'
        Width='1220' Height='800'
        MinWidth='1060' MinHeight='720'
        WindowStartupLocation='CenterScreen'
        WindowStyle='None'
        AllowsTransparency='False'
        Background='#202020'
        Foreground='#FFFFFF'
        FontFamily='Segoe UI Variable Text, Segoe UI'
        UseLayoutRounding='True'
        TextOptions.TextFormattingMode='Display'>
    <Window.Resources>
        <!-- ============ Windows 11 Fluent Design (WinUI 3 Dark) Brushes ============ -->
        <SolidColorBrush x:Key='BrushWindowBg' Color='#202020'/>
        <SolidColorBrush x:Key='BrushTitleBar' Color='#202020'/>
        <SolidColorBrush x:Key='BrushSidebar' Color='#202020'/>
        <SolidColorBrush x:Key='BrushWorkspace' Color='#272727'/>
        <SolidColorBrush x:Key='BrushCard' Color='#2C2C2C'/>
        <SolidColorBrush x:Key='BrushInnerPanel' Color='#1F1F1F'/>
        <SolidColorBrush x:Key='BrushHealthCard' Color='#292929'/>
        <SolidColorBrush x:Key='BrushCardBorder' Color='#3A3A3A'/>
        <SolidColorBrush x:Key='BrushDivider' Color='#303030'/>

        <SolidColorBrush x:Key='BrushAccent' Color='#60CDFF'/>
        <SolidColorBrush x:Key='BrushAccentHover' Color='#4CC2FF'/>
        <SolidColorBrush x:Key='BrushTitleText' Color='#FFFFFF'/>
        <SolidColorBrush x:Key='BrushProgressMint' Color='#60CDFF'/>
        <SolidColorBrush x:Key='BrushProgressSky' Color='#60CDFF'/>
        <SolidColorBrush x:Key='BrushBannerUpdate' Color='#2C2C2C'/>

        <!-- Win11 Thin Scrollbar -->
        <ControlTemplate x:Key='VScrollBarTemplate' TargetType='ScrollBar'>
            <Grid Background='Transparent'>
                <Track Name='PART_Track' IsDirectionReversed='True'>
                    <Track.DecreaseRepeatButton>
                        <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False' IsTabStop='False'/>
                    </Track.DecreaseRepeatButton>
                    <Track.Thumb>
                        <Thumb Name='thumb' Margin='2,0'>
                            <Thumb.Template>
                                <ControlTemplate TargetType='Thumb'>
                                    <Border Background='#5A5A5A' CornerRadius='3' Width='4'/>
                                </ControlTemplate>
                            </Thumb.Template>
                        </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton>
                        <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False' IsTabStop='False'/>
                    </Track.IncreaseRepeatButton>
                </Track>
            </Grid>
        </ControlTemplate>
        <ControlTemplate x:Key='HScrollBarTemplate' TargetType='ScrollBar'>
            <Grid Background='Transparent'>
                <Track Name='PART_Track'>
                    <Track.DecreaseRepeatButton>
                        <RepeatButton Command='ScrollBar.PageLeftCommand' Opacity='0' Focusable='False' IsTabStop='False'/>
                    </Track.DecreaseRepeatButton>
                    <Track.Thumb>
                        <Thumb Name='thumb' Margin='0,2'>
                            <Thumb.Template>
                                <ControlTemplate TargetType='Thumb'>
                                    <Border Background='#5A5A5A' CornerRadius='3' Height='4'/>
                                </ControlTemplate>
                            </Thumb.Template>
                        </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton>
                        <RepeatButton Command='ScrollBar.PageRightCommand' Opacity='0' Focusable='False' IsTabStop='False'/>
                    </Track.IncreaseRepeatButton>
                </Track>
            </Grid>
        </ControlTemplate>
        <Style TargetType='ScrollBar'>
            <Setter Property='Width' Value='8'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Template' Value='{StaticResource VScrollBarTemplate}'/>
            <Style.Triggers>
                <Trigger Property='Orientation' Value='Horizontal'>
                    <Setter Property='Height' Value='8'/>
                    <Setter Property='Width' Value='Auto'/>
                    <Setter Property='Template' Value='{StaticResource HScrollBarTemplate}'/>
                </Trigger>
                <Trigger Property='IsEnabled' Value='False'>
                    <Setter Property='Visibility' Value='Hidden'/>
                </Trigger>
            </Style.Triggers>
        </Style>

        <Style TargetType='Border'>
            <Setter Property='CornerRadius' Value='8'/>
        </Style>

        <!-- Win11 Fluent CheckBox -->
        <Style TargetType='CheckBox'>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='FontSize' Value='13'/>
            <Setter Property='VerticalContentAlignment' Value='Center'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='CheckBox'>
                        <StackPanel Orientation='Horizontal' Background='Transparent'>
                            <Border Name='box' Width='18' Height='18' CornerRadius='4' BorderBrush='#8A8A8A' BorderThickness='1' Background='#1F1F1F' VerticalAlignment='Center'>
                                <Path Name='glyph' Data='M 3.5 9 L 7 12.5 L 14 4.5' Stroke='#002030' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Visibility='Collapsed'/>
                            </Border>
                            <ContentPresenter Margin='8,0,0,0' VerticalAlignment='Center'/>
                        </StackPanel>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='box' Property='Background' Value='#2B2B2B'/>
                                <Setter TargetName='box' Property='BorderBrush' Value='#C5C5C5'/>
                            </Trigger>
                            <Trigger Property='IsChecked' Value='True'>
                                <Setter TargetName='box' Property='Background' Value='{StaticResource BrushAccent}'/>
                                <Setter TargetName='box' Property='BorderBrush' Value='{StaticResource BrushAccent}'/>
                                <Setter TargetName='glyph' Property='Visibility' Value='Visible'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Win11 Dark ComboBox -->
        <Style TargetType='ComboBoxItem'>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='FontWeight' Value='Normal'/>
            <Setter Property='Padding' Value='10,6'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='ComboBoxItem'>
                        <Border Name='itemBdr' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' Margin='2' CornerRadius='4'>
                            <ContentPresenter VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='itemBdr' Property='Background' Value='#383838'/>
                            </Trigger>
                            <Trigger Property='IsSelected' Value='True'>
                                <Setter TargetName='itemBdr' Property='Background' Value='#404040'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <ControlTemplate x:Key='DarkComboBoxTemplate' TargetType='ComboBox'>
            <Grid Name='MainGrid'>
                <ToggleButton Name='ToggleButton'
                              Focusable='False'
                              IsChecked='{Binding Path=IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'
                              ClickMode='Press'>
                    <ToggleButton.Template>
                        <ControlTemplate TargetType='ToggleButton'>
                            <Border Name='Border' Background='#323232' BorderBrush='#454545' BorderThickness='1' CornerRadius='4' Padding='6,3'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='*'/>
                                        <ColumnDefinition Width='Auto'/>
                                    </Grid.ColumnDefinitions>
                                    <Path Name='Arrow' Grid.Column='1' Margin='4,0,2,0' HorizontalAlignment='Center' VerticalAlignment='Center'
                                          Data='M 0 0 L 4 4 L 8 0' Stroke='#C5C5C5' StrokeThickness='1.3'/>
                                </Grid>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'>
                                    <Setter TargetName='Border' Property='Background' Value='#3B3B3B'/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter Name='ContentSite'
                                  IsHitTestVisible='False'
                                  Content='{TemplateBinding SelectionBoxItem}'
                                  ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                                  ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
                                  Margin='8,0,20,0'
                                  VerticalAlignment='Center'
                                  HorizontalAlignment='Left'/>
                <Popup Name='Popup'
                       Placement='Bottom'
                       IsOpen='{TemplateBinding IsDropDownOpen}'
                       AllowsTransparency='True'
                       Focusable='False'
                       PopupAnimation='Slide'>
                    <Grid Name='DropDown'
                          SnapsToDevicePixels='True'
                          MinWidth='{TemplateBinding ActualWidth}'
                          MaxHeight='{TemplateBinding MaxDropDownHeight}'
                          Margin='0,4,0,0'>
                        <Border Name='DropDownBorder' Background='#2C2C2C' BorderBrush='#454545' BorderThickness='1' CornerRadius='6' Padding='4'>
                            <ScrollViewer SnapsToDevicePixels='True'>
                                <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
                            </ScrollViewer>
                        </Border>
                    </Grid>
                </Popup>
            </Grid>
        </ControlTemplate>

        <Style TargetType='ComboBox'>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='FontWeight' Value='Normal'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template' Value='{StaticResource DarkComboBoxTemplate}'/>
        </Style>

        <!-- Win11 NavigationView Item Style -->
        <Style x:Key='SidebarNavBtn' TargetType='Button'>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#C5C5C5'/>
            <Setter Property='FontSize' Value='13'/>
            <Setter Property='FontWeight' Value='Normal'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Padding' Value='10,9'/>
            <Setter Property='Margin' Value='0,0,0,3'/>
            <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Name='bdr' Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
                            <ContentPresenter VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#2D2D2D'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Win11 Primary Accent Button -->
        <Style x:Key='PrimaryBtn' TargetType='Button'>
            <Setter Property='Background' Value='{StaticResource BrushAccent}'/>
            <Setter Property='Foreground' Value='#002030'/>
            <Setter Property='FontSize' Value='13'/>
            <Setter Property='FontWeight' Value='SemiBold'/>
            <Setter Property='BorderThickness' Value='1'/>
            <Setter Property='BorderBrush' Value='#75D5FF'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Padding' Value='16,8'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Name='bdr' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4' Padding='{TemplateBinding Padding}'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='{StaticResource BrushAccentHover}'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#38B0E8'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter TargetName='bdr' Property='Background' Value='#333333'/>
                                <Setter TargetName='bdr' Property='BorderBrush' Value='#3A3A3A'/>
                                <Setter Property='Foreground' Value='#777777'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Win11 Standard Secondary Button -->
        <Style x:Key='SecondaryBtn' TargetType='Button'>
            <Setter Property='Background' Value='#323232'/>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='FontWeight' Value='Normal'/>
            <Setter Property='BorderThickness' Value='1'/>
            <Setter Property='BorderBrush' Value='#424242'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Padding' Value='12,6'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Name='bdr' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4' Padding='{TemplateBinding Padding}'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#3B3B3B'/>
                                <Setter TargetName='bdr' Property='BorderBrush' Value='#505050'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#292929'/>
                                <Setter Property='Foreground' Value='#C5C5C5'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Native Win11 Caption Buttons (46x32) -->
        <Style x:Key='WinControlBtn' TargetType='Button'>
            <Setter Property='Width' Value='46'/>
            <Setter Property='Height' Value='32'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#C5C5C5'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Cursor' Value='Arrow'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Name='bdr' Background='{TemplateBinding Background}' CornerRadius='0'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#2E2E2E'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Native Win11 Close Button (#C42B1C on Hover) -->
        <Style x:Key='WinCloseBtn' TargetType='Button'>
            <Setter Property='Width' Value='46'/>
            <Setter Property='Height' Value='32'/>
            <Setter Property='Background' Value='Transparent'/>
            <Setter Property='Foreground' Value='#C5C5C5'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Cursor' Value='Arrow'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Name='bdr' Background='{TemplateBinding Background}' CornerRadius='0'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bdr' Property='Background' Value='#C42B1C'/>
                                <Setter Property='Foreground' Value='#FFFFFF'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>

    <!-- Outer Win11 Mica Window Frame -->
    <Border Background='{StaticResource BrushWindowBg}' BorderBrush='#333333' BorderThickness='1' CornerRadius='8'>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height='40'/>
                <RowDefinition Height='Auto'/>
                <RowDefinition Height='*'/>
            </Grid.RowDefinitions>

            <!-- Top Win11 Titlebar -->
            <Border Grid.Row='0' Name='titleBar' Background='{StaticResource BrushTitleBar}' Padding='14,0,0,0'>
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='Auto'/>
                        <ColumnDefinition Width='*'/>
                        <ColumnDefinition Width='Auto'/>
                    </Grid.ColumnDefinitions>

                    <!-- App Icon & Title -->
                    <StackPanel Grid.Column='0' Orientation='Horizontal' VerticalAlignment='Center'>
                        <Border Width='22' Height='22' Background='#0078D4' CornerRadius='5' Margin='0,0,10,0'>
                            <TextBlock Text='⚡' FontSize='12' Foreground='White' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <TextBlock Text='OmniClean' FontSize='12' FontWeight='SemiBold' Foreground='#FFFFFF' VerticalAlignment='Center'/>
                        <Border Background='#2D2D2D' BorderBrush='#3D3D3D' BorderThickness='1' CornerRadius='4' Padding='6,1' Margin='8,0,0,0' VerticalAlignment='Center'>
                            <TextBlock Text='v1.0.5' FontSize='10' Foreground='#60CDFF'/>
                        </Border>
                    </StackPanel>

                    <!-- Center Uptime Pill -->
                    <StackPanel Grid.Column='1' Orientation='Horizontal' HorizontalAlignment='Center' VerticalAlignment='Center'>
                        <Border Background='#292929' BorderBrush='#363636' BorderThickness='1' CornerRadius='4' Padding='10,3'>
                            <StackPanel Orientation='Horizontal'>
                                <Ellipse Width='6' Height='6' Fill='#6CCB5F' Margin='0,0,7,0' VerticalAlignment='Center'/>
                                <TextBlock Name='txtHeaderUptimePill' Text='System Online' FontSize='11' Foreground='#C5C5C5' VerticalAlignment='Center'/>
                            </StackPanel>
                        </Border>
                    </StackPanel>

                    <!-- Native Win11 Caption Buttons -->
                    <StackPanel Grid.Column='2' Orientation='Horizontal' VerticalAlignment='Top'>
                        <Button Name='btnMinimize' Style='{StaticResource WinControlBtn}' Content='&#xE921;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10'/>
                        <Button Name='btnMaximize' Style='{StaticResource WinControlBtn}' Content='&#xE922;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10'/>
                        <Button Name='btnClose' Style='{StaticResource WinCloseBtn}' Content='&#xE8BB;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10'/>
                    </StackPanel>
                </Grid>
            </Border>

            <!-- Win11 InfoBar Update Banner -->
            <Border Grid.Row='1' Name='bannerUpdate' Background='{StaticResource BrushBannerUpdate}' BorderBrush='#3A3A3A' BorderThickness='0,1,0,1' Padding='16,8' Visibility='Collapsed' CornerRadius='0'>
                <Grid>
                    <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                        <Ellipse Width='8' Height='8' Fill='#60CDFF' Margin='0,0,10,0' VerticalAlignment='Center'/>
                        <TextBlock Name='txtUpdateNotice' Text='A new release of OmniClean is available!' FontSize='12' FontWeight='Normal' Foreground='#FFFFFF' VerticalAlignment='Center'/>
                    </StackPanel>
                    <Button Name='btnDownloadUpdate' Content='View Release on GitHub' HorizontalAlignment='Right' Style='{StaticResource PrimaryBtn}' Padding='12,4' FontSize='11'/>
                </Grid>
            </Border>

            <!-- Main App Body: Left NavigationView + Right Layered Workspace -->
            <Grid Grid.Row='2'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='240'/>
                    <ColumnDefinition Width='*'/>
                </Grid.ColumnDefinitions>

                <!-- LEFT NAVIGATION PANE -->
                <Border Grid.Column='0' Background='{StaticResource BrushSidebar}' Padding='10,8,10,12'>
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height='*'/>
                            <RowDefinition Height='Auto'/>
                        </Grid.RowDefinitions>

                        <!-- Navigation Items -->
                        <StackPanel Grid.Row='0'>
                            <!-- Nav 0: Storage Cleaner -->
                            <Button Name='btnNavCleaner' Style='{StaticResource SidebarNavBtn}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='Auto'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Name='barNavCleaner' Grid.Column='0' Width='3' Height='16' Background='#60CDFF' CornerRadius='1.5' Margin='0,0,10,0' Visibility='Visible'/>
                                    <TextBlock Grid.Column='1' Text='🧹  Storage Cleaner' VerticalAlignment='Center'/>
                                </Grid>
                            </Button>

                            <!-- Nav 1: Large Files -->
                            <Button Name='btnNavLargeFiles' Style='{StaticResource SidebarNavBtn}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='Auto'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Name='barNavLargeFiles' Grid.Column='0' Width='3' Height='16' Background='#60CDFF' CornerRadius='1.5' Margin='0,0,10,0' Visibility='Collapsed'/>
                                    <TextBlock Grid.Column='1' Text='📦  Large Files' VerticalAlignment='Center'/>
                                </Grid>
                            </Button>

                            <!-- Nav 2: Duplicate Finder -->
                            <Button Name='btnNavDuplicates' Style='{StaticResource SidebarNavBtn}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='Auto'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Name='barNavDuplicates' Grid.Column='0' Width='3' Height='16' Background='#60CDFF' CornerRadius='1.5' Margin='0,0,10,0' Visibility='Collapsed'/>
                                    <TextBlock Grid.Column='1' Text='👥  Duplicate Finder' VerticalAlignment='Center'/>
                                </Grid>
                            </Button>

                            <!-- Nav 3: Startup Optimizer -->
                            <Button Name='btnNavStartup' Style='{StaticResource SidebarNavBtn}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='Auto'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Name='barNavStartup' Grid.Column='0' Width='3' Height='16' Background='#60CDFF' CornerRadius='1.5' Margin='0,0,10,0' Visibility='Collapsed'/>
                                    <TextBlock Grid.Column='1' Text='🚀  Startup Apps' VerticalAlignment='Center'/>
                                </Grid>
                            </Button>

                            <!-- Nav 4: Automation & Settings -->
                            <Button Name='btnNavSettings' Style='{StaticResource SidebarNavBtn}'>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='Auto'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>
                                    <Border Name='barNavSettings' Grid.Column='0' Width='3' Height='16' Background='#60CDFF' CornerRadius='1.5' Margin='0,0,10,0' Visibility='Collapsed'/>
                                    <TextBlock Grid.Column='1' Text='⚙️  Settings &amp; About' VerticalAlignment='Center'/>
                                </Grid>
                            </Button>
                        </StackPanel>

                        <!-- Bottom Sidebar: System Telemetry Card -->
                        <Border Grid.Row='1' Background='{StaticResource BrushHealthCard}' BorderBrush='#383838' BorderThickness='1' CornerRadius='8' Padding='12,10'>
                            <StackPanel>
                                <TextBlock Text='System Memory' FontSize='11' FontWeight='SemiBold' Foreground='#FFFFFF' Margin='0,0,0,4'/>
                                <TextBlock Name='txtSidebarCpu' Text='CPU: Detecting...' FontSize='11' Foreground='#C5C5C5' TextTrimming='CharacterEllipsis' Margin='0,0,0,4'/>
                                
                                <Grid Margin='0,0,0,5'>
                                    <TextBlock Name='txtSidebarRam' Text='RAM: -- / -- GB' FontSize='11' Foreground='#8A8A8A'/>
                                </Grid>
                                <ProgressBar Name='barSidebarRam' Height='4' Value='40' Maximum='100' Foreground='{StaticResource BrushProgressSky}' Background='#1A1A1A' BorderThickness='0' Margin='0,0,0,10'/>

                                <Button Name='btnQuickFlushRam' Content='Flush Standby RAM' Style='{StaticResource SecondaryBtn}' Padding='8,5' FontSize='11' HorizontalAlignment='Stretch'/>
                                
                                <TextBlock Name='txtSidebarUptime' Text='Uptime: --' FontSize='10' Foreground='#8A8A8A' Margin='0,8,0,0' HorizontalAlignment='Center'/>
                            </StackPanel>
                        </Border>
                    </Grid>
                </Border>

                <!-- RIGHT WORKSPACE SURFACE (Win11 Layered Page Container) -->
                <Border Grid.Column='1' Background='{StaticResource BrushWorkspace}' BorderBrush='#333333' BorderThickness='1,1,0,0' CornerRadius='8,0,0,0' Padding='20,16'>
                    <TabControl Name='mainTabControl' Background='Transparent' BorderThickness='0'>
                        <TabControl.ItemContainerStyle>
                            <Style TargetType='TabItem'>
                                <Setter Property='Visibility' Value='Collapsed'/>
                            </Style>
                        </TabControl.ItemContainerStyle>

                        <!-- ================= TAB 0: STORAGE CLEANER ================= -->
                        <TabItem>
                            <Grid>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height='Auto'/>
                                    <RowDefinition Height='*'/>
                                </Grid.RowDefinitions>

                                <!-- Page Header -->
                                <Grid Grid.Row='0' Margin='0,0,0,16'>
                                    <StackPanel VerticalAlignment='Center'>
                                        <TextBlock Text='Storage Cleaner' FontSize='22' FontWeight='SemiBold' Foreground='{StaticResource BrushTitleText}'/>
                                        <TextBlock Text='Inspect drives and safely purge temporary caches' FontSize='12' Foreground='#C5C5C5' Margin='0,2,0,0'/>
                                    </StackPanel>
                                    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
                                        <Border Background='#2C2C2C' BorderBrush='#3A3A3A' BorderThickness='1' CornerRadius='4' Padding='8,3' Margin='0,0,8,0'>
                                            <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                                                <TextBlock Text='Drive' FontSize='12' Foreground='#C5C5C5' VerticalAlignment='Center' Margin='0,0,8,0'/>
                                                <ComboBox Name='cmbDrive' Width='110' Height='24'/>
                                            </StackPanel>
                                        </Border>
                                        <Button Name='btnRescan' Content='Rescan' Style='{StaticResource SecondaryBtn}' Margin='0,0,6,0'/>
                                        <Button Name='btnOptimizeRam' Content='Flush RAM' Style='{StaticResource SecondaryBtn}'/>
                                    </StackPanel>
                                </Grid>

                                <!-- Two-Column Content -->
                                <Grid Grid.Row='1'>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width='330'/>
                                        <ColumnDefinition Width='*'/>
                                    </Grid.ColumnDefinitions>

                                    <!-- Left Column: Drive Capacity + Activity Console -->
                                    <Grid Grid.Column='0' Margin='0,0,14,0'>
                                        <Grid.RowDefinitions>
                                            <RowDefinition Height='Auto'/>
                                            <RowDefinition Height='*'/>
                                        </Grid.RowDefinitions>

                                        <!-- Win11 Storage Sense Style Drive Card -->
                                        <Border Grid.Row='0' Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='16' Margin='0,0,0,12'>
                                            <StackPanel>
                                                <TextBlock Text='Local Disk Capacity' FontSize='12' FontWeight='SemiBold' Foreground='#C5C5C5' Margin='0,0,0,6'/>
                                                <TextBlock Name='txtDiskPercent' Text='--% Free' FontSize='28' FontWeight='SemiBold' Foreground='#FFFFFF' Margin='0,0,0,2'/>
                                                <TextBlock Name='txtDiskRatio' Text='-- GB Free / -- GB' FontSize='12' Foreground='#C5C5C5' Margin='0,0,0,10'/>
                                                <ProgressBar Name='diskProgress' Height='8' Value='30' Maximum='100' Foreground='{StaticResource BrushProgressMint}' Background='#1A1A1A' BorderThickness='0' Margin='0,0,0,14'/>

                                                <Grid>
                                                    <Grid.ColumnDefinitions>
                                                        <ColumnDefinition Width='*'/>
                                                        <ColumnDefinition Width='*'/>
                                                        <ColumnDefinition Width='*'/>
                                                    </Grid.ColumnDefinitions>
                                                    <Border Grid.Column='0' Background='#222222' BorderBrush='#333333' BorderThickness='1' Padding='8,8' Margin='0,0,4,0' CornerRadius='4'>
                                                        <StackPanel>
                                                            <TextBlock Text='Free' FontSize='10' Foreground='#8A8A8A'/>
                                                            <TextBlock Name='txtStatFree' Text='-- GB' FontSize='12' FontWeight='SemiBold' Foreground='#6CCB5F'/>
                                                        </StackPanel>
                                                    </Border>
                                                    <Border Grid.Column='1' Background='#222222' BorderBrush='#333333' BorderThickness='1' Padding='8,8' Margin='2,0,2,0' CornerRadius='4'>
                                                        <StackPanel>
                                                            <TextBlock Text='Used' FontSize='10' Foreground='#8A8A8A'/>
                                                            <TextBlock Name='txtStatUsed' Text='-- GB' FontSize='12' FontWeight='SemiBold' Foreground='#60CDFF'/>
                                                        </StackPanel>
                                                    </Border>
                                                    <Border Grid.Column='2' Background='#222222' BorderBrush='#333333' BorderThickness='1' Padding='8,8' Margin='4,0,0,0' CornerRadius='4'>
                                                        <StackPanel>
                                                            <TextBlock Text='Total' FontSize='10' Foreground='#8A8A8A'/>
                                                            <TextBlock Name='txtStatTotal' Text='-- GB' FontSize='12' FontWeight='SemiBold' Foreground='#FFFFFF'/>
                                                        </StackPanel>
                                                    </Border>
                                                </Grid>
                                            </StackPanel>
                                        </Border>

                                        <!-- Live Diagnostic Activity Console -->
                                        <Border Grid.Row='1' Background='{StaticResource BrushInnerPanel}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='12'>
                                            <Grid>
                                                <Grid.RowDefinitions>
                                                    <RowDefinition Height='Auto'/>
                                                    <RowDefinition Height='*'/>
                                                </Grid.RowDefinitions>
                                                <Grid Grid.Row='0' Margin='0,0,0,8'>
                                                    <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                                                        <Ellipse Width='6' Height='6' Fill='#60CDFF' Margin='0,0,6,0'/>
                                                        <TextBlock Text='Activity Console' FontSize='12' FontWeight='SemiBold' Foreground='#C5C5C5'/>
                                                    </StackPanel>
                                                    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
                                                        <Button Name='btnClearLog' Content='Clear' Background='Transparent' Foreground='#8A8A8A' BorderThickness='0' FontSize='11' Margin='0,0,10,0' Cursor='Hand'/>
                                                        <Button Name='btnExportLog' Content='Export' Background='Transparent' Foreground='#60CDFF' BorderThickness='0' FontSize='11' Cursor='Hand'/>
                                                    </StackPanel>
                                                </Grid>
                                                <TextBox Grid.Row='1' Name='txtConsole' IsReadOnly='True' Background='Transparent' Foreground='#D4D4D4' BorderThickness='0' FontFamily='Cascadia Code, Consolas' FontSize='11' VerticalScrollBarVisibility='Auto' TextWrapping='Wrap'/>
                                            </Grid>
                                        </Border>
                                    </Grid>

                                    <!-- Right Column: Cleanable Targets & Bottom Action Dock -->
                                    <Border Grid.Column='1' Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='16'>
                                        <Grid>
                                            <Grid.RowDefinitions>
                                                <RowDefinition Height='Auto'/>
                                                <RowDefinition Height='Auto'/>
                                                <RowDefinition Height='*'/>
                                                <RowDefinition Height='Auto'/>
                                            </Grid.RowDefinitions>

                                            <!-- Categories Filter Header -->
                                            <Grid Grid.Row='0' Margin='0,0,0,12'>
                                                <StackPanel>
                                                    <TextBlock Text='Temporary Files &amp; Caches' FontSize='15' FontWeight='SemiBold' Foreground='White'/>
                                                    <TextBlock Text='Personal files and browser passwords are never touched' FontSize='11' Foreground='#8A8A8A'/>
                                                </StackPanel>
                                                <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
                                                    <Button Name='btnSelectAll' Content='Select All' Style='{StaticResource SecondaryBtn}' Padding='9,4' FontSize='11' Margin='0,0,5,0'/>
                                                    <Button Name='btnSelectSafe' Content='Recommended' Style='{StaticResource SecondaryBtn}' Padding='9,4' FontSize='11' Margin='0,0,5,0'/>
                                                    <Button Name='btnClearAll' Content='Clear' Style='{StaticResource SecondaryBtn}' Padding='9,4' FontSize='11'/>
                                                </StackPanel>
                                            </Grid>

                                            <!-- In-App Success Banner -->
                                            <Border Grid.Row='1' Name='bannerSuccess' Background='#1E2E22' BorderBrush='#6CCB5F' BorderThickness='1' Padding='12,8' Margin='0,0,0,10' CornerRadius='4' Visibility='Collapsed'>
                                                <TextBlock Name='txtSuccessMsg' Text='Cleanup completed successfully.' FontSize='12' FontWeight='Normal' Foreground='#6CCB5F'/>
                                            </Border>

                                            <!-- Scrollable Category Cards -->
                                            <ScrollViewer Grid.Row='2' VerticalScrollBarVisibility='Auto' Margin='0,0,0,12'>
                                                <StackPanel Name='pnlCategories'/>
                                            </ScrollViewer>

                                            <!-- Bottom Action Dock -->
                                            <Border Grid.Row='3' Background='#222222' BorderBrush='#383838' BorderThickness='1' Padding='14,10' CornerRadius='6'>
                                                <Grid>
                                                    <StackPanel VerticalAlignment='Center'>
                                                        <TextBlock Text='Selected to Reclaim' FontSize='11' Foreground='#8A8A8A'/>
                                                        <TextBlock Name='txtSelectedSize' Text='0.00 MB' FontSize='20' FontWeight='SemiBold' Foreground='#FFFFFF'/>
                                                    </StackPanel>
                                                    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
                                                        <CheckBox Name='chkDryRun' Content='Preview only (Dry Run)' FontSize='12' Margin='0,0,16,0' VerticalAlignment='Center'/>
                                                        <Button Name='btnClean' Content='Clean Selected Storage' Style='{StaticResource PrimaryBtn}' Padding='20,9' FontSize='13'/>
                                                    </StackPanel>
                                                </Grid>
                                            </Border>
                                        </Grid>
                                    </Border>
                                </Grid>
                            </Grid>
                        </TabItem>

                        <!-- ================= TAB 1: LARGE FILES FINDER ================= -->
                        <TabItem>
                            <Border Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='18'>
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height='Auto'/>
                                        <RowDefinition Height='Auto'/>
                                        <RowDefinition Height='*'/>
                                    </Grid.RowDefinitions>

                                    <Grid Grid.Row='0' Margin='0,0,0,14'>
                                        <StackPanel>
                                            <TextBlock Text='Large Files Profiler' FontSize='20' FontWeight='SemiBold' Foreground='{StaticResource BrushTitleText}'/>
                                            <TextBlock Name='txtLargeFilesSummary' Text='Discover large files (&gt;100 MB) consuming your storage space.' FontSize='12' Foreground='#C5C5C5'/>
                                        </StackPanel>
                                        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
                                            <Button Name='btnStopLargeFiles' Content='Stop' Style='{StaticResource SecondaryBtn}' Padding='14,8' Margin='0,0,8,0' Visibility='Collapsed'/>
                                            <Button Name='btnScanLargeFiles' Content='Scan for Large Files' Style='{StaticResource PrimaryBtn}' Padding='16,8'/>
                                        </StackPanel>
                                    </Grid>

                                    <StackPanel Grid.Row='1' Orientation='Horizontal' Margin='0,0,0,12'>
                                        <TextBlock Text='Minimum Size:' FontSize='11' Foreground='#8A8A8A' VerticalAlignment='Center' Margin='0,0,8,0'/>
                                        <Button Name='btnFilterAll100' Content='All (&gt;100 MB)' Style='{StaticResource SecondaryBtn}' Padding='10,4' FontSize='11' Margin='0,0,6,0'/>
                                        <Button Name='btnFilter500MB' Content='&gt;500 MB' Style='{StaticResource SecondaryBtn}' Padding='10,4' FontSize='11' Margin='0,0,6,0'/>
                                        <Button Name='btnFilter1GB' Content='&gt;1 GB' Style='{StaticResource SecondaryBtn}' Padding='10,4' FontSize='11'/>
                                    </StackPanel>

                                    <ScrollViewer Grid.Row='2' VerticalScrollBarVisibility='Auto'>
                                        <StackPanel Name='pnlLargeFiles'/>
                                    </ScrollViewer>
                                </Grid>
                            </Border>
                        </TabItem>

                        <!-- ================= TAB 2: DUPLICATE FINDER ================= -->
                        <TabItem>
                            <Border Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='18'>
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height='Auto'/>
                                        <RowDefinition Height='*'/>
                                    </Grid.RowDefinitions>

                                    <Grid Grid.Row='0' Margin='0,0,0,14'>
                                        <StackPanel>
                                            <TextBlock Text='Duplicate Finder' FontSize='20' FontWeight='SemiBold' Foreground='{StaticResource BrushTitleText}'/>
                                            <TextBlock Name='txtDuplicatesSummary' Text='Scans user folders for 100% byte-matched duplicate files using SHA-256 hashing.' FontSize='12' Foreground='#C5C5C5'/>
                                        </StackPanel>
                                        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right'>
                                            <Button Name='btnCleanAllDuplicates' Content='Delete All Duplicate Copies' Style='{StaticResource SecondaryBtn}' Margin='0,0,8,0' Padding='14,8'/>
                                            <Button Name='btnStopDuplicates' Content='Stop' Style='{StaticResource SecondaryBtn}' Padding='14,8' Margin='0,0,8,0' Visibility='Collapsed'/>
                                            <Button Name='btnScanDuplicates' Content='Find Duplicates' Style='{StaticResource PrimaryBtn}' Padding='16,8'/>
                                        </StackPanel>
                                    </Grid>

                                    <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto'>
                                        <StackPanel Name='pnlDuplicates'/>
                                    </ScrollViewer>
                                </Grid>
                            </Border>
                        </TabItem>

                        <!-- ================= TAB 3: STARTUP OPTIMIZER ================= -->
                        <TabItem>
                            <Border Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='18'>
                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height='Auto'/>
                                        <RowDefinition Height='*'/>
                                    </Grid.RowDefinitions>

                                    <Grid Grid.Row='0' Margin='0,0,0,14'>
                                        <StackPanel>
                                            <TextBlock Text='Startup Apps' FontSize='20' FontWeight='SemiBold' Foreground='{StaticResource BrushTitleText}'/>
                                            <TextBlock Name='txtStartupSummary' Text='Configure apps that start automatically when you sign in to Windows.' FontSize='12' Foreground='#C5C5C5'/>
                                        </StackPanel>
                                        <Button Name='btnScanStartup' Content='Refresh List' Style='{StaticResource SecondaryBtn}' HorizontalAlignment='Right' Padding='14,8'/>
                                    </Grid>

                                    <ScrollViewer Grid.Row='1' VerticalScrollBarVisibility='Auto'>
                                        <StackPanel Name='pnlStartupApps'/>
                                    </ScrollViewer>
                                </Grid>
                            </Border>
                        </TabItem>

                        <!-- ================= TAB 4: AUTOMATION & ABOUT ================= -->
                        <TabItem>
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width='*'/>
                                    <ColumnDefinition Width='*'/>
                                </Grid.ColumnDefinitions>

                                <!-- Left Card: Maintenance Automation -->
                                <Border Grid.Column='0' Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='20' Margin='0,0,8,0'>
                                    <StackPanel>
                                        <TextBlock Text='Automated Maintenance' FontSize='17' FontWeight='SemiBold' Foreground='White' Margin='0,0,0,6'/>
                                        <TextBlock Text='Schedule silent background cleaning via Windows Task Scheduler. Cleans Recycle Bin, temp files, and caches automatically.' FontSize='12' Foreground='#C5C5C5' TextWrapping='Wrap' Margin='0,0,0,16'/>

                                        <Border Background='{StaticResource BrushInnerPanel}' BorderBrush='#383838' BorderThickness='1' CornerRadius='6' Padding='14' Margin='0,0,0,16'>
                                            <StackPanel>
                                                <CheckBox Name='chkAutoClean' Content='Run weekly auto-clean (Sundays at 12:00 PM)' Margin='0,0,0,8'/>
                                                <TextBlock Name='txtScheduleStatus' Text='Status: Checking schedule...' FontSize='11' Foreground='#8A8A8A'/>
                                            </StackPanel>
                                        </Border>

                                        <TextBlock Text='Quick Actions' FontSize='13' FontWeight='SemiBold' Foreground='#FFFFFF' Margin='0,0,0,10'/>
                                        <Button Name='btnCreateShortcut' Content='Create Desktop Shortcut' Style='{StaticResource SecondaryBtn}' Padding='14,8' Margin='0,0,0,8' HorizontalAlignment='Stretch'/>
                                        <Button Name='btnTestSilentClean' Content='Run Silent Clean Now (Test Run)' Style='{StaticResource SecondaryBtn}' Padding='14,8' HorizontalAlignment='Stretch'/>
                                    </StackPanel>
                                </Border>

                                <!-- Right Card: About & Hardware Telemetry -->
                                <Border Grid.Column='1' Background='{StaticResource BrushCard}' BorderBrush='{StaticResource BrushCardBorder}' BorderThickness='1' Padding='20' Margin='8,0,0,0'>
                                    <StackPanel>
                                        <TextBlock Text='System Specifications' FontSize='17' FontWeight='SemiBold' Foreground='White' Margin='0,0,0,6'/>
                                        <TextBlock Text='OmniClean v1.0.5 Standalone Edition' FontSize='12' Foreground='#C5C5C5' Margin='0,0,0,14'/>

                                        <Border Background='{StaticResource BrushInnerPanel}' BorderBrush='#383838' BorderThickness='1' CornerRadius='6' Padding='14' Margin='0,0,0,16'>
                                            <StackPanel>
                                                <TextBlock Name='txtSysSpecOs' Text='OS: Windows' FontSize='12' Foreground='#FFFFFF' Margin='0,0,0,4'/>
                                                <TextBlock Name='txtSysSpecCpu' Text='CPU: Detecting...' FontSize='12' Foreground='#C5C5C5' Margin='0,0,0,4'/>
                                                <TextBlock Name='txtSysSpecRam' Text='RAM: Detecting...' FontSize='12' Foreground='#C5C5C5' Margin='0,0,0,4'/>
                                                <TextBlock Name='txtSysSpecGpu' Text='GPU: Detecting...' FontSize='12' Foreground='#C5C5C5'/>
                                            </StackPanel>
                                        </Border>

                                        <TextBlock Text='Safety Guarantees' FontSize='13' FontWeight='SemiBold' Foreground='#FFFFFF' Margin='0,0,0,8'/>
                                        <TextBlock Text='• Never touches browser passwords, cookies, or active sessions.' FontSize='11' Foreground='#6CCB5F' Margin='0,0,0,4'/>
                                        <TextBlock Text='• Leaves personal documents and project files untouched.' FontSize='11' Foreground='#6CCB5F' Margin='0,0,0,4'/>
                                        <TextBlock Text='• Zero background resident processes when closed.' FontSize='11' Foreground='#6CCB5F' Margin='0,0,0,16'/>

                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width='*'/>
                                                <ColumnDefinition Width='*'/>
                                            </Grid.ColumnDefinitions>
                                            <Button Name='btnCheckUpdates' Grid.Column='0' Content='Check for Updates' Style='{StaticResource SecondaryBtn}' Padding='10,8' Margin='0,0,4,0'/>
                                            <Button Name='btnOpenGitHub' Grid.Column='1' Content='GitHub Repository' Style='{StaticResource SecondaryBtn}' Padding='10,8' Margin='4,0,0,0'/>
                                        </Grid>
                                    </StackPanel>
                                </Border>
                            </Grid>
                        </TabItem>
                    </TabControl>
                </Border>
            </Grid>
        </Grid>
    </Border>
</Window>";

                if (args != null && Array.IndexOf(args, "--validate-xaml") >= 0) {
                    try {
                        XamlReader.Parse(xaml);
                        File.WriteAllText("xaml-validation.log", "OK: embedded XAML parsed successfully.\r\n");
                        Environment.Exit(0);
                    } catch (Exception vx) {
                        File.WriteAllText("xaml-validation.log", "XAML VALIDATION FAILED:\r\n" + vx.ToString());
                        Environment.Exit(1);
                    }
                }

                mainWindow = (Window)XamlReader.Parse(xaml);

                // Native Windows 11 DWM Chrome: Rounded corners, Dark Mode, and Mica System Backdrop
                IntPtr hwnd = new WindowInteropHelper(mainWindow).EnsureHandle();
                int cornerPref = DWM_WINDOW_CORNER_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                int backdropType = DWMSBT_MAINWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                // Titlebar and Window Controls
                titleBar = (Border)mainWindow.FindName("titleBar");
                btnMinimize = (Button)mainWindow.FindName("btnMinimize");
                btnMaximize = (Button)mainWindow.FindName("btnMaximize");
                btnClose = (Button)mainWindow.FindName("btnClose");
                txtHeaderUptimePill = (TextBlock)mainWindow.FindName("txtHeaderUptimePill");

                titleBar.MouseLeftButtonDown += (s, e) => {
                    if (e.ClickCount == 2) {
                        mainWindow.WindowState = (mainWindow.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                    } else if (e.ButtonState == MouseButtonState.Pressed) {
                        mainWindow.DragMove();
                    }
                };
                btnMinimize.Click += (s, e) => mainWindow.WindowState = WindowState.Minimized;
                btnMaximize.Click += (s, e) => mainWindow.WindowState = (mainWindow.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                btnClose.Click += (s, e) => mainWindow.Close();

                // Navigation Controls
                btnNavCleaner = (Button)mainWindow.FindName("btnNavCleaner");
                btnNavLargeFiles = (Button)mainWindow.FindName("btnNavLargeFiles");
                btnNavDuplicates = (Button)mainWindow.FindName("btnNavDuplicates");
                btnNavStartup = (Button)mainWindow.FindName("btnNavStartup");
                btnNavSettings = (Button)mainWindow.FindName("btnNavSettings");

                barNavCleaner = (Border)mainWindow.FindName("barNavCleaner");
                barNavLargeFiles = (Border)mainWindow.FindName("barNavLargeFiles");
                barNavDuplicates = (Border)mainWindow.FindName("barNavDuplicates");
                barNavStartup = (Border)mainWindow.FindName("barNavStartup");
                barNavSettings = (Border)mainWindow.FindName("barNavSettings");

                mainTabControl = (TabControl)mainWindow.FindName("mainTabControl");

                btnNavCleaner.Click += (s, e) => SwitchTab(0);
                btnNavLargeFiles.Click += (s, e) => SwitchTab(1);
                btnNavDuplicates.Click += (s, e) => SwitchTab(2);
                btnNavStartup.Click += (s, e) => SwitchTab(3);
                btnNavSettings.Click += (s, e) => SwitchTab(4);

                // Sidebar Telemetry
                txtSidebarCpu = (TextBlock)mainWindow.FindName("txtSidebarCpu");
                txtSidebarRam = (TextBlock)mainWindow.FindName("txtSidebarRam");
                barSidebarRam = (ProgressBar)mainWindow.FindName("barSidebarRam");
                btnQuickFlushRam = (Button)mainWindow.FindName("btnQuickFlushRam");
                txtSidebarUptime = (TextBlock)mainWindow.FindName("txtSidebarUptime");
                btnQuickFlushRam.Click += (s, e) => OptimizeRam();

                // Update Banner
                bannerUpdate = (Border)mainWindow.FindName("bannerUpdate");
                txtUpdateNotice = (TextBlock)mainWindow.FindName("txtUpdateNotice");
                btnDownloadUpdate = (Button)mainWindow.FindName("btnDownloadUpdate");
                btnDownloadUpdate.Click += (s, e) => {
                    try { Process.Start(REPO_URL + "/releases"); } catch { }
                };

                // Tab 1 Bindings
                cmbDrive = (ComboBox)mainWindow.FindName("cmbDrive");
                diskProgress = (ProgressBar)mainWindow.FindName("diskProgress");
                txtDiskPercent = (TextBlock)mainWindow.FindName("txtDiskPercent");
                txtDiskRatio = (TextBlock)mainWindow.FindName("txtDiskRatio");
                txtStatFree = (TextBlock)mainWindow.FindName("txtStatFree");
                txtStatUsed = (TextBlock)mainWindow.FindName("txtStatUsed");
                txtStatTotal = (TextBlock)mainWindow.FindName("txtStatTotal");
                txtSelectedSize = (TextBlock)mainWindow.FindName("txtSelectedSize");
                btnClean = (Button)mainWindow.FindName("btnClean");
                btnRescan = (Button)mainWindow.FindName("btnRescan");
                btnOptimizeRam = (Button)mainWindow.FindName("btnOptimizeRam");
                btnSelectAll = (Button)mainWindow.FindName("btnSelectAll");
                btnSelectSafe = (Button)mainWindow.FindName("btnSelectSafe");
                btnClearAll = (Button)mainWindow.FindName("btnClearAll");
                chkDryRun = (CheckBox)mainWindow.FindName("chkDryRun");
                txtConsole = (TextBox)mainWindow.FindName("txtConsole");
                btnExportLog = (Button)mainWindow.FindName("btnExportLog");
                btnClearLog = (Button)mainWindow.FindName("btnClearLog");
                pnlCategories = (StackPanel)mainWindow.FindName("pnlCategories");
                bannerSuccess = (Border)mainWindow.FindName("bannerSuccess");
                txtSuccessMsg = (TextBlock)mainWindow.FindName("txtSuccessMsg");

                cmbDrive.SelectionChanged += (s, e) => {
                    if (cmbDrive.SelectedItem != null) {
                        string sel = cmbDrive.SelectedItem.ToString();
                        if (sel.Length > 0) {
                            selectedDriveLetter = sel.Substring(0, 1).ToUpper();
                            UpdateDiskMetrics();
                            RunScanAsync();
                        }
                    }
                };
                btnRescan.Click += (s, e) => RunScanAsync();
                btnOptimizeRam.Click += (s, e) => OptimizeRam();
                btnSelectAll.Click += (s, e) => SetAllCheckboxes(true);
                btnSelectSafe.Click += (s, e) => SetSafeCheckboxes();
                btnClearAll.Click += (s, e) => SetAllCheckboxes(false);
                btnClean.Click += (s, e) => StartCleanAsync();
                btnClearLog.Click += (s, e) => txtConsole.Clear();
                btnExportLog.Click += (s, e) => ExportLog();

                // Tab 2 Bindings
                btnScanLargeFiles = (Button)mainWindow.FindName("btnScanLargeFiles");
                btnFilterAll100 = (Button)mainWindow.FindName("btnFilterAll100");
                btnFilter500MB = (Button)mainWindow.FindName("btnFilter500MB");
                btnFilter1GB = (Button)mainWindow.FindName("btnFilter1GB");
                pnlLargeFiles = (StackPanel)mainWindow.FindName("pnlLargeFiles");
                txtLargeFilesSummary = (TextBlock)mainWindow.FindName("txtLargeFilesSummary");
                btnStopLargeFiles = (Button)mainWindow.FindName("btnStopLargeFiles");

                btnScanLargeFiles.Click += (s, e) => ScanLargeFilesAsync();
                btnStopLargeFiles.Click += (s, e) => {
                    if (largeFilesCts != null) largeFilesCts.Cancel();
                    btnStopLargeFiles.IsEnabled = false;
                };
                btnFilterAll100.Click += (s, e) => ApplyLargeFileFilter(100 * 1024 * 1024);
                btnFilter500MB.Click += (s, e) => ApplyLargeFileFilter(500 * 1024 * 1024);
                btnFilter1GB.Click += (s, e) => ApplyLargeFileFilter(1024L * 1024 * 1024);

                // Tab 3 Bindings
                btnScanDuplicates = (Button)mainWindow.FindName("btnScanDuplicates");
                btnCleanAllDuplicates = (Button)mainWindow.FindName("btnCleanAllDuplicates");
                pnlDuplicates = (StackPanel)mainWindow.FindName("pnlDuplicates");
                txtDuplicatesSummary = (TextBlock)mainWindow.FindName("txtDuplicatesSummary");
                btnStopDuplicates = (Button)mainWindow.FindName("btnStopDuplicates");
                btnScanDuplicates.Click += (s, e) => ScanDuplicatesAsync();
                btnStopDuplicates.Click += (s, e) => {
                    if (duplicatesCts != null) duplicatesCts.Cancel();
                    btnStopDuplicates.IsEnabled = false;
                };
                btnCleanAllDuplicates.Click += (s, e) => CleanAllDuplicatesAsync();

                // Tab 4 Bindings
                btnScanStartup = (Button)mainWindow.FindName("btnScanStartup");
                pnlStartupApps = (StackPanel)mainWindow.FindName("pnlStartupApps");
                txtStartupSummary = (TextBlock)mainWindow.FindName("txtStartupSummary");
                btnScanStartup.Click += (s, e) => ScanStartupAppsAsync();

                // Tab 5 Bindings
                chkAutoClean = (CheckBox)mainWindow.FindName("chkAutoClean");
                txtScheduleStatus = (TextBlock)mainWindow.FindName("txtScheduleStatus");
                btnCreateShortcut = (Button)mainWindow.FindName("btnCreateShortcut");
                btnTestSilentClean = (Button)mainWindow.FindName("btnTestSilentClean");
                btnCheckUpdates = (Button)mainWindow.FindName("btnCheckUpdates");
                btnOpenGitHub = (Button)mainWindow.FindName("btnOpenGitHub");
                txtSysSpecOs = (TextBlock)mainWindow.FindName("txtSysSpecOs");
                txtSysSpecCpu = (TextBlock)mainWindow.FindName("txtSysSpecCpu");
                txtSysSpecRam = (TextBlock)mainWindow.FindName("txtSysSpecRam");
                txtSysSpecGpu = (TextBlock)mainWindow.FindName("txtSysSpecGpu");

                chkAutoClean.Click += (s, e) => ToggleScheduler(chkAutoClean.IsChecked == true);
                btnCreateShortcut.Click += (s, e) => CreateDesktopShortcut();
                btnTestSilentClean.Click += (s, e) => {
                    Log("Running silent clean test run...");
                    Task.Run(new Action(() => {
                        PerformSilentCleanup();
                        mainWindow.Dispatcher.Invoke(new Action(() => {
                            Log("Silent clean test finished successfully.");
                            UpdateDiskMetrics();
                            RunScanAsync();
                        }));
                    }));
                };
                btnCheckUpdates.Click += (s, e) => CheckForUpdatesAsync(true);
                btnOpenGitHub.Click += (s, e) => {
                    try { Process.Start(REPO_URL); } catch { }
                };

                // Initialize System Info, Drives, Categories
                SwitchTab(0);
                PopulateDrives();
                InitCategoryDefinitions();
                DetectSystemHardware();
                CheckScheduledTask();
                CheckForUpdatesAsync(false);
                StartUptimeTimer();

                Log("OmniClean v1.0.5 initialized.");
                Log(string.Format("Host OS: {0} ({1}-bit)", Environment.OSVersion, Environment.Is64BitOperatingSystem ? "64" : "32"));
                Log("Select cleanable targets and click 'Clean Selected Storage'.");

                UpdateDiskMetrics();
                RunScanAsync();

                app.Run(mainWindow);
            } catch (Exception ex) {
                MessageBox.Show("Fatal startup error:\r\n" + ex.ToString(), "OmniClean Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) {
            try {
                WriteCrashLog(e.Exception);
                MessageBox.Show("An unexpected error occurred:\r\n" + e.Exception.Message + "\r\n\r\nDetails were written to the crash log.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Error);
            } catch { }
            e.Handled = true;
        }

        static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) {
            try {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) WriteCrashLog(ex);
            } catch { }
        }

        static void WriteCrashLog(Exception ex) {
            try {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniClean", "logs");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"),
                    "OmniClean " + CURRENT_VERSION + " crash\r\nTime: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n\r\n" + ex.ToString());
            } catch { }
        }

        static bool IsNewerVersion(string latestTag, string currentVersion) {
            string[] partsLatest = (latestTag ?? "").Trim().TrimStart('v', 'V').Split('.');
            string[] partsCurrent = (currentVersion ?? "").Trim().TrimStart('v', 'V').Split('.');
            for (int i = 0; i < 3; i++) {
                int lv = 0, cv = 0;
                int.TryParse(partsLatest.Length > i ? partsLatest[i] : "0", out lv);
                int.TryParse(partsCurrent.Length > i ? partsCurrent[i] : "0", out cv);
                if (lv != cv) return lv > cv;
            }
            return false;
        }

        static void BeginCleanManifest(bool isDryRun) {
            cleanDeletedCount = 0;
            cleanFailedCount = 0;
            cleanFreedMB = 0;
            try {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniClean", "logs");
                Directory.CreateDirectory(dir);
                lastCleanLogPath = Path.Combine(dir, "clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (isDryRun ? "-preview" : "") + ".log");
                cleanManifest = new StreamWriter(lastCleanLogPath, false, Encoding.UTF8);
                cleanManifest.WriteLine("# OmniClean " + CURRENT_VERSION + " cleanup manifest");
                cleanManifest.WriteLine("# Mode: " + (isDryRun ? "PREVIEW (nothing deleted)" : "LIVE CLEAN"));
                cleanManifest.WriteLine("# Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cleanManifest.Flush();
            } catch { cleanManifest = null; }
        }

        static void Manifest(string line) {
            try {
                if (cleanManifest != null) {
                    cleanManifest.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + line);
                    cleanManifest.Flush();
                }
            } catch { }
        }

        static void EndCleanManifest(string summaryLine) {
            try {
                if (cleanManifest != null) {
                    if (summaryLine != null) cleanManifest.WriteLine("# " + summaryLine);
                    cleanManifest.WriteLine("# Finished: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cleanManifest.Dispose();
                }
            } catch { }
            cleanManifest = null;
        }

        static void SwitchTab(int index) {
            mainTabControl.SelectedIndex = index;

            SetNavState(btnNavCleaner, barNavCleaner, index == 0);
            SetNavState(btnNavLargeFiles, barNavLargeFiles, index == 1);
            SetNavState(btnNavDuplicates, barNavDuplicates, index == 2);
            SetNavState(btnNavStartup, barNavStartup, index == 3);
            SetNavState(btnNavSettings, barNavSettings, index == 4);

            if (index == 1 && allLargeFiles.Count == 0) {
                ScanLargeFilesAsync();
            } else if (index == 2 && duplicateGroups.Count == 0) {
                ScanDuplicatesAsync();
            } else if (index == 3 && startupApps.Count == 0) {
                ScanStartupAppsAsync();
            }
        }

        static void SetNavState(Button btn, Border bar, bool isActive) {
            if (btn == null || bar == null) return;
            bar.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            btn.Background = isActive
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D))
                : Brushes.Transparent;
            bar.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            btn.Foreground = isActive ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
            btn.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // ---- Win11 Fluent Card Brush Helpers ----
        static Brush CardBgBrush() {
            return new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        }
        static Brush CardHoverBrush() {
            return new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
        }
        static Brush CardBorderBrush() {
            return new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36));
        }
        static Brush CardHoverBorderBrush() {
            return new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48));
        }

        static void StartUptimeTimer() {
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += (s, e) => {
                try {
                    TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount);
                    string upStr = string.Format("Uptime: {0}d {1}h {2}m", uptime.Days, uptime.Hours, uptime.Minutes);
                    txtHeaderUptimePill.Text = upStr;
                    txtSidebarUptime.Text = upStr;
                    RefreshRamMetrics();
                } catch { }
            };
            timer.Start();
        }

        static void RefreshRamMetrics() {
            try {
                MEMORYSTATUSEX mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem)) {
                    double totalGB = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double freeGB = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedGB = totalGB - freeGB;
                    uint pct = mem.dwMemoryLoad;

                    if (barSidebarRam != null) barSidebarRam.Value = pct;
                    if (txtSidebarRam != null) txtSidebarRam.Text = string.Format("RAM: {0:F1} / {1:F1} GB ({2}%)", usedGB, totalGB, pct);
                }
            } catch { }
        }

        static void Log(string msg) {
            // The console may not exist yet (headless --silent-clean) and the
            // call may already be on the UI thread, where a Dispatcher.Invoke
            // would deadlock.
            if (txtConsole == null || mainWindow == null) return;
            if (mainWindow.Dispatcher.CheckAccess()) {
                AppendLogLine(msg);
                return;
            }
            mainWindow.Dispatcher.Invoke(new Action(() => {
                AppendLogLine(msg);
            }));
        }

        static void AppendLogLine(string msg) {
            if (txtConsole == null) return;
            string time = DateTime.Now.ToString("HH:mm:ss");
            txtConsole.AppendText(string.Format("[{0}] {1}\r\n", time, msg));
            txtConsole.ScrollToEnd();
        }

        static void ExportLog() {
            try {
                var sfd = new SaveFileDialog();
                sfd.FileName = string.Format("OmniClean_Log_{0:yyyyMMdd_HHmmss}.txt", DateTime.Now);
                sfd.Filter = "Text Documents (*.txt)|*.txt|All Files (*.*)|*.*";
                if (sfd.ShowDialog() == true) {
                    File.WriteAllText(sfd.FileName, txtConsole.Text);
                    Log("Activity log exported to " + sfd.FileName);
                }
            } catch (Exception ex) {
                Log("Failed to export log: " + ex.Message);
            }
        }

        static void DetectSystemHardware() {
            Task.Run(new Action(() => {
                string cpu = "Generic CPU";
                string gpu = "Standard Graphics Adapter";
                string ramGB = "16";
                string osName = string.Format("Windows ({0}-bit)", Environment.Is64BitOperatingSystem ? "64" : "32");

                try {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) {
                        if (key != null) {
                            object val = key.GetValue("ProcessorNameString");
                            if (val != null) cpu = val.ToString().Trim();
                        }
                    }
                } catch { }

                try {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus)) {
                        double total = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                        ramGB = string.Format("{0:F1}", total);
                    }
                } catch { }

                try {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000")) {
                        if (key != null) {
                            object val = key.GetValue("DriverDesc");
                            if (val != null) gpu = val.ToString().Trim();
                        }
                    }
                } catch { }

                try {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                        if (key != null) {
                            object prod = key.GetValue("ProductName");
                            object build = key.GetValue("CurrentBuild");
                            if (prod != null) osName = string.Format("{0} (Build {1})", prod, build != null ? build.ToString() : "");
                        }
                    }
                } catch { }

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    txtSidebarCpu.Text = "CPU: " + cpu;
                    RefreshRamMetrics();

                    txtSysSpecOs.Text = "OS: " + osName;
                    txtSysSpecCpu.Text = "Processor: " + cpu;
                    txtSysSpecRam.Text = "Physical Memory: " + ramGB + " GB";
                    txtSysSpecGpu.Text = "Graphics: " + gpu;
                }));
            }));
        }

        static void CheckForUpdatesAsync(bool userInitiated) {
            Task.Run(new Action(() => {
                try {
                    var req = (HttpWebRequest)WebRequest.Create(RELEASES_API);
                    req.UserAgent = "OmniClean";
                    req.Timeout = 5000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream())) {
                        string json = reader.ReadToEnd();
                        var match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (match.Success) {
                            string latestTag = match.Groups[1].Value.Trim();
                            bool isNew = IsNewerVersion(latestTag, CURRENT_VERSION);

                            mainWindow.Dispatcher.Invoke(new Action(() => {
                                if (isNew) {
                                    bannerUpdate.Visibility = Visibility.Visible;
                                    txtUpdateNotice.Text = string.Format("Update Available: {0} (Current: {1})", latestTag, CURRENT_VERSION);
                                    if (userInitiated) {
                                        MessageBox.Show(mainWindow, string.Format("A new version ({0}) is available on GitHub!\r\nClick 'View Release' to download.", latestTag), "OmniClean Update", MessageBoxButton.OK, MessageBoxImage.Information);
                                    }
                                } else if (userInitiated) {
                                    MessageBox.Show(mainWindow, string.Format("You are running the latest version of OmniClean ({0}).", CURRENT_VERSION), "OmniClean Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }));
                        }
                    }
                } catch {
                    if (userInitiated) {
                        mainWindow.Dispatcher.Invoke(new Action(() => {
                            MessageBox.Show(mainWindow, "Unable to check for updates at this time. Please check your internet connection.", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }));
                    }
                }
            }));
        }

        static void OptimizeRam() {
            Task.Run(new Action(() => {
                try {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    EmptyWorkingSet(Process.GetCurrentProcess().Handle);

                    int count = 0;
                    foreach (var proc in Process.GetProcesses()) {
                        try {
                            if (proc.Id != Process.GetCurrentProcess().Id) {
                                EmptyWorkingSet(proc.Handle);
                                count++;
                            }
                        } catch { }
                    }

                    mainWindow.Dispatcher.Invoke(new Action(() => {
                        Log(string.Format("RAM Optimizer: Working sets trimmed across {0} active processes.", count));
                        RefreshRamMetrics();
                    }));
                } catch (Exception ex) {
                    Log("RAM Optimizer error: " + ex.Message);
                }
            }));
        }

        static void PopulateDrives() {
            cmbDrive.Items.Clear();
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            string sysLetter = (sysRoot != null && sysRoot.Length > 0) ? sysRoot.Substring(0, 1).ToUpper() : "C";

            int selectedIndex = 0;
            int idx = 0;
            foreach (var d in DriveInfo.GetDrives()) {
                if (d.IsReady && d.DriveType == DriveType.Fixed) {
                    string letter = d.Name.Substring(0, 1).ToUpper();
                    string label = string.Format("{0}: ({1:F0} GB)", letter, d.TotalSize / (1024.0 * 1024.0 * 1024.0));
                    cmbDrive.Items.Add(label);
                    if (letter == sysLetter) {
                        selectedIndex = idx;
                        selectedDriveLetter = letter;
                    }
                    idx++;
                }
            }

            if (cmbDrive.Items.Count > 0) {
                cmbDrive.SelectedIndex = selectedIndex;
            }
        }

        static void InitCategoryDefinitions() {
            categories.Clear();
            pnlCategories.Children.Clear();

            AddCategory("recycle_bin", "Windows Recycle Bin", "All files and deleted items sent to the Recycle Bin", "🗑️", "#60CDFF");
            AddCategory("temp_files", "System & User Temporary Files", "Windows and application temporary cache in %TEMP% and Windows\\Temp", "📁", "#60CDFF");
            AddCategory("browser_cache", "Web Browser Caches", "Chrome, Edge, Brave, Firefox, and Opera internet cache and shader data", "🌐", "#60CDFF");
            AddCategory("win_update", "Windows Update Download Cache", "Leftover update packages in SoftwareDistribution\\Download", "🔄", "#60CDFF");
            AddCategory("crash_dumps", "Error Reports & Crash Dumps", "Windows Error Reporting (WER) and memory dump logs", "⚠️", "#FCE100");
            AddCategory("empty_folders", "Empty Temporary Directories", "Abandoned 0-byte directories created by uninstalled applications", "📂", "#8A8A8A");

            AddCategory("vscode_cache", "VS Code Caches & Crash Dumps", "Visual Studio Code installer packages and crash logs", "💻", "#60CDFF");
            AddCategory("dev_cache", "npm & pip Package Caches", "Node.js and Python package manager cache folders", "📦", "#60CDFF");
            AddCategory("discord_cache", "Discord Client Cache", "Discord media, audio, and GPU shader caches", "💬", "#60CDFF");
            AddCategory("capcut_old", "Outdated CapCut App Versions", "Older builds leftover in AppData\\Local\\CapCut\\Apps", "🎬", "#60CDFF");
            AddCategory("nvidia_artifacts", "NVIDIA Driver Update Packages", "Downloaded GPU driver installers in NVIDIA App UpdateFramework", "🎮", "#6CCB5F");
        }

        static void AddCategory(string id, string name, string desc, string icon, string iconBgHex) {
            Border card = new Border();
            card.Background = CardBgBrush();
            card.BorderBrush = CardBorderBrush();
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(14, 10, 14, 10);
            card.Margin = new Thickness(0, 0, 0, 5);
            card.CornerRadius = new CornerRadius(6);
            card.Cursor = Cursors.Hand;

            card.MouseEnter += (s, e) => {
                card.Background = CardHoverBrush();
                card.BorderBrush = CardHoverBorderBrush();
            };
            card.MouseLeave += (s, e) => {
                card.Background = CardBgBrush();
                card.BorderBrush = CardBorderBrush();
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            CheckBox chk = new CheckBox();
            chk.IsChecked = true;
            chk.VerticalAlignment = VerticalAlignment.Center;
            chk.Margin = new Thickness(0, 0, 12, 0);
            chk.Checked += (s, e) => UpdateSelectedSize();
            chk.Unchecked += (s, e) => UpdateSelectedSize();
            Grid.SetColumn(chk, 0);
            grid.Children.Add(chk);

            StackPanel sp = new StackPanel();
            StackPanel titleRow = new StackPanel();
            titleRow.Orientation = Orientation.Horizontal;

            Border iconChip = new Border();
            iconChip.Width = 26;
            iconChip.Height = 26;
            iconChip.CornerRadius = new CornerRadius(5);
            iconChip.Margin = new Thickness(0, 0, 10, 0);
            iconChip.VerticalAlignment = VerticalAlignment.Center;
            iconChip.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            iconChip.BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
            iconChip.BorderThickness = new Thickness(1);

            TextBlock iconBlock = new TextBlock();
            iconBlock.Text = icon;
            iconBlock.FontSize = 13;
            iconBlock.HorizontalAlignment = HorizontalAlignment.Center;
            iconBlock.VerticalAlignment = VerticalAlignment.Center;
            iconChip.Child = iconBlock;

            titleRow.Children.Add(iconChip);

            TextBlock title = new TextBlock();
            title.Text = name;
            title.FontWeight = FontWeights.SemiBold;
            title.FontSize = 13;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            title.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(title);

            TextBlock txtDesc = new TextBlock();
            txtDesc.Text = desc;
            txtDesc.FontSize = 11;
            txtDesc.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            txtDesc.Margin = new Thickness(0, 2, 0, 0);

            sp.Children.Add(titleRow);
            sp.Children.Add(txtDesc);
            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);

            Border badge = new Border();
            badge.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            badge.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
            badge.BorderThickness = new Thickness(1);
            badge.Padding = new Thickness(10, 3, 10, 3);
            badge.CornerRadius = new CornerRadius(4);
            badge.VerticalAlignment = VerticalAlignment.Center;

            TextBlock txtBadge = new TextBlock();
            txtBadge.Text = "Scanning...";
            txtBadge.FontSize = 11;
            txtBadge.FontWeight = FontWeights.SemiBold;
            txtBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
            badge.Child = txtBadge;

            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);

            card.Child = grid;
            pnlCategories.Children.Add(card);

            categories.Add(new CategoryItem {
                Id = id,
                Name = name,
                Description = desc,
                IconText = icon,
                IconBgHex = iconBgHex,
                SizeMB = 0,
                IsInstalled = true,
                CheckBoxRef = chk,
                BadgeRef = txtBadge,
                CardRef = card
            });
        }

        static void SetAllCheckboxes(bool isChecked) {
            foreach (var c in categories) {
                if (c.IsInstalled) {
                    c.CheckBoxRef.IsChecked = isChecked;
                }
            }
            UpdateSelectedSize();
        }

        static void SetSafeCheckboxes() {
            foreach (var c in categories) {
                if (c.IsInstalled) {
                    c.CheckBoxRef.IsChecked = (c.Id != "empty_folders");
                }
            }
            UpdateSelectedSize();
        }

        static void UpdateSelectedSize() {
            double totalMB = 0;
            int count = 0;
            foreach (var c in categories) {
                if (c.CheckBoxRef.IsChecked == true && c.IsInstalled) {
                    totalMB += c.SizeMB;
                    count++;
                }
            }

            if (totalMB > 1024) {
                txtSelectedSize.Text = string.Format("{0:F2} GB", totalMB / 1024.0);
            } else {
                txtSelectedSize.Text = string.Format("{0:F1} MB", totalMB);
            }

            btnClean.IsEnabled = (count > 0) && !isScanning && !isCleaning;
        }

        static void UpdateDiskMetrics() {
            try {
                DriveInfo drive = new DriveInfo(selectedDriveLetter);
                if (!drive.IsReady) return;

                double totalGB = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                double freeGB = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double usedGB = totalGB - freeGB;

                // A zero-byte or unreadable volume would divide by zero and
                // produce NaN/Infinity in the UI.
                if (totalGB <= 0) return;
                double pctFree = (freeGB / totalGB) * 100.0;

                txtDiskPercent.Text = string.Format("{0:F1}% Free", pctFree);
                txtDiskRatio.Text = string.Format("{0:F1} GB Free / {1:F1} GB Total", freeGB, totalGB);
                txtStatFree.Text = string.Format("{0:F1} GB", freeGB);
                txtStatUsed.Text = string.Format("{0:F1} GB", usedGB);
                txtStatTotal.Text = string.Format("{0:F1} GB", totalGB);

                double usedPct = (usedGB / totalGB) * 100.0;
                diskProgress.Value = usedPct < 0 ? 0 : (usedPct > 100 ? 100 : usedPct);
            } catch { }
        }

        // Runs a schtasks query and reports whether the named task exists.
        static bool ScheduledTaskExists(string taskName) {
            try {
                Process p = new Process();
                p.StartInfo.FileName = "schtasks.exe";
                p.StartInfo.Arguments = "/query /tn \"" + taskName + "\"";
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.Start();
                p.WaitForExit();
                return p.ExitCode == 0;
            } catch { return false; }
        }

        static void CheckScheduledTask() {
            Task.Run(new Action(() => {
                // Both editions schedule a weekly maintenance job under their
                // own task name - either one means "a schedule is active".
                bool hasTask = ScheduledTaskExists(TASK_NAME) || ScheduledTaskExists(WEB_TASK_NAME);

                mainWindow.Dispatcher.Invoke(new Action(() => {
                    chkAutoClean.IsChecked = hasTask;
                    txtScheduleStatus.Text = hasTask ? "Status: Active (Every Sunday at 12:00 PM)" : "Status: Disabled";
                    txtScheduleStatus.Foreground = hasTask ? new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)) : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
                }));
            }));
        }

        static void ToggleScheduler(bool enable) {
            Task.Run(new Action(() => {
                try {
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    Process p = new Process();
                    p.StartInfo.FileName = "schtasks.exe";
                    if (enable) {
                        string taskCmd = "\"" + exePath + "\" --silent-clean";
                        p.StartInfo.Arguments = string.Format("/create /tn \"{0}\" /tr \"{1}\" /sc weekly /d SUN /st 12:00 /f", TASK_NAME, taskCmd);
                    } else {
                        p.StartInfo.Arguments = string.Format("/delete /tn \"{0}\" /f", TASK_NAME);
                    }
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.Start();
                    p.WaitForExit();

                    if (p.ExitCode != 0) {
                        // schtasks failed (no permission, task missing, ...).
                        // Report it instead of claiming success.
                        mainWindow.Dispatcher.Invoke(new Action(() => {
                            Log((enable ? "Failed to schedule" : "Failed to remove") + " the auto-clean task (schtasks exit code " + p.ExitCode + ").");
                        }));
                        return;
                    }

                    mainWindow.Dispatcher.Invoke(new Action(() => {
                        txtScheduleStatus.Text = enable ? "Status: Active (Every Sunday at 12:00 PM)" : "Status: Disabled";
                        txtScheduleStatus.Foreground = enable ? new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)) : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
                        Log(enable ? "Scheduled weekly auto-clean in Windows Task Scheduler." : "Removed auto-clean scheduled task.");
                    }));
                } catch (Exception ex) {
                    Log("Scheduler error: " + ex.Message);
                }
            }));
        }

        static void CreateDesktopShortcut() {
            try {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktop, "OmniClean.lnk");
                string exePath = Process.GetCurrentProcess().MainModule.FileName;

                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "OmniClean - Universal Windows Optimizer";
                shortcut.IconLocation = exePath + ",0";
                shortcut.Save();
                Log("Desktop shortcut created.");
                MessageBox.Show(mainWindow, "Desktop shortcut successfully created!", "OmniClean", MessageBoxButton.OK, MessageBoxImage.Information);
            } catch (Exception ex) {
                Log("Shortcut error: " + ex.Message);
            }
        }

        static void PerformSilentCleanup() {
            try {
                BeginCleanManifest(false);
                SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                Manifest("[RECYCLE-BIN] emptied by scheduled silent clean");
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                CleanFolder(Path.GetTempPath(), false);
                CleanFolder(Path.Combine(winDir, "Temp"), false);
                CleanBrowserCaches(false);
                CleanFolder(Path.Combine(winDir, @"SoftwareDistribution\Download"), false);

                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                CleanFolder(Path.Combine(localApp, "CrashDumps"), false);
                CleanFolder(Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"), false);

                CleanFolder(Path.Combine(localApp, "npm-cache"), false);
                CleanFolder(Path.Combine(localApp, @"pip\cache"), false);

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                CleanFolder(Path.Combine(appData, @"discord\Cache"), false);

                string vscPath = Path.Combine(appData, "Code");
                if (Directory.Exists(vscPath)) {
                    CleanFolder(Path.Combine(vscPath, "CachedExtensionVSIXs"), false);
                    CleanFolder(Path.Combine(vscPath, @"Crashpad\reports"), false);
                }
            } catch (Exception ex) {
                Manifest("[ERROR] silent clean aborted -- " + ex.Message);
            } finally {
                EndCleanManifest("Scheduled silent clean finished: " + cleanDeletedCount + " items removed, " + cleanFailedCount + " failures.");
            }
        }
    }
}