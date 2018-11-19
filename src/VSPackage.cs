﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using EnvDTE;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;

namespace StringResourceVisualizer
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [ProvideAutoLoad(UIContextGuids.SolutionHasMultipleProjects, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids.SolutionHasSingleProject, PackageAutoLoadFlags.BackgroundLoad)]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.1")] // Info on this package for Help/About
    [Guid(VSPackage.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    public sealed class VSPackage : AsyncPackage
    {
        /// <summary>
        /// VSPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "8c14dc72-9022-42ff-a85c-1cfe548a8956";

        /// <summary>
        /// Initializes a new instance of the <see cref="VSPackage"/> class.
        /// </summary>
        public VSPackage()
        {
            // Inside this method you can place any initialization code that does not require
            // any Visual Studio service because at this point the package object is created but
            // not sited yet inside Visual Studio environment. The place to do all the other
            // initialization is the Initialize method.
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Since this package might not be initialized until after a solution has finished loading,
            // we need to check if a solution has already been loaded and then handle it.
            bool isSolutionLoaded = await IsSolutionLoadedAsync();

            if (isSolutionLoaded)
            {
                await HandleOpenSolutionAsync(cancellationToken);
            }

            // Listen for subsequent solution events
            SolutionEvents.OnAfterOpenSolution += HandleOpenSolution;
        }

        private async Task<bool> IsSolutionLoadedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var solService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;

            ErrorHandler.ThrowOnFailure(solService.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out object value));

            return value is bool isSolOpen && isSolOpen;
        }

        private void HandleOpenSolution(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(() => HandleOpenSolutionAsync(DisposalToken)).Task.LogAndForget("StringResourceVisualizer");
        }

        private async Task HandleOpenSolutionAsync(CancellationToken cancellationToken)
        {
            // TODO: handle res files being removed or added to a project - currently will be ignored. Issue #2
            // Get all resource files from the solution
            // Do this now, rather than in adornment manager for performance and to avoid thread issues
            ResourceAdornmentManager.ResourceFiles = await FindResourceFilesAsync(this);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var startTime2 = DateTime.Now;
            IVsFontAndColorStorage storage = (IVsFontAndColorStorage)VSPackage.GetGlobalService(typeof(IVsFontAndColorStorage));

            var guid = new Guid("A27B4E24-A735-4d1d-B8E7-9716E1E3D8E0");

            // Seem like reasonabel defaults as should be visible on light & dark theme
            int _fontSize = 10;
            Color _textColor = Colors.Gray;

            if (storage != null && storage.OpenCategory(ref guid, (uint)(__FCSTORAGEFLAGS.FCSF_READONLY | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS)) == Microsoft.VisualStudio.VSConstants.S_OK)
            {
                LOGFONTW[] Fnt = new LOGFONTW[] { new LOGFONTW() };
                FontInfo[] Info = new FontInfo[] { new FontInfo() };
                storage.GetFont(Fnt, Info);

                _fontSize = Info[0].wPointSize;
            }

            if (storage != null && storage.OpenCategory(ref guid, (uint)(__FCSTORAGEFLAGS.FCSF_NOAUTOCOLORS | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS)) == Microsoft.VisualStudio.VSConstants.S_OK)
            {
                var info = new ColorableItemInfo[1];

                // Get the color value configured for regular string display
                storage.GetItem("String", info);

                var win32Color = (int)info[0].crForeground;

                int r = win32Color & 0x000000FF;
                int g = (win32Color & 0x0000FF00) >> 8;
                int b = (win32Color & 0x00FF0000) >> 16;

                _textColor = Color.FromRgb((byte)r, (byte)g, (byte)b);
            }

            ResourceAdornmentManager.TextSize = _fontSize;
            ResourceAdornmentManager.TextForegroundColor = _textColor;

            if (await this.GetServiceAsync(typeof(DTE)) is DTE dte)
            {
                var plural = ResourceAdornmentManager.ResourceFiles.Count > 1 ? "s" : string.Empty;
                dte.StatusBar.Text = $"String Resource Visualizer initialized with {ResourceAdornmentManager.ResourceFiles.Count} resource file{plural}.";
            }

            var took2 = DateTime.Now - startTime2;
            System.Diagnostics.Trace.WriteLine($"HandleOpenSolutionAsync took {took2.TotalSeconds} seconds");
        }

        private static async Task<List<string>> FindResourceFilesAsync(
            Microsoft.VisualStudio.Shell.IAsyncServiceProvider asyncServiceProvider)
        {
            // Run using the thread pool
            await TaskScheduler.Default;

            // Get the VisualStudioWorkspace from MEF
            var componentModel = await asyncServiceProvider.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            Assumes.NotNull(componentModel);
            var visualStudioWorkspace = componentModel.GetService<VisualStudioWorkspace>();
            Assumes.NotNull(visualStudioWorkspace);

            var resourceFiles = new List<string>();
            foreach (var project in visualStudioWorkspace.CurrentSolution.Projects)
            {
                foreach (var document in project.Documents)
                {

                    var postfix = ".Designer.cs";
                    var filePath = document.FilePath;

                    if (!filePath.EndsWith(postfix))
                    {
                        continue;
                    }

                    var resxFile = filePath.Substring(0, filePath.Length - postfix.Length) + ".resx";
                    if (!File.Exists(resxFile))
                    {
                        continue;
                    }

                    if (Path.GetFileNameWithoutExtension(resxFile).Contains("."))
                    {
                        // Only want neutral language ones, not locale specific versions
                        continue;
                    }

                    Trace.WriteLine($"Found resource file at {resxFile}");
                    resourceFiles.Add(resxFile);
                }
            }

            return resourceFiles;
        }
    }

    static class TaskExtensions
    {
        internal static void LogAndForget(this Task task, string source) =>
            task.ContinueWith((t, s) => VsShellUtilities.LogError(s as string, t.Exception.ToString()),
                source,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                VsTaskLibraryHelper.GetTaskScheduler(VsTaskRunContext.UIThreadNormalPriority));
    }
}
