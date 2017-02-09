// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using NuGet.Configuration;
using NuGet.PackageManagement;
using NuGet.PackageManagement.UI;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Protocol.Core.Types;
using Task = System.Threading.Tasks.Task;

namespace NuGet.SolutionRestoreManager
{
    /// <summary>
    /// Visual Studio extension package designed to bootstrap solution restore components.
    /// Loads on solution open to attach to build events.
    /// </summary>
    // Flag AllowsBackgroundLoading is set to False because switching to Main thread wiht JTF is creating
    // performance overhead in InitializeAsync() API.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    [Guid(PackageGuidString)]
    public sealed class RestoreManagerPackage : AsyncPackage, IVsUpdateSolutionEvents4
    {
        public const string ProductVersion = "4.0.0";

        /// <summary>
        /// RestoreManagerPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "2b52ac92-4551-426d-bd34-c6d7d9fdd1c5";

        private Lazy<ISolutionRestoreWorker> _restoreWorker;
        private Lazy<ISettings> _settings;
        private Lazy<IVsSolutionManager> _solutionManager;
        private Lazy<INuGetLockService> _lockService;

        private ISolutionRestoreWorker SolutionRestoreWorker => _restoreWorker.Value;
        private ISettings Settings => _settings.Value;
        private IVsSolutionManager SolutionManager => _solutionManager.Value;
        private INuGetLockService LockService => _lockService.Value;

        // keeps a reference to BuildEvents so that our event handler
        // won't get disconnected.
        private EnvDTE.BuildEvents _buildEvents;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            componentModel.DefaultCompositionService.SatisfyImportsOnce(this);

            _restoreWorker = new Lazy<ISolutionRestoreWorker>(
                () => componentModel.GetService<ISolutionRestoreWorker>());

            _settings = new Lazy<ISettings>(
                () => componentModel.GetService<ISettings>());

            _solutionManager = new Lazy<IVsSolutionManager>(
                () => componentModel.GetService<IVsSolutionManager>());

            _lockService = new Lazy<INuGetLockService>(
                () => componentModel.GetService<INuGetLockService>());

            // Don't use CPS thread helper because of RPS perf regression
            await ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(SDTE));
                _buildEvents = dte.Events.BuildEvents;
                _buildEvents.OnBuildBegin += BuildEvents_OnBuildBegin;

                var solutionBuildManager = (IVsSolutionBuildManager5)await GetServiceAsync(typeof(SVsSolutionBuildManager));
                if (solutionBuildManager != null)
                {
                    uint updateSolutionEventsCookie4;
                    solutionBuildManager.AdviseUpdateSolutionEvents4(this, out updateSolutionEventsCookie4);
                }

                UserAgent.SetUserAgentString(
                    new UserAgentStringBuilder().WithVisualStudioSKU(dte.GetFullVsVersionString()));
            });

            await SolutionRestoreCommand.InitializeAsync(this);

            await base.InitializeAsync(cancellationToken, progress);
        }

        private void BuildEvents_OnBuildBegin(
            EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction Action)
        {
            if (Action == EnvDTE.vsBuildAction.vsBuildActionClean)
            {
                // Clear the project.json restore cache on clean to ensure that the next build restores again
                SolutionRestoreWorker.CleanCache();

                return;
            }

            if (!ShouldRestoreOnBuild)
            {
                return;
            }

            var forceRestore = Action == EnvDTE.vsBuildAction.vsBuildActionRebuildAll;

            // Execute
            SolutionRestoreWorker.Restore(SolutionRestoreRequest.OnBuild(forceRestore));
        }

        /// <summary>
        /// Returns true if automatic package restore on build is enabled.
        /// </summary>
        private bool ShouldRestoreOnBuild
        {
            get
            {
                var packageRestoreConsent = new PackageRestoreConsent(Settings);
                return packageRestoreConsent.IsAutomatic;
            }
        }

        #region IVsUpdateSolutionEvents4

        public void UpdateSolution_QueryDelayFirstUpdateAction(out int pfDelay)
        {
            // check if NuGet lock is already acquired by some other NuGet operation
            if (LockService.IsLockHeld)
            {
                // delay build by 1000ms and let it try again after that.
                pfDelay = 1000;
            }
            else
            {
                // Disable running auto restore at this time, since it will start the on build restore
                SolutionRestoreWorker.IsAutoRestoreAllowed = false;

                // Set delay to 0 which means don't delay build
                pfDelay = 0;
            }
        }

        public void UpdateSolution_BeginFirstUpdateAction() { }

        public void UpdateSolution_EndLastUpdateAction()
        {
            // Enable running auto restore at this time, since build restore is done.
            // It's a fallback code, since in some cases, dte onbuild event isn't triggered on F5
            // so it will always make sure to reset this flag to allow auto restore
            SolutionRestoreWorker.IsAutoRestoreAllowed = true;
        }

        public void UpdateSolution_BeginUpdateAction(uint dwAction) { }

        public void UpdateSolution_EndUpdateAction(uint dwAction) { }

        public void OnActiveProjectCfgChangeBatchBegin() { }

        public void OnActiveProjectCfgChangeBatchEnd() { }

        #endregion IVsUpdateSolutionEvents4
    }
}
