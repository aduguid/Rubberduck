﻿using Extensibility;
using Ninject;
using Ninject.Extensions.Factory;
using Rubberduck.Root;
using Rubberduck.UI;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using Ninject.Extensions.Interception;
using NLog;
using Rubberduck.Settings;
using Rubberduck.SettingsProvider;
using Rubberduck.VBEditor.Events;
using Rubberduck.VBEditor.SafeComWrappers.VB.Abstract;
using VBAIA = Microsoft.Vbe.Interop;
using VBA = Rubberduck.VBEditor.SafeComWrappers.VB.VBA;
using VB6IA = Microsoft.VB6.Interop.VBIDE; 
using VB6 = Rubberduck.VBEditor.SafeComWrappers.VB.VB6;
using Rubberduck.VBEditor.WindowsApi;
using User32 = Rubberduck.Common.WinAPI.User32;


namespace Rubberduck
{
    /// <remarks>
    /// Special thanks to Carlos Quintero (MZ-Tools) for providing the general structure here.
    /// </remarks>
    [ComVisible(true)]
    [Guid(RubberduckGuid.ExtensionGuid)]
    [ProgId(RubberduckProgId.ExtensionProgId)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    // ReSharper disable once InconsistentNaming // note: underscore prefix hides class from COM API
    public class _Extension : IDTExtensibility2
    {
        private IVBE _ide;
        private IAddIn _addin;
        private bool _isInitialized;
        private bool _isBeginShutdownExecuted;

        private IKernel _kernel;
        private App _app;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public void OnAddInsUpdate(ref Array custom) { }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public void OnConnection(object Application, ext_ConnectMode ConnectMode, object AddInInst, ref Array custom)
        {
            try
            {
                if (Application is VBAIA.VBE vbae)
                {                    
                    _ide = new VBA.VBE(vbae);
                    VBENativeServices.HookEvents(_ide);
                    
                    var addin = (VBAIA.AddIn)AddInInst;
                    _addin = new VBA.AddIn(addin) { Object = this };
                }
                else if (Application is VB6IA.VBE vb6e)
                {                    
                    _ide = new VB6.VBE(vb6e);

                    var addin = (VB6IA.AddIn) AddInInst;
                    _addin = new VB6.AddIn(addin);
                }


                switch (ConnectMode)
                {
                    case ext_ConnectMode.ext_cm_Startup:
                        // normal execution path - don't initialize just yet, wait for OnStartupComplete to be called by the host.
                        break;
                    case ext_ConnectMode.ext_cm_AfterStartup:
                        InitializeAddIn();
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        Assembly LoadFromSameFolder(object sender, ResolveEventArgs args)
        {
            var folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var assemblyPath = Path.Combine(folderPath, new AssemblyName(args.Name).Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                return null;
            }

            var assembly = Assembly.LoadFile(assemblyPath);
            return assembly;
        }

        public void OnStartupComplete(ref Array custom)
        {
            InitializeAddIn();
        }

        public void OnBeginShutdown(ref Array custom)
        {
            _isBeginShutdownExecuted = true;
            ShutdownAddIn();
        }

        // ReSharper disable InconsistentNaming
        public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
        {            
            switch (RemoveMode)
            {
                case ext_DisconnectMode.ext_dm_UserClosed:
                    ShutdownAddIn();
                    break;

                case ext_DisconnectMode.ext_dm_HostShutdown:
                    if (_isBeginShutdownExecuted)
                    {
                        // this is the normal case: nothing to do here, we already ran ShutdownAddIn.
                    }
                    else
                    {
                        // some hosts do not call OnBeginShutdown: this mitigates it.
                        ShutdownAddIn();
                    }
                    break;
            }
        }

        private void InitializeAddIn()
        {
            if (_isInitialized)
            {
                // The add-in is already initialized. See:
                // The strange case of the add-in initialized twice
                // http://msmvps.com/blogs/carlosq/archive/2013/02/14/the-strange-case-of-the-add-in-initialized-twice.aspx
                return;
            }

            var configLoader = new XmlPersistanceService<GeneralSettings>
            {
                FilePath =
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Rubberduck", "rubberduck.config")
            };
            var configProvider = new GeneralConfigProvider(configLoader);
            
            var settings = configProvider.Create();
            if (settings != null)
            {
                try
                {
                    var cultureInfo = CultureInfo.GetCultureInfo(settings.Language.Code);
                    Dispatcher.CurrentDispatcher.Thread.CurrentUICulture = cultureInfo;
                }
                catch (CultureNotFoundException)
                {
                }
            }
            else
            {
                Debug.Assert(false, "Settings could not be initialized.");
            }

            Splash splash = null;
            if (settings.ShowSplash)
            {
                splash = new Splash
                {
                    // note: IVersionCheck.CurrentVersion could return this string.
                    Version = $"version {Assembly.GetExecutingAssembly().GetName().Version}"
                };
                splash.Show();
                splash.Refresh();
            }

            try
            {
                Startup();
            }
            catch (Win32Exception)
            {
                System.Windows.Forms.MessageBox.Show(RubberduckUI.RubberduckReloadFailure_Message, RubberduckUI.RubberduckReloadFailure_Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            catch (Exception exception)
            {
                _logger.Fatal(exception);
                System.Windows.Forms.MessageBox.Show(exception.ToString(), RubberduckUI.RubberduckLoadFailure,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                splash?.Dispose();
            }
        }

        private void Startup()
        {
            try
            {
                var currentDomain = AppDomain.CurrentDomain;
                currentDomain.UnhandledException += HandlAppDomainException;
                currentDomain.AssemblyResolve += LoadFromSameFolder;

                _kernel = new StandardKernel(new NinjectSettings {LoadExtensions = true}, new FuncModule(), new DynamicProxyModule());
                _kernel.Load(new RubberduckModule(_ide, _addin));

                _app = _kernel.Get<App>();
                _app.Startup();

                _isInitialized = true;

            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Fatal, e, "Startup sequence threw an unexpected exception.");
                //throw; // <<~ uncomment to crash the process
            }
        }

        private void HandlAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            _logger.Log(LogLevel.Fatal, e);
        }

        private void ShutdownAddIn()
        {
            var currentDomain = AppDomain.CurrentDomain;
            try
            {
                _logger.Log(LogLevel.Info, "Rubberduck is shutting down.");
                _logger.Log(LogLevel.Trace, "Unhooking VBENativeServices events...");
                VBENativeServices.UnhookEvents();

                _logger.Log(LogLevel.Trace, "Broadcasting shutdown...");
                User32.EnumChildWindows(_ide.MainWindow.Handle(), EnumCallback, new IntPtr(0));

                _logger.Log(LogLevel.Trace, "Releasing dockable hosts...");
                VBA.Windows.ReleaseDockableHosts(); // TODO!!!

                if (_app != null)
                {
                    _logger.Log(LogLevel.Trace, "Initiating App.Shutdown...");
                    _app.Shutdown();
                    _app = null;
                }

                if (_kernel != null)
                {
                    _logger.Log(LogLevel.Trace, "Disposing IoC container...");
                    _kernel.Dispose();
                    _kernel = null;
                }

                _isInitialized = false;
                _logger.Log(LogLevel.Info, "No exceptions were thrown.");
            }
            catch (Exception e)
            {
                _logger.Error(e);
                _logger.Log(LogLevel.Warn, "Exception is swallowed.");
                //throw; // <<~ uncomment to crash the process
            }
            finally
            {
                _logger.Log(LogLevel.Trace, "Unregistering AppDomain handlers....");
                currentDomain.AssemblyResolve -= LoadFromSameFolder;
                currentDomain.UnhandledException -= HandlAppDomainException;
                _logger.Log(LogLevel.Trace, "Done. Initiating garbage collection...");
                GC.Collect();
                _logger.Log(LogLevel.Trace, "Done. Waiting for pending finalizers...");
                GC.WaitForPendingFinalizers();
                _logger.Log(LogLevel.Trace, "Done. Shutdown completed. Quack!");
                _isInitialized = false;
            }
        }

        private static int EnumCallback(IntPtr hwnd, IntPtr lparam)
        {
            User32.SendMessage(hwnd, WM.RUBBERDUCK_SINKING, IntPtr.Zero, IntPtr.Zero);
            return 1;
        }
    }
}
