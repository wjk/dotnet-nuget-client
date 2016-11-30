// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using NuGet.Client;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Repositories;
using NuGet.Versioning;

namespace NuGet.Commands
{
    public static class BuildAssetsUtils
    {
        internal static readonly string CrossTargetingCondition = "'$(TargetFramework)' == ''";
        internal static readonly string TargetFrameworkCondition = "'$(TargetFramework)' == '{0}'";
        internal static readonly string ExcludeAllCondition = "'$(ExcludeRestorePackageImports)' != 'true'";

        /// <summary>
        /// The macros that we may use in MSBuild to replace path roots.
        /// </summary>
        public static readonly string[] MacroCandidates = new[]
        {
            "UserProfile", // e.g. C:\users\myusername
        };

        /// <summary>
        /// Write XML to disk.
        /// Delete files which do not have new XML.
        /// </summary>
        public static void WriteFiles(IReadOnlyList<MSBuildOutputFile> files, ILogger log)
        {
            foreach (var file in files)
            {
                if (file.Content == null)
                {
                    // Remove the file if the XML is null
                    FileUtility.Delete(file.Path);
                }
                else
                {
                    log.LogMinimal(string.Format(CultureInfo.CurrentCulture, Strings.Log_GeneratingMsBuildFile, file.Path));

                    // Create the directory if it doesn't exist
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path));

                    // Write out XML file
                    WriteXML(file.Path, file.Content);
                }
            }
        }

        /// <summary>
        /// Create MSBuild targets and props files.
        /// Null will be returned for files that should be removed.
        /// </summary>
        public static IReadOnlyList<MSBuildOutputFile> GenerateMultiTargetFailureFiles(
            string targetsPath,
            string propsPath,
            string repositoryRoot,
            bool success,
            RestoreOutputType restoreType,
            ILogger log,
            CancellationToken token)
        {
            XDocument targetsXML = null;
            XDocument propsXML = null;

            // Create an error file for MSBuild to stop the build.
            targetsXML = GenerateMultiTargetFrameworkWarning(repositoryRoot, restoreType, success);

            if (restoreType == RestoreOutputType.NETCore)
            {
                propsXML = GenerateEmptyImportsFile(repositoryRoot, restoreType, success);
            }

            return new List<MSBuildOutputFile>()
            {
                new MSBuildOutputFile(targetsPath, targetsXML),
                new MSBuildOutputFile(propsPath, propsXML),
            };
        }

        /// <summary>
        /// Create MSBuild targets and props files.
        /// Null will be returned for files that should be removed.
        /// </summary>
        public static IReadOnlyList<MSBuildOutputFile> GenerateFiles(
            string targetsPath,
            string propsPath,
            IList<MSBuildRestoreImportGroup> targets,
            IList<MSBuildRestoreImportGroup> props,
            string repositoryRoot,
            bool success,
            RestoreOutputType restoreType,
            ILogger log)
        {
            XDocument targetsXML = null;
            XDocument propsXML = null;

            // Generate the files as needed for project.json
            // Always generate for NETCore
            if (restoreType == RestoreOutputType.NETCore
                || targets.Any(group => group.Imports.Count > 0))
            {
                targetsXML = GenerateImportsFile(targets, repositoryRoot, restoreType, success);
            }

            if (restoreType == RestoreOutputType.NETCore
                || props.Any(group => group.Imports.Count > 0))
            {
                propsXML = GenerateImportsFile(props, repositoryRoot, restoreType, success);
            }

            return new List<MSBuildOutputFile>()
            {
                new MSBuildOutputFile(targetsPath, targetsXML),
                new MSBuildOutputFile(propsPath, propsXML),
            };
        }

        public static string ReplacePathsWithMacros(string path)
        {
            foreach (var macroName in MacroCandidates)
            {
                string macroValue = Environment.GetEnvironmentVariable(macroName);
                if (!string.IsNullOrEmpty(macroValue)
                    && path.StartsWith(macroValue, StringComparison.OrdinalIgnoreCase))
                {
                    path = $"$({macroName})" + $"{path.Substring(macroValue.Length)}";
                }

                break;
            }

            return path;
        }

        public static XDocument GenerateMultiTargetFrameworkWarning(string repositoryRoot, RestoreOutputType outputType, bool success)
        {
            var doc = GenerateEmptyImportsFile(repositoryRoot, outputType, success);
            var ns = doc.Root.GetDefaultNamespace();

            doc.Root.Add(new XElement(ns + "Target",
                        new XAttribute("Name", "EmitMSBuildWarning"),
                        new XAttribute("BeforeTargets", "Build"),

                        new XElement(ns + "Warning",
                            new XAttribute("Text", Strings.MSBuildWarning_MultiTarget))));

            return doc;
        }

        /// <summary>
        /// Get empty file with the base properties.
        /// </summary>
        public static XDocument GenerateEmptyImportsFile(string repositoryRoot, RestoreOutputType outputType, bool success)
        {
            var projectStyle = "Unknown";

            if (outputType == RestoreOutputType.NETCore)
            {
                projectStyle = "PackageReference";
            }
            else if (outputType == RestoreOutputType.UAP)
            {
                projectStyle = "ProjectJson";
            }

            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "no"),

                new XElement(ns + "Project",
                    new XAttribute("ToolsVersion", "14.0"),

                    new XElement(ns + "PropertyGroup",
                        GetProperty(ns, "NuGetPackageRoot", ReplacePathsWithMacros(repositoryRoot)),
                        GetProperty(ns, "NuGetProjectStyle", projectStyle),
                        GetProperty(ns, "NuGetToolVersion", MinClientVersionUtility.GetNuGetClientVersion().ToNormalizedString()),
                        GetProperty(ns, "NuGetRestoreSuccess", success.ToString()))));

            return doc;
        }

        public static XElement GetProperty(XNamespace ns, string propertyName, string content)
        {
            return new XElement(ns + propertyName,
                            new XAttribute("Condition", $" '$({propertyName})' == '' "),
                            content);
        }

        public static XDocument GenerateImportsFile(IList<MSBuildRestoreImportGroup> groups,
            string repositoryRoot,
            RestoreOutputType outputType,
            bool success)
        {
            var doc = GenerateEmptyImportsFile(repositoryRoot, outputType, success);
            var ns = doc.Root.GetDefaultNamespace();

            // Add import groups, order by position, then by the conditions to keep the results deterministic
            // Skip empty groups
            foreach (var group in groups
                .Where(e => e.Imports.Count > 0)
                .OrderBy(e => e.Position)
                .ThenBy(e => e.Condition, StringComparer.OrdinalIgnoreCase))
            {
                var itemGroup = new XElement(ns + "ImportGroup", group.Imports.Select(i =>
                            new XElement(ns + "Import",
                                new XAttribute("Project", GetImportPath(i, repositoryRoot)),
                                new XAttribute("Condition", $"Exists('{GetImportPath(i, repositoryRoot)}')"))));

                // Add a conditional statement if multiple TFMs exist or cross targeting is present
                var conditionValue = group.Condition;
                if (!string.IsNullOrEmpty(conditionValue))
                {
                    itemGroup.Add(new XAttribute("Condition", conditionValue));
                }

                // Add itemgroup to file
                doc.Root.Add(itemGroup);
            }

            return doc;
        }

        public static void WriteXML(string path, XDocument doc)
        {
            FileUtility.Replace((outputPath) =>
            {
                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    doc.Save(output);
                }
            },
            path);
        }

        public static string GetImportPath(string importPath, string repositoryRoot)
        {
            var path = importPath;

            if (importPath.StartsWith(repositoryRoot, StringComparison.Ordinal))
            {
                path = $"$(NuGetPackageRoot){importPath.Substring(repositoryRoot.Length)}";
            }
            else
            {
                path = ReplacePathsWithMacros(importPath);
            }

            return path;
        }

        /// <summary>
        /// Check if the file has changes compared to the original on disk.
        /// </summary>
        public static bool HasChanges(XDocument newFile, string path, ILogger log)
        {
            XDocument existing = ReadExisting(path, log);

            if (existing != null)
            {
                // Use a simple string compare to check if the files match
                // This can be optimized in the future, but generally these are very small files.
                return !newFile.ToString().Equals(existing.ToString(), StringComparison.Ordinal);
            }

            return true;
        }

        public static XDocument ReadExisting(string path, ILogger log)
        {
            XDocument result = null;

            if (File.Exists(path))
            {
                try
                {
                    using (var output = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        result = XDocument.Load(output);
                    }
                }
                catch (Exception ex)
                {
                    // Log a debug message and ignore, this will force an overwrite
                    log.LogDebug($"Failed to open imports file: {path} Error: {ex.Message}");
                }
            }

            return result;
        }

        internal static MSBuildRestoreResult RestoreMSBuildFiles(PackageSpec project,
            IEnumerable<RestoreTargetGraph> targetGraphs,
            IReadOnlyList<NuGetv3LocalRepository> repositories,
            RemoteWalkContext context,
            RestoreRequest request,
            Dictionary<RestoreTargetGraph, Dictionary<string, LibraryIncludeFlags>> includeFlagGraphs)
        {
            var targetsPath = Path.Combine(request.RestoreOutputPath, $"{project.Name}.nuget.targets");
            var propsPath = Path.Combine(request.RestoreOutputPath, $"{project.Name}.nuget.props");

            if (request.RestoreOutputType == RestoreOutputType.NETCore)
            {
                var projFileName = Path.GetFileName(request.Project.RestoreMetadata.ProjectPath);

                targetsPath = Path.Combine(request.RestoreOutputPath, $"{projFileName}.nuget.g.targets");
                propsPath = Path.Combine(request.RestoreOutputPath, $"{projFileName}.nuget.g.props");
            }

            // Non-Msbuild projects should skip targets and treat it as success
            if (!context.IsMsBuildBased && !ForceWriteTargets())
            {
                return new MSBuildRestoreResult(targetsPath, propsPath, success: true);
            }

            // Invalid msbuild projects should write out an msbuild error target
            if (!targetGraphs.Any())
            {
                return new MSBuildRestoreResult(targetsPath, propsPath, success: false);
            }

            // Framework -> (targets, props)
            var buildAssetsByFramework = new Dictionary<NuGetFramework, TargetsAndProps>();

            // Get assets for each framework
            foreach (var projectFramework in project.TargetFrameworks.Select(f => f.FrameworkName))
            {
                var targetsAndProps =
                    GetTargetsAndPropsForFramework(
                        targetGraphs,
                        repositories,
                        context,
                        request,
                        includeFlagGraphs,
                        projectFramework);

                buildAssetsByFramework.Add(projectFramework, targetsAndProps);
            }

            var props = new List<MSBuildRestoreImportGroup>();
            var targets = new List<MSBuildRestoreImportGroup>();

            // Conditionals for targets and props are only supported by NETCore
            if (project.RestoreMetadata?.OutputType == RestoreOutputType.NETCore)
            {
                // Add additional conditionals for cross targeting
                var isCrossTargeting = request.Project.RestoreMetadata.CrossTargeting
                    || request.Project.TargetFrameworks.Count > 1;

                Debug.Assert((!request.Project.RestoreMetadata.CrossTargeting && (request.Project.TargetFrameworks.Count < 2)
                    || (request.Project.RestoreMetadata.CrossTargeting)),
                    "Invalid crosstargeting and framework count combination");

                if (isCrossTargeting)
                {
                    // Find all global targets from buildCrossTargeting
                    var crossTargetingAssets = GetTargetsAndPropsForCrossTargeting(
                            targetGraphs,
                            repositories,
                            context,
                            request,
                            includeFlagGraphs);

                    var crossProps = new MSBuildRestoreImportGroup();
                    crossProps.Position = 0;
                    crossProps.Conditions.Add(CrossTargetingCondition);
                    crossProps.Imports.AddRange(crossTargetingAssets.Props);
                    props.Add(crossProps);

                    var crossTargets = new MSBuildRestoreImportGroup();
                    crossTargets.Position = 0;
                    crossTargets.Conditions.Add(CrossTargetingCondition);
                    crossTargets.Imports.AddRange(crossTargetingAssets.Targets);
                    targets.Add(crossTargets);
                }

                // Find TFM specific assets from the build folder
                foreach (var pair in buildAssetsByFramework)
                {
                    // There could be multiple string matches
                    foreach (var match in GetMatchingFrameworkStrings(project, pair.Key))
                    {
                        var frameworkCondition = string.Format(CultureInfo.InvariantCulture, TargetFrameworkCondition, match);

                        // Add entries regardless of if imports exist,
                        // this is needed to trigger conditionals
                        var propsGroup = new MSBuildRestoreImportGroup();

                        if (isCrossTargeting)
                        {
                            propsGroup.Conditions.Add(frameworkCondition);
                        }

                        propsGroup.Imports.AddRange(pair.Value.Props);
                        propsGroup.Position = 1;
                        props.Add(propsGroup);

                        var targetsGroup = new MSBuildRestoreImportGroup();

                        if (isCrossTargeting)
                        {
                            targetsGroup.Conditions.Add(frameworkCondition);
                        }

                        targetsGroup.Imports.AddRange(pair.Value.Targets);
                        targetsGroup.Position = 1;
                        targets.Add(targetsGroup);
                    }
                }
            }
            else
            {
                // Copy targets and props over, there can only be 1 tfm here
                // No conditionals are added
                var targetsAndProps = buildAssetsByFramework.First();

                var propsGroup = new MSBuildRestoreImportGroup();
                propsGroup.Imports.AddRange(targetsAndProps.Value.Props);
                props.Add(propsGroup);

                var targetsGroup = new MSBuildRestoreImportGroup();
                targetsGroup.Imports.AddRange(targetsAndProps.Value.Targets);
                targets.Add(targetsGroup);
            }

            // Add exclude all condition to all groups
            foreach (var group in props.Concat(targets))
            {
                group.Conditions.Add(ExcludeAllCondition);
            }

            // Targets files contain a macro for the repository root. If only the user package folder was used
            // allow a replacement. If fallback folders were used the macro cannot be applied.
            // Do not use macros for fallback folders. Use only the first repository which is the user folder.
            var repositoryRoot = repositories.First().RepositoryRoot;

            // Create a result which may be committed to disk later.
            return new MSBuildRestoreResult(
                targetsPath,
                propsPath,
                repositoryRoot,
                props,
                targets);
        }

        private static HashSet<string> GetMatchingFrameworkStrings(PackageSpec spec, NuGetFramework framework)
        {
            // Ignore case since msbuild does
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            matches.UnionWith(spec.RestoreMetadata.OriginalTargetFrameworks
                .Where(s => framework.Equals(NuGetFramework.Parse(s))));

            // If there were no matches, use the generated name
            if (matches.Count < 1)
            {
                matches.Add(framework.GetShortFolderName());
            }

            return matches;
        }

        private static TargetsAndProps GetTargetsAndPropsForFramework(
            IEnumerable<RestoreTargetGraph> targetGraphs,
            IReadOnlyList<NuGetv3LocalRepository> repositories,
            RemoteWalkContext context,
            RestoreRequest request,
            Dictionary<RestoreTargetGraph,
            Dictionary<string, LibraryIncludeFlags>> includeFlagGraphs,
            NuGetFramework projectFramework)
        {
            var result = new TargetsAndProps();

            // Skip runtime graphs, msbuild targets may not come from RID specific packages
            var graph = targetGraphs
                .Single(g => string.IsNullOrEmpty(g.RuntimeIdentifier) && g.Framework.Equals(projectFramework));

            // Gather props and targets to write out
            var buildGroupSets = GetMSBuildAssets(
                context,
                graph,
                request.Project,
                includeFlagGraphs,
                graph.Conventions.Patterns.MSBuildFiles);

            // Second find the nearest group for each framework
            foreach (var buildGroupSetsEntry in buildGroupSets)
            {
                var libraryIdentity = buildGroupSetsEntry.Key;
                var buildGroupSet = buildGroupSetsEntry.Value;

                // Find the nearest msbuild group, this can include the root level Any group.
                var buildItems = NuGetFrameworkUtility.GetNearest(
                        buildGroupSet,
                        graph.Framework,
                        group =>
                            group.Properties[ManagedCodeConventions.PropertyNames.TargetFrameworkMoniker]
                                as NuGetFramework);

                // Check if compatible build assets exist
                if (buildItems != null)
                {
                    AddPropsAndTargets(repositories, libraryIdentity, buildItems, result);
                }
            }

            return result;
        }

        private static TargetsAndProps GetTargetsAndPropsForCrossTargeting(
            IEnumerable<RestoreTargetGraph> targetGraphs,
            IReadOnlyList<NuGetv3LocalRepository> repositories,
            RemoteWalkContext context,
            RestoreRequest request,
            Dictionary<RestoreTargetGraph,
            Dictionary<string, LibraryIncludeFlags>> includeFlagGraphs)
        {
            var result = new TargetsAndProps();

            // Skip runtime graphs, msbuild targets may not come from RID specific packages
            // Order the graphs by framework to make this deterministic for scenarios where
            // TFMs disagree on the dependency order, there is little that can be done for
            // conflicts where A->B for TFM1 and B->A for TFM2.
            var ridlessGraphs = targetGraphs
                .Where(g => string.IsNullOrEmpty(g.RuntimeIdentifier))
                .OrderBy(g => g.Framework, new NuGetFrameworkSorter());

            // Gather props and targets to write out
            foreach (var graph in ridlessGraphs)
            {
                var globalGroupSets = GetMSBuildAssets(
                    context,
                    graph,
                    request.Project,
                    includeFlagGraphs,
                    graph.Conventions.Patterns.MSBuildCrossTargetingFiles);

                // Check if compatible build assets exist
                foreach (var globalGroupEntry in globalGroupSets)
                {
                    var libraryIdentity = globalGroupEntry.Key;
                    var buildGroupSet = globalGroupEntry.Value;

                    Debug.Assert(buildGroupSet.Length < 2, "Unexpected number of build global asset groups");

                    // There can only be one group since there are no TFMs here.
                    if (buildGroupSet.Length == 1)
                    {
                        // Add all targets and props from buildCrossTargeting
                        // Note: AddPropsAndTargets handles de-duping file paths. Since these non-TFM specific
                        // files are found for every TFM it is likely that there will be duplicates going in.
                        AddPropsAndTargets(
                                repositories,
                                libraryIdentity,
                                buildGroupSet[0],
                                result);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Check if NUGET_XPROJ_WRITE_TARGETS is true.
        /// </summary>
        private static bool ForceWriteTargets()
        {
            var envVar = Environment.GetEnvironmentVariable("NUGET_XPROJ_WRITE_TARGETS");

            bool forceWriteTargets = false;
            if (!string.IsNullOrEmpty(envVar))
            {
                Boolean.TryParse(envVar, out forceWriteTargets);
            }

            return forceWriteTargets;
        }

        /// <summary>
        /// Find all included msbuild assets for a graph.
        /// </summary>
        private static Dictionary<PackageIdentity, ContentItemGroup[]> GetMSBuildAssets(
            RemoteWalkContext context,
            RestoreTargetGraph graph,
            PackageSpec project,
            Dictionary<RestoreTargetGraph, Dictionary<string, LibraryIncludeFlags>> includeFlagGraphs,
            PatternSet patternSet)
        {
            var buildGroupSets = new Dictionary<PackageIdentity, ContentItemGroup[]>();

            var flattenedFlags = IncludeFlagUtils.FlattenDependencyTypes(includeFlagGraphs, project, graph);

            // convert graph items to package dependency info list
            var dependencies = ConvertToPackageDependencyInfo(graph.Flattened);

            // sort graph nodes by dependencies order
            var sortedItems = TopologicalSortUtility.SortPackagesByDependencyOrder(dependencies);

            // First find all msbuild items in the packages
            foreach (var library in sortedItems)
            {
                var includeLibrary = true;

                LibraryIncludeFlags libraryFlags;
                if (flattenedFlags.TryGetValue(library.Id, out libraryFlags))
                {
                    includeLibrary = libraryFlags.HasFlag(LibraryIncludeFlags.Build);
                }

                // Skip libraries that do not include build files such as transitive packages
                if (includeLibrary)
                {
                    var packageIdentity = new PackageIdentity(library.Id, library.Version);
                    IList<string> packageFiles;
                    context.PackageFileCache.TryGetValue(packageIdentity, out packageFiles);

                    if (packageFiles != null)
                    {
                        var contentItemCollection = new ContentItemCollection();
                        contentItemCollection.Load(packageFiles);

                        // Find MSBuild groups
                        var buildGroupSet = contentItemCollection
                            .FindItemGroups(patternSet)
                            .ToArray();

                        buildGroupSets.Add(packageIdentity, buildGroupSet);
                    }
                }
            }

            return buildGroupSets;
        }

        private static HashSet<PackageDependencyInfo> ConvertToPackageDependencyInfo(
            ISet<GraphItem<RemoteResolveResult>> items)
        {
            var result = new HashSet<PackageDependencyInfo>(PackageIdentity.Comparer);

            foreach (var item in items)
            {
                var dependencies =
                    item.Data?.Dependencies?.Select(
                        dependency => new PackageDependency(dependency.Name, VersionRange.All));

                result.Add(new PackageDependencyInfo(item.Key.Name, item.Key.Version, dependencies));
            }

            return result;
        }

        /// <summary>
        /// Add all valid targets and props to the passed in lists.
        /// Modifies targetsAndProps
        /// </summary>
        private static void AddPropsAndTargets(
            IReadOnlyList<NuGetv3LocalRepository> repositories,
            PackageIdentity libraryIdentity,
            ContentItemGroup buildItems,
            TargetsAndProps targetsAndProps)
        {
            // We need to additionally filter to items that are named "{packageId}.targets" and "{packageId}.props"
            // Filter by file name here and we'll filter by extension when we add things to the lists.
            var items = buildItems.Items
                .Where(item =>
                    Path.GetFileNameWithoutExtension(item.Path)
                    .Equals(libraryIdentity.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var packageInfo = NuGetv3LocalRepositoryUtility.GetPackage(repositories, libraryIdentity.Id, libraryIdentity.Version);
            var pathResolver = packageInfo.Repository.PathResolver;

            var targets = items
                .Where(c => Path.GetExtension(c.Path).Equals(".targets", StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                    Path.Combine(pathResolver.GetInstallPath(libraryIdentity.Id, libraryIdentity.Version),
                    c.Path.Replace('/', Path.DirectorySeparatorChar)));

            // avoid duplicate targets
            foreach (var target in targets)
            {
                if (!targetsAndProps.Targets.Contains(target, StringComparer.Ordinal))
                {
                    targetsAndProps.Targets.Add(target);
                }
            }

            var props = items
                .Where(c => Path.GetExtension(c.Path).Equals(".props", StringComparison.OrdinalIgnoreCase))
                .Select(c =>
                    Path.Combine(pathResolver.GetInstallPath(libraryIdentity.Id, libraryIdentity.Version),
                    c.Path.Replace('/', Path.DirectorySeparatorChar)));

            foreach (var prop in props)
            {
                // avoid duplicate props
                if (!targetsAndProps.Props.Contains(prop, StringComparer.Ordinal))
                {
                    targetsAndProps.Props.Add(prop);
                }
            }
        }

        private class TargetsAndProps
        {
            public List<string> Targets { get; } = new List<string>();

            public List<string> Props { get; } = new List<string>();
        }
    }
}