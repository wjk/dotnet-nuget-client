// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using NuGet.ProjectManagement;
using NuGet.ProjectModel;
using Microsoft.VisualStudio.Shell.Interop;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Provides a method of creating <see cref="CpsPackageReferenceProject"/> instance.
    /// </summary>
    [Export(typeof(IProjectSystemProvider))]
    [Name(nameof(CpsPackageReferenceProjectProvider))]
    [Microsoft.VisualStudio.Utilities.Order(After = nameof(ProjectKNuGetProjectProvider))]
    public class CpsPackageReferenceProjectProvider : IProjectSystemProvider
    {
        private readonly string RestoreProjectStyle = "RestoreProjectStyle";
        private readonly string TargetFramework = "TargetFramework";
        private readonly string TargetFrameworks = "TargetFrameworks";

        private readonly IProjectSystemCache _projectSystemCache;

        [ImportingConstructor]
        public CpsPackageReferenceProjectProvider(IProjectSystemCache projectSystemCache)
        {
            if (projectSystemCache == null)
            {
                throw new ArgumentNullException(nameof(projectSystemCache));
            }

            _projectSystemCache = projectSystemCache;
        }

        public bool TryCreateNuGetProject(EnvDTE.Project dteProject, ProjectSystemProviderContext context, out NuGetProject result)
        {
            if (dteProject == null)
            {
                throw new ArgumentNullException(nameof(dteProject));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            result = null;

            // The project must be an IVsHierarchy.
            var hierarchy = VsHierarchyUtility.ToVsHierarchy(dteProject);
            
            if (hierarchy == null)
            {
                return false;
            }

            // Check if the project is not CPS capable or if it is CPS capable then it does not have TargetFramework(s), if so then return false
            if (!hierarchy.IsCapabilityMatch("CPS"))
            {
                return false;
            }

            var buildPropertyStorage = hierarchy as IVsBuildPropertyStorage;

            // read MSBuild property RestoreProjectStyle which can be set to any NuGet project sytle
            // and pass it on to NugetFactory which can pass it to each NuGet project provider to consume.
            var restoreProjectStyle = VsHierarchyUtility.GetMSBuildProperty(buildPropertyStorage, RestoreProjectStyle);

            var targetFramework = VsHierarchyUtility.GetMSBuildProperty(buildPropertyStorage, TargetFramework);

            var targetFrameworks = VsHierarchyUtility.GetMSBuildProperty(buildPropertyStorage, TargetFrameworks);

            // check for RestoreProjectStyle property is set and if not set to PackageReference then return false
            if (!(string.IsNullOrEmpty(restoreProjectStyle) || 
                restoreProjectStyle.Equals(ProjectStyle.PackageReference.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            // check whether TargetFramework or TargetFrameworks property is set, else return false
            else if (string.IsNullOrEmpty(targetFramework) && string.IsNullOrEmpty(targetFrameworks))
            {
                return false;
            }

            var projectNames = ProjectNames.FromDTEProject(dteProject);
            var fullProjectPath = EnvDTEProjectUtility.GetFullProjectPath(dteProject);
            var unconfiguredProject = GetUnconfiguredProject(dteProject);

            result = new CpsPackageReferenceProject(
                dteProject.Name,
                EnvDTEProjectUtility.GetCustomUniqueName(dteProject),
                fullProjectPath,
                _projectSystemCache,
                dteProject,
                unconfiguredProject,
                VsHierarchyUtility.GetProjectId(dteProject));

            return true;
        }

        private UnconfiguredProject GetUnconfiguredProject(EnvDTE.Project project)
        {
             IVsBrowseObjectContext context = project as IVsBrowseObjectContext;
             if (context == null && project != null)
             { // VC implements this on their DTE.Project.Object
                 context = project.Object as IVsBrowseObjectContext;
             }
             return context != null ? context.UnconfiguredProject : null;
        }
    }
}
