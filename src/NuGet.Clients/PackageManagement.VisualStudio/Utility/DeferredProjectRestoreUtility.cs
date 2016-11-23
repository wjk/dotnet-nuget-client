// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.ProjectModel;
using System.Threading;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Utility class to construct restore data for deferred projects.
    /// </summary>
    public static class DeferredProjectRestoreUtility
    {
        public static async Task<DeferredProjectRestoreData> GetDeferredProjectsData(
            IDeferredProjectWorkspaceService deferredWorkspaceService,
            IEnumerable<string> deferredProjectsPath,
            CancellationToken token)
        {
            var packageReferencesDict = new Dictionary<PackageReference, List<string>>(new PackageReferenceComparer());
            var packageSpecs = new List<PackageSpec>();

            foreach (var projectPath in deferredProjectsPath)
            {
                // packages.config
                string packagesConfigFilePath = Path.Combine(Path.GetDirectoryName(projectPath), "packages.config");
                bool packagesConfigFileExists = await deferredWorkspaceService.EntityExists(packagesConfigFilePath);

                if (packagesConfigFileExists)
                {
                    // read packages.config and get all package references.
                    var projectName = Path.GetFileNameWithoutExtension(projectPath);
                    using (var stream = new FileStream(packagesConfigFilePath, FileMode.Open, FileAccess.Read))
                    {
                        var reader = new PackagesConfigReader(stream);
                        var packageReferences = reader.GetPackages();

                        foreach (var packageRef in packageReferences)
                        {
                            List<string> projectNames = null;
                            if (!packageReferencesDict.TryGetValue(packageRef, out projectNames))
                            {
                                projectNames = new List<string>();
                                packageReferencesDict.Add(packageRef, projectNames);
                            }

                            projectNames.Add(projectName);
                        }
                    }

                    // create package spec for packages.config based project
                    var packageSpec = await GetPackageSpecForPackagesConfigAsync(deferredWorkspaceService, projectPath);
                    if (packageSpec != null)
                    {
                        packageSpecs.Add(packageSpec);
                    }
                }
                else
                {

                    // project.json
                    string projectJsonFilePath = Path.Combine(Path.GetDirectoryName(projectPath), "project.json");
                    bool projectJsonFileExists = await deferredWorkspaceService.EntityExists(projectJsonFilePath);

                    if (projectJsonFileExists)
                    {
                        // create package spec for project.json based project
                        var packageSpec = await GetPackageSpecForProjectJsonAsync(deferredWorkspaceService, projectPath, projectJsonFilePath);
                        packageSpecs.Add(packageSpec);
                    }
                    else
                    {
                        // TODO: https://github.com/NuGet/Home/issues/4003
                        // package references (CPS or Legacy CSProj)
                        /*var packageRefsDict = await deferredWorkspaceService.GetPackageReferencesAsync(projectPath, token);

                        if (packageRefsDict.Count > 0)
                        {
                            var packageSpec = await GetPackageSpecForPackageReferencesAsync(deferredWorkspaceService, projectPath);
                            packageSpecs.Add(packageSpec);
                        }*/
                    }
                }

            }
            
            return new DeferredProjectRestoreData(packageReferencesDict, packageSpecs);
        }

        private static async Task<PackageSpec> GetPackageSpecForPackagesConfigAsync(IDeferredProjectWorkspaceService deferredWorkspaceService, string projectPath)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var msbuildProject = EnvDTEProjectUtility.AsMicrosoftBuildEvaluationProject(projectPath);
            var targetFrameworkString = MSBuildProjectUtility.GetTargetFrameworkString(msbuildProject);

            if (targetFrameworkString == null)
            {
                return null;
            }

            var nuGetFramework = new NuGetFramework(targetFrameworkString);

            var packageSpec = new PackageSpec(
                new List<TargetFrameworkInformation>
                {
                    new TargetFrameworkInformation
                    {
                        FrameworkName = nuGetFramework
                    }
                });

            packageSpec.Name = projectName;
            packageSpec.FilePath = projectPath;

            var metadata = new ProjectRestoreMetadata();
            packageSpec.RestoreMetadata = metadata;

            metadata.OutputType = RestoreOutputType.PackagesConfig;
            metadata.ProjectPath = projectPath;
            metadata.ProjectName = projectName;
            metadata.ProjectUniqueName = projectPath;
            metadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(nuGetFramework));

            await AddProjectReferencesAsync(deferredWorkspaceService, metadata, projectPath);

            return packageSpec;
        }

        private static async Task<PackageSpec> GetPackageSpecForProjectJsonAsync(
            IDeferredProjectWorkspaceService deferredWorkspaceService,
            string projectPath,
            string projectJsonFilePath)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var packageSpec = JsonPackageSpecReader.GetPackageSpec(projectName, projectJsonFilePath);

            var metadata = new ProjectRestoreMetadata();
            packageSpec.RestoreMetadata = metadata;

            metadata.OutputType = RestoreOutputType.UAP;
            metadata.ProjectPath = projectPath;
            metadata.ProjectJsonPath = packageSpec.FilePath;
            metadata.ProjectName = packageSpec.Name;
            metadata.ProjectUniqueName = projectPath;

            foreach (var framework in packageSpec.TargetFrameworks.Select(e => e.FrameworkName))
            {
                metadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(framework));
            }

            await AddProjectReferencesAsync(deferredWorkspaceService, metadata, projectPath);

            return packageSpec;
        }

        /*private static async Task<PackageSpec> GetPackageSpecForPackageReferencesAsync(
            IDeferredProjectWorkspaceService deferredWorkspaceService, 
            string projectPath)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var msbuildProject = EnvDTEProjectUtility.AsMicrosoftBuildEvaluationProject(projectPath);
            var targetFrameworkString = MSBuildProjectUtility.GetTargetFrameworkString(msbuildProject);

            if (targetFrameworkString == null)
            {
                return null;
            }

            var targetFrameworks = targetFrameworkString.Split(new[] { ';' });

            var tfis = targetFrameworks.Select(tfm => new NuGetFramework(tfm)).
                Select(nFramework => new TargetFrameworkInformation
                {
                    FrameworkName = nFramework
                }).ToArray();

            var packageSpec = new PackageSpec(tfis);
            {
                Name = projectName,
                FilePath = projectPath,
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    ProjectName = projectName,
                    ProjectUniqueName = projectPath,
                    ProjectPath = projectPath,
                    OutputPath = Path.GetFullPath(
                        Path.Combine(
                            projectDirectory,
                            projectRestoreInfo.BaseIntermediatePath)),
                    OutputType = RestoreOutputType.NETCore,
                    TargetFrameworks = projectRestoreInfo.TargetFrameworks
                        .Cast<IVsTargetFrameworkInfo>()
                        .Select(item => ToProjectRestoreMetadataFrameworkInfo(item, projectDirectory))
                        .ToList(),
                    OriginalTargetFrameworks = originalTargetFrameworks,
                    CrossTargeting = crossTargeting
                },
                RuntimeGraph = GetRuntimeGraph(projectRestoreInfo)
            };

            packageSpec.Name = projectName;
            packageSpec.FilePath = projectPath;

            var metadata = new ProjectRestoreMetadata();
            packageSpec.RestoreMetadata = metadata;

            metadata.OutputType = RestoreOutputType.PackagesConfig;
            metadata.ProjectPath = projectPath;
            metadata.ProjectName = projectName;
            metadata.ProjectUniqueName = projectPath;
            //metadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(nuGetFramework));

            await AddProjectReferencesAsync(deferredWorkspaceService, metadata, projectPath);

            return packageSpec;
        }*/

        private static async Task AddProjectReferencesAsync(
            IDeferredProjectWorkspaceService deferredWorkspaceService,
            ProjectRestoreMetadata metadata,
            string projectPath)
        {
            var references = await deferredWorkspaceService.GetProjectReferencesAsync(projectPath);

            foreach (var reference in references)
            {
                var restoreReference = new ProjectRestoreReference()
                {
                    ProjectPath = reference,
                    ProjectUniqueName = reference
                };

                foreach (var frameworkInfo in metadata.TargetFrameworks)
                {
                    frameworkInfo.ProjectReferences.Add(restoreReference);
                }
            }
        }
    }
}
